using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public sealed class UpdateFile
{
    public string RelativePath { get; set; }
    public string Sha256 { get; set; }
    public long Length { get; set; }
}

public sealed class UpdatePlan
{
    public UpdatePlan() { SchemaVersion=1; Files=new List<UpdateFile>(); Restart=true; }
    public int SchemaVersion { get; set; }
    public string InstallDirectory { get; set; }
    public string PreparedDirectory { get; set; }
    public string Version { get; set; }
    public List<UpdateFile> Files { get; set; }
    public int TrainerPid { get; set; }
    public long TrainerStartUtcTicks { get; set; }
    public bool Restart { get; set; }
}

internal static class UpdateInstallerCore
{
    internal static readonly string[] AllowedFiles={"Azeroth-Speed.exe","AzerothSpeedHook.dll","Azeroth-Speed-Updater.exe","build-info.json","README.md","LICENSE","RELEASE_NOTES.md","SHA256SUMS.txt","docs/full-preview.png","docs/mini-preview.png"};
    private static readonly string[] RequiredFiles={"Azeroth-Speed.exe","AzerothSpeedHook.dll","Azeroth-Speed-Updater.exe"};
    private sealed class Entry { internal string Relative,Source,Destination,Temporary,Backup; internal UpdateFile File; internal bool Existed; }

    internal static bool IsWithin(string path,string directory)
    {
        return String.Equals(path,directory,StringComparison.OrdinalIgnoreCase) || path.StartsWith(directory.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
    }
    internal static string FullDirectory(string path)
    {
        if(String.IsNullOrWhiteSpace(path) || path.Length>32760 || path.IndexOf('/')>=0 || path.StartsWith("\\\\?\\",StringComparison.Ordinal) || path.StartsWith("\\\\.\\",StringComparison.Ordinal))
            throw new InvalidDataException("The update directory is not a normal absolute Windows path.");
        bool drive=path.Length>3 && Char.IsLetter(path[0]) && path[1]==':' && path[2]=='\\';
        bool unc=path.StartsWith("\\\\",StringComparison.Ordinal);
        if(!drive && !unc) throw new InvalidDataException("The update directory must be absolute.");
        if(path.IndexOf(':',drive ? 2 : 0)>=0) throw new InvalidDataException("Alternate data stream paths are not allowed.");
        string full=Path.GetFullPath(path).TrimEnd('\\');
        string root=Path.GetPathRoot(full).TrimEnd('\\');
        if(String.Equals(full,root,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A drive or share root cannot be an update directory.");
        foreach(string special in new[]{Environment.GetFolderPath(Environment.SpecialFolder.Windows),Environment.GetFolderPath(Environment.SpecialFolder.System),Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)})
        {
            if(!String.IsNullOrEmpty(special) && IsWithin(full,Path.GetFullPath(special).TrimEnd('\\'))) throw new InvalidDataException("System directories cannot be updated.");
        }
        AssertNoReparse(full);
        return full;
    }
    internal static void AssertNoReparse(string path)
    {
        string current=Path.GetFullPath(path);
        for(;;)
        {
            try { if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Update paths cannot contain symbolic links or junctions."); }
            catch(FileNotFoundException) { }
            catch(DirectoryNotFoundException) { }
            string parent=Path.GetDirectoryName(current.TrimEnd('\\'));
            if(String.IsNullOrEmpty(parent) || String.Equals(parent,current,StringComparison.OrdinalIgnoreCase)) break;
            current=parent;
        }
    }
    private static string NormalizeRelative(string name)
    {
        if(String.IsNullOrEmpty(name) || name.Length>200) throw new InvalidDataException("Invalid update filename.");
        string normalized=name.Replace('\\','/');
        if(!AllowedFiles.Contains(normalized,StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected update file: "+name);
        // Return the fixed spelling so aliases cannot introduce extra filenames.
        return AllowedFiles.First(n=>String.Equals(n,normalized,StringComparison.OrdinalIgnoreCase));
    }
    private static string Child(string directory,string relative)
    {
        string full=Path.GetFullPath(Path.Combine(directory,relative.Replace('/','\\')));
        if(!IsWithin(full,directory) || String.Equals(full,directory,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An update file escaped its directory.");
        AssertNoReparse(full);
        return full;
    }
    private static Version ReleaseVersion(string text)
    {
        Version value;
        if(String.IsNullOrEmpty(text) || !Version.TryParse(text,out value) || value.Major<0 || value.Minor<0 || value.Build<0 || value.Revision>=0 || value.ToString(3)!=text)
            throw new InvalidDataException("The update version must have three numeric components.");
        return new Version(value.Major,value.Minor,value.Build,0);
    }
    private static Version FileVersion(string path)
    {
        var info=FileVersionInfo.GetVersionInfo(path);
        if(String.IsNullOrEmpty(info.FileVersion)) throw new InvalidDataException("The trainer executable has no file version.");
        return new Version(info.FileMajorPart,info.FileMinorPart,info.FileBuildPart,info.FilePrivatePart);
    }
    private static void Verify(UpdateFile item,string file)
    {
        AssertNoReparse(file);
        if(item.Length<0 || item.Length>512L*1024*1024 || item.Sha256==null || item.Sha256.Length!=64 || item.Sha256.Any(c=>!Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid update checksum or size.");
        using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var sha=SHA256.Create())
        {
            if(input.Length!=item.Length || !String.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""),item.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update checksum mismatch: "+item.RelativePath);
        }
    }
    private static List<Entry> Validate(UpdatePlan plan)
    {
        if(plan==null || plan.SchemaVersion!=1 || plan.Files==null || plan.Files.Count<3 || plan.Files.Count>AllowedFiles.Length) throw new InvalidDataException("Invalid update plan.");
        string install=FullDirectory(plan.InstallDirectory),prepared=FullDirectory(plan.PreparedDirectory);
        if(IsWithin(install,prepared) || IsWithin(prepared,install)) throw new InvalidDataException("Prepared files must be outside the installation directory.");
        if(!Directory.Exists(install) || !Directory.Exists(prepared)) throw new DirectoryNotFoundException("The update directory no longer exists.");
        Version requested=ReleaseVersion(plan.Version);
        string installedExe=Child(install,"Azeroth-Speed.exe");
        if(!File.Exists(installedExe)) throw new InvalidDataException("The existing trainer executable was not found.");
        var entries=new List<Entry>(); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in plan.Files)
        {
            if(item==null) throw new InvalidDataException("Invalid update file entry.");
            string relative=NormalizeRelative(item.RelativePath);
            if(!seen.Add(relative)) throw new InvalidDataException("Duplicate update filename.");
            string source=Child(prepared,relative),destination=Child(install,relative);
            if(Directory.Exists(source) || Directory.Exists(destination)) throw new InvalidDataException("An update file is a directory.");
            Verify(item,source);
            entries.Add(new Entry {Relative=relative,Source=source,Destination=destination,File=item});
        }
        foreach(string required in RequiredFiles) if(!seen.Contains(required)) throw new InvalidDataException("The update is missing "+required+".");
        string newExe=entries.First(e=>e.Relative=="Azeroth-Speed.exe").Source;
        if(FileVersion(newExe)!=requested) throw new InvalidDataException("The staged trainer version does not match the update plan.");
        if(requested<=FileVersion(installedExe)) throw new InvalidDataException("Only a newer trainer version can be installed.");
        return entries;
    }
    internal static void ValidatePlan(UpdatePlan plan) { Validate(plan); }
    private static void CopyVerified(Entry item)
    {
        Verify(item.File,item.Source);
        AssertNoReparse(item.Temporary);
        using(var input=new FileStream(item.Source,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var output=new FileStream(item.Temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        using(var sha=SHA256.Create())
        {
            long count=0; var buffer=new byte[81920]; int size;
            while((size=input.Read(buffer,0,buffer.Length))!=0) { output.Write(buffer,0,size); sha.TransformBlock(buffer,0,size,buffer,0); count+=size; }
            sha.TransformFinalBlock(new byte[0],0,0);
            output.Flush(true);
            if(count!=item.File.Length || !String.Equals(BitConverter.ToString(sha.Hash).Replace("-",""),item.File.Sha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Staged update changed while copying.");
        }
    }
    private static void CreateParents(string file,string root)
    {
        string parent=Path.GetDirectoryName(file);
        if(!IsWithin(parent,root)) throw new InvalidDataException("Invalid transaction path.");
        AssertNoReparse(parent); Directory.CreateDirectory(parent); AssertNoReparse(parent);
    }
    private static void CleanOwnedTree(string directory,string install)
    {
        if(String.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        string full=Path.GetFullPath(directory),parent=Path.GetDirectoryName(full),name=Path.GetFileName(full);
        string suffix=name.StartsWith(".azs-update-",StringComparison.Ordinal) ? name.Substring(12) : name.StartsWith(".azs-backup-",StringComparison.Ordinal) ? name.Substring(12) : "";
        Guid parsed;
        if(!String.Equals(parent,install,StringComparison.OrdinalIgnoreCase) || suffix.Length!=32 || !Guid.TryParseExact(suffix,"N",out parsed)) throw new InvalidDataException("Refusing to clean an unowned directory.");
        CleanTreeContents(full,full);
    }
    private static void CleanTreeContents(string path,string ownedRoot)
    {
        if(!IsWithin(Path.GetFullPath(path),ownedRoot)) throw new InvalidDataException("Cleanup escaped its transaction directory.");
        AssertNoReparse(path);
        foreach(string file in Directory.GetFiles(path)) { AssertNoReparse(file); File.Delete(file); }
        foreach(string child in Directory.GetDirectories(path)) CleanTreeContents(child,ownedRoot);
        Directory.Delete(path,false);
    }
    internal static void Apply(UpdatePlan plan) { Apply(plan,null); }
    internal static void Apply(UpdatePlan plan,Action<int> faultAfterWrite)
    {
        List<Entry> entries=Validate(plan);
        string install=FullDirectory(plan.InstallDirectory);
        string temporary=Path.Combine(install,".azs-update-"+Guid.NewGuid().ToString("N"));
        string backup=Path.Combine(install,".azs-backup-"+Guid.NewGuid().ToString("N"));
        if(Directory.Exists(temporary) || Directory.Exists(backup)) throw new IOException("Transaction directory already exists.");
        Directory.CreateDirectory(temporary); Directory.CreateDirectory(backup);
        var attempted=new List<Entry>(); bool rollbackFailed=false;
        try
        {
            foreach(var item in entries)
            {
                item.Temporary=Child(temporary,item.Relative); item.Backup=Child(backup,item.Relative);
                CreateParents(item.Temporary,temporary); CreateParents(item.Backup,backup);
                CopyVerified(item);
                item.Existed=File.Exists(item.Destination);
                if(item.Existed) { AssertNoReparse(item.Destination); File.Copy(item.Destination,item.Backup,false); }
            }
            // Recheck versions after staging and before changing installed files.
            ValidatePlan(plan);
            int writes=0;
            foreach(var item in entries)
            {
                AssertNoReparse(install); AssertNoReparse(item.Destination); AssertNoReparse(item.Temporary);
                CreateParents(item.Destination,install);
                attempted.Add(item);
                if(item.Existed) File.Replace(item.Temporary,item.Destination,null,true);
                else File.Move(item.Temporary,item.Destination);
                if(faultAfterWrite!=null) faultAfterWrite(++writes);
            }
        }
        catch(Exception original)
        {
            var errors=new List<Exception> {original};
            for(int i=attempted.Count-1;i>=0;i--)
            {
                var item=attempted[i];
                try
                {
                    AssertNoReparse(install); AssertNoReparse(item.Destination); AssertNoReparse(item.Backup);
                    if(item.Existed)
                    {
                        if(File.Exists(item.Destination)) File.Replace(item.Backup,item.Destination,null,true);
                        else File.Move(item.Backup,item.Destination);
                    }
                    else if(File.Exists(item.Destination)) File.Delete(item.Destination);
                }
                catch(Exception restore) { rollbackFailed=true; errors.Add(restore); }
            }
            if(rollbackFailed) throw new AggregateException("Update failed and some files could not be restored. Backups remain in "+backup,errors);
            throw;
        }
        finally
        {
            try { CleanOwnedTree(temporary,install); } catch { }
            if(!rollbackFailed) { try { CleanOwnedTree(backup,install); } catch { } }
        }
    }
}

internal static class UpdaterProgram
{
    private static bool Take(Mutex mutex,int timeout) { try { return mutex.WaitOne(timeout); } catch(AbandonedMutexException) { return true; } }
    private static bool ParentAlive(UpdatePlan plan)
    {
        try { using(var process=Process.GetProcessById(plan.TrainerPid)) return !process.HasExited && process.StartTime.ToUniversalTime().Ticks==plan.TrainerStartUtcTicks; }
        catch(ArgumentException) { return false; }
        catch(InvalidOperationException) { return false; }
        catch(System.ComponentModel.Win32Exception) { return true; }
    }
    private static bool GamesClosed()
    {
        var processes=Process.GetProcessesByName("AzerothSurvivors");
        try { return processes.Length==0; } finally { foreach(var process in processes) process.Dispose(); }
    }
    private static string UpdateMutexName(string install)
    {
        using(var sha=SHA256.Create()) return "Local\\AzerothSpeedUpdate_"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(install.ToUpperInvariant()))).Replace("-","");
    }
    private static UpdatePlan ReadPlan(string file,string expectedSha256)
    {
        if(expectedSha256==null || expectedSha256.Length!=64 || expectedSha256.Any(c=>!Uri.IsHexDigit(c))) throw new InvalidDataException("The update plan checksum is invalid.");
        if(!Path.IsPathRooted(file)) throw new InvalidDataException("The update plan path must be absolute.");
        UpdateInstallerCore.FullDirectory(Path.GetDirectoryName(file));
        UpdateInstallerCore.AssertNoReparse(file);
        using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if(input.Length<=0 || input.Length>65536) throw new InvalidDataException("The update plan exceeds its size limit.");
            byte[] bytes;
            using(var memory=new MemoryStream()) { input.CopyTo(memory); bytes=memory.ToArray(); }
            using(var sha=SHA256.Create())
                if(!String.Equals(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-",""),expectedSha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The update plan changed after the trainer prepared it.");
            using(var memory=new MemoryStream(bytes))
            using(var reader=new StreamReader(memory,new UTF8Encoding(false,true),true))
                return new JavaScriptSerializer {MaxJsonLength=65536,RecursionLimit=16}.Deserialize<UpdatePlan>(reader.ReadToEnd());
        }
    }
    private static void Result(string planFile,string text)
    {
        try
        {
            string result=Path.Combine(UpdateInstallerCore.FullDirectory(Path.GetDirectoryName(planFile)),"update-result.txt");
            UpdateInstallerCore.AssertNoReparse(result);
            File.WriteAllText(result,DateTime.UtcNow.ToString("u")+" "+text+Environment.NewLine,new UTF8Encoding(false));
        }
        catch { }
    }
    [STAThread] internal static int Main(string[] args)
    {
        if(args.Length!=4 || args[0]!="--plan" || args[2]!="--sha256")
        {
            System.Windows.Forms.MessageBox.Show("This helper installs a prepared trainer update after the trainer and Azeroth Survivors close. Start updates from the trainer.","Azeroth Speed Updater");
            return 2;
        }
        string planFile=null;
        try
        {
            if(!Path.IsPathRooted(args[1])) throw new InvalidDataException("The update plan path must be absolute.");
            planFile=Path.GetFullPath(args[1]);
            UpdatePlan plan=ReadPlan(planFile,args[3]);
            UpdateInstallerCore.ValidatePlan(plan);
            string install=UpdateInstallerCore.FullDirectory(plan.InstallDirectory);
            string helper=System.Reflection.Assembly.GetExecutingAssembly().Location;
            if(UpdateInstallerCore.IsWithin(helper,install) || UpdateInstallerCore.IsWithin(planFile,install)) throw new InvalidDataException("The update helper and plan must be staged outside the installation directory.");
            if(plan.TrainerPid<=0 || plan.TrainerStartUtcTicks<=0) throw new InvalidDataException("The update plan has no valid trainer identity.");
            bool updateOwned=false,uiOwned=false;
            using(var updateMutex=new Mutex(false,UpdateMutexName(install)))
            using(var uiMutex=new Mutex(false,"Local\\AzerothSurvivorsSpeedTrainerUI_v1"))
            {
                try
                {
                    updateOwned=Take(updateMutex,0);
                    if(!updateOwned) { Result(planFile,"Another installer is already waiting for this installation."); return 3; }
                    Result(planFile,"Waiting for the trainer and Azeroth Survivors to close.");
                    for(;;)
                    {
                        if(!ParentAlive(plan) && GamesClosed())
                        {
                            uiOwned=Take(uiMutex,500);
                            if(uiOwned && !ParentAlive(plan) && GamesClosed()) break;
                            if(uiOwned) { uiMutex.ReleaseMutex(); uiOwned=false; }
                        }
                        Thread.Sleep(500);
                    }
                    UpdateInstallerCore.Apply(plan);
                    Result(planFile,"Successfully installed version "+plan.Version+".");
                }
                finally
                {
                    if(uiOwned) uiMutex.ReleaseMutex();
                    if(updateOwned) updateMutex.ReleaseMutex();
                }
            }
            if(plan.Restart)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(install,"Azeroth-Speed.exe")) {WorkingDirectory=install,UseShellExecute=true}); }
                catch(Exception restart) { Result(planFile,"Version "+plan.Version+" was installed, but restart failed: "+restart.Message); }
            }
            return 0;
        }
        catch(Exception error) { Result(planFile,"Update failed: "+error.Message); return 1; }
    }
}
