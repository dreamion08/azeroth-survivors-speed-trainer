using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class UpdateInstallerTests
{
    private static int failures;
    private static string root,oldExe,newExe;
    private static void Check(bool value,string name) { Console.WriteLine((value ? "PASS " : "FAIL ")+name); if(!value) failures++; }
    private static string Hash(string file) { using(var input=File.OpenRead(file)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""); }
    private static void Reject(Action action,string name)
    {
        bool rejected=false; try { action(); } catch(Exception) { rejected=true; }
        Check(rejected,name);
    }
    private static void CompileFixture(string file,string version)
    {
        string temp=Path.Combine(root,"compiler"); Directory.CreateDirectory(temp);
        using(var provider=CodeDomProvider.CreateProvider("CSharp"))
        {
            var options=new CompilerParameters {GenerateExecutable=true,GenerateInMemory=false,OutputAssembly=file,CompilerOptions="/nologo /optimize+ /platform:x64",TempFiles=new TempFileCollection(temp,false)};
            string source="using System.Reflection; [assembly: AssemblyVersion(\""+version+"\")] [assembly: AssemblyFileVersion(\""+version+"\")] internal static class Fixture { private static void Main() {} }";
            CompilerResults result=provider.CompileAssemblyFromSource(options,source);
            if(result.Errors.HasErrors) throw new InvalidOperationException(String.Join(Environment.NewLine,result.Errors.Cast<CompilerError>().Select(e=>e.ToString())));
        }
    }
    private static UpdatePlan Plan(string name)
    {
        string area=Path.Combine(root,name),install=Path.Combine(area,"install"),prepared=Path.Combine(area,"prepared");
        Directory.CreateDirectory(install); Directory.CreateDirectory(prepared);
        File.Copy(oldExe,Path.Combine(install,"Azeroth-Speed.exe"));
        File.WriteAllText(Path.Combine(install,"AzerothSpeedHook.dll"),"old hook");
        File.WriteAllText(Path.Combine(install,"Azeroth-Speed-Updater.exe"),"old updater");
        File.WriteAllText(Path.Combine(install,"README.md"),"old readme");
        File.WriteAllText(Path.Combine(install,"keep-user-file.txt"),"user data must stay");
        File.Copy(newExe,Path.Combine(prepared,"Azeroth-Speed.exe"));
        File.WriteAllText(Path.Combine(prepared,"AzerothSpeedHook.dll"),"new hook");
        File.Copy(newExe,Path.Combine(prepared,"Azeroth-Speed-Updater.exe"));
        File.WriteAllText(Path.Combine(prepared,"README.md"),"new readme");
        var plan=new UpdatePlan {InstallDirectory=install,PreparedDirectory=prepared,Version="2.0.0",Restart=false};
        foreach(string namePart in new[]{"Azeroth-Speed.exe","AzerothSpeedHook.dll","Azeroth-Speed-Updater.exe","README.md"})
        {
            string file=Path.Combine(prepared,namePart);
            plan.Files.Add(new UpdateFile {RelativePath=namePart,Length=new FileInfo(file).Length,Sha256=Hash(file)});
        }
        return plan;
    }
    private static void Rehash(UpdatePlan plan,string relative)
    {
        UpdateFile item=plan.Files.First(f=>f.RelativePath==relative);
        string file=Path.Combine(plan.PreparedDirectory,relative);
        item.Length=new FileInfo(file).Length; item.Sha256=Hash(file);
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputLength,IntPtr output,int outputLength,out int returned,IntPtr overlapped);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool RemoveDirectory(string name);
    private static void Junction(string link,string target)
    {
        Directory.CreateDirectory(link);
        string substitute="\\??\\"+Path.GetFullPath(target);
        byte[] substituteBytes=Encoding.Unicode.GetBytes(substitute),printBytes=Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer=new byte[16+substituteBytes.Length+2+printBytes.Length+2];
        using(var output=new MemoryStream(buffer)) using(var writer=new BinaryWriter(output))
        {
            writer.Write((uint)0xA0000003); writer.Write((ushort)(buffer.Length-8)); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write((ushort)substituteBytes.Length);
            writer.Write((ushort)(substituteBytes.Length+2)); writer.Write((ushort)printBytes.Length);
            writer.Write(substituteBytes); writer.Write((ushort)0); writer.Write(printBytes); writer.Write((ushort)0);
        }
        using(var handle=CreateFile(link,0x40000000,0,IntPtr.Zero,3,0x02200000,IntPtr.Zero))
        {
            int returned;
            if(handle.IsInvalid || !DeviceIoControl(handle,0x000900A4,buffer,buffer.Length,IntPtr.Zero,0,out returned,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Create isolated test junction");
        }
    }
    private static void TestReparse()
    {
        UpdatePlan plan=Plan("reparse"); string link=Path.Combine(Path.GetDirectoryName(plan.PreparedDirectory),"linked-prepared");
        Junction(link,plan.PreparedDirectory);
        try { plan.PreparedDirectory=link; Reject(()=>UpdateInstallerCore.ValidatePlan(plan),"junction in prepared path is rejected"); }
        finally { if(!RemoveDirectory(link)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Remove isolated test junction"); }
    }
    private static void Cleanup()
    {
        if(String.IsNullOrEmpty(root)) return;
        string full=Path.GetFullPath(root),temp=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
        if(!full.StartsWith(temp+"\\",StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("AzerothUpdaterTests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path.");
        UpdateInstallerCore.AssertNoReparse(full);
        foreach(string file in Directory.GetFiles(full,"*",SearchOption.AllDirectories)) UpdateInstallerCore.AssertNoReparse(file);
        Directory.Delete(full,true);
    }
    internal static int Main()
    {
        root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AzerothUpdaterTests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            oldExe=Path.Combine(root,"old-fixture.exe"); newExe=Path.Combine(root,"new-fixture.exe");
            CompileFixture(oldExe,"1.0.0.0"); CompileFixture(newExe,"2.0.0.0");

            UpdatePlan success=Plan("success");
            UpdateInstallerCore.Apply(success);
            Check(Hash(Path.Combine(success.InstallDirectory,"Azeroth-Speed.exe"))==Hash(newExe),"transaction installs new versioned executable");
            Check(File.ReadAllText(Path.Combine(success.InstallDirectory,"README.md"))=="new readme","transaction installs optional allowlisted files");
            Check(File.ReadAllText(Path.Combine(success.InstallDirectory,"keep-user-file.txt"))=="user data must stay","transaction preserves unrelated user files");
            Check(!Directory.GetDirectories(success.InstallDirectory,".azs-*").Any(),"successful transaction cleans its own temporary and backup folders");

            UpdatePlan rollback=Plan("rollback");
            Reject(()=>UpdateInstallerCore.Apply(rollback,writes=>{if(writes==1) throw new IOException("Injected write failure");}),"injected failure stops transaction");
            Check(Hash(Path.Combine(rollback.InstallDirectory,"Azeroth-Speed.exe"))==Hash(oldExe),"rollback restores previous working executable");
            Check(File.ReadAllText(Path.Combine(rollback.InstallDirectory,"AzerothSpeedHook.dll"))=="old hook" && File.ReadAllText(Path.Combine(rollback.InstallDirectory,"README.md"))=="old readme","rollback preserves all remaining old files");
            Check(File.ReadAllText(Path.Combine(rollback.InstallDirectory,"keep-user-file.txt"))=="user data must stay","rollback preserves unrelated user files");

            UpdatePlan badHash=Plan("hash"); badHash.Files[0].Sha256=new string('0',64);
            Reject(()=>UpdateInstallerCore.Apply(badHash),"wrong checksum is rejected before mutation");
            Check(Hash(Path.Combine(badHash.InstallDirectory,"Azeroth-Speed.exe"))==Hash(oldExe),"checksum failure keeps previous executable");
            UpdatePlan traversal=Plan("traversal"); traversal.Files[0].RelativePath="../Azeroth-Speed.exe";
            Reject(()=>UpdateInstallerCore.ValidatePlan(traversal),"parent traversal is rejected");
            UpdatePlan extra=Plan("unexpected"); extra.Files[0].RelativePath="user-settings.json";
            Reject(()=>UpdateInstallerCore.ValidatePlan(extra),"unexpected filename is rejected");
            UpdatePlan duplicate=Plan("duplicate"); duplicate.Files.Add(new UpdateFile {RelativePath="README.MD",Length=0,Sha256=new string('0',64)});
            Reject(()=>UpdateInstallerCore.ValidatePlan(duplicate),"case-insensitive filename collision is rejected");
            UpdatePlan missing=Plan("missing"); missing.Files.RemoveAll(f=>f.RelativePath=="AzerothSpeedHook.dll");
            Reject(()=>UpdateInstallerCore.ValidatePlan(missing),"missing required native module is rejected");
            UpdatePlan mismatch=Plan("version-mismatch"); mismatch.Version="3.0.0";
            Reject(()=>UpdateInstallerCore.ValidatePlan(mismatch),"plan version must match staged executable file version");
            UpdatePlan old=Plan("old-version"); File.Copy(oldExe,Path.Combine(old.PreparedDirectory,"Azeroth-Speed.exe"),true); Rehash(old,"Azeroth-Speed.exe"); old.Version="1.0.0";
            Reject(()=>UpdateInstallerCore.ValidatePlan(old),"equal or older version is rejected despite correct checksums");
            UpdatePlan downgrade=Plan("downgrade"); File.Copy(newExe,Path.Combine(downgrade.InstallDirectory,"Azeroth-Speed.exe"),true); File.Copy(oldExe,Path.Combine(downgrade.PreparedDirectory,"Azeroth-Speed.exe"),true); Rehash(downgrade,"Azeroth-Speed.exe"); downgrade.Version="1.0.0";
            Reject(()=>UpdateInstallerCore.ValidatePlan(downgrade),"version downgrade is rejected");
            UpdatePlan edited=Plan("edited-size"); edited.Files[1].Length++;
            Reject(()=>UpdateInstallerCore.ValidatePlan(edited),"edited file size is rejected");
            UpdatePlan overlap=Plan("overlap"); overlap.PreparedDirectory=overlap.InstallDirectory;
            Reject(()=>UpdateInstallerCore.ValidatePlan(overlap),"overlapping install and preparation directories are rejected");
            TestReparse();
            Console.WriteLine(failures==0 ? "All installer tests passed." : failures+" installer tests failed.");
            return failures==0 ? 0 : 1;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Cleanup(); }
    }
}
