using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

internal static class UpdateClientTests
{
    private static int failures;
    private static string root;
    private static Version fixtureVersion;
    private static byte[] fixtureExe;
    private static readonly UTF8Encoding Utf8=new UTF8Encoding(false);

    private static void Check(bool ok,string message)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ")+message); if(!ok) failures++;
    }
    private static string Sha(byte[] bytes)
    {
        using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
    }
    private static byte[] Bytes(string text) { return Utf8.GetBytes(text); }
    private static Dictionary<string,byte[]> Payload()
    {
        var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        files["Azeroth-Speed.exe"]=fixtureExe;
        files["AzerothSpeedHook.dll"]=Bytes("Isolated checksum fixture, not an executable module.");
        files["Azeroth-Speed-Updater.exe"]=Bytes("Isolated updater fixture, never started.");
        files["build-info.json"]=Bytes("{\"version\":\""+fixtureVersion.ToString()+"\",\"platform\":\"win-x64\",\"protocol\":3}");
        files["README.md"]=Bytes("Synthetic update archive used by offline tests.");
        files["SHA256SUMS.txt"]=Bytes(Sha(files["Azeroth-Speed.exe"])+"  Azeroth-Speed.exe\n"+Sha(files["AzerothSpeedHook.dll"])+"  AzerothSpeedHook.dll\n"+Sha(files["Azeroth-Speed-Updater.exe"])+"  Azeroth-Speed-Updater.exe\n");
        return files;
    }
    private static string Zip(string folder,Dictionary<string,byte[]> files,string extraName=null,int? extraAttributes=null,byte[] extraBytes=null)
    {
        string path=Path.Combine(folder,"fixture.zip");
        using(var image=new FileStream(path,FileMode.CreateNew,FileAccess.Write))
        using(var archive=new ZipArchive(image,ZipArchiveMode.Create))
        {
            foreach(var file in files)
            {
                var entry=archive.CreateEntry(file.Key,CompressionLevel.Optimal);
                using(var output=entry.Open()) output.Write(file.Value,0,file.Value.Length);
            }
            if(extraName!=null)
            {
                var entry=archive.CreateEntry(extraName,CompressionLevel.Optimal);
                if(extraAttributes.HasValue) entry.ExternalAttributes=extraAttributes.Value;
                byte[] content=extraBytes??Bytes("extra fixture");
                using(var output=entry.Open()) output.Write(content,0,content.Length);
            }
        }
        return path;
    }
    private static string CaseFolder()
    {
        string path=Path.Combine(root,Guid.NewGuid().ToString("N").Substring(0,8)); Directory.CreateDirectory(path); return path;
    }
    private static void AcceptArchive(string name,Dictionary<string,byte[]> files,string extraName=null,byte[] extraBytes=null)
    {
        string folder=CaseFolder();
        string zip=Zip(folder,files,extraName,null,extraBytes);
        try
        {
            PreparedUpdate update=UpdateClient.PrepareFromZip(zip,fixtureVersion,Sha(File.ReadAllBytes(zip)),Path.Combine(folder,"staging"));
            Check(update.Version==fixtureVersion && File.Exists(Path.Combine(update.Directory,"Azeroth-Speed.exe"))
                && Path.GetDirectoryName(update.Directory)==Path.Combine(folder,"staging")
                && !Path.GetFileName(update.Directory).StartsWith(".prepare-",StringComparison.Ordinal),name);
            bool manifestMatches=update.Files.Count==files.Count;
            foreach(var expected in update.Files)
            {
                byte[] original;
                if(!files.TryGetValue(expected.RelativePath,out original) || expected.Sha256!=Sha(original) || expected.Length!=original.Length)
                    manifestMatches=false;
            }
            Check(manifestMatches,name+" retains hashes and lengths from every verified archive file");
        }
        catch(Exception e) { Check(false,name+": "+e.GetType().Name+": "+e.Message); }
    }
    private static void RejectArchive(string name,Action<Dictionary<string,byte[]>> mutate=null,string extraName=null,int? extraAttributes=null,byte[] extraBytes=null,Version expectedVersion=null,bool badZipChecksum=false)
    {
        string folder=CaseFolder();
        var files=Payload(); if(mutate!=null) mutate(files);
        string zip=Zip(folder,files,extraName,extraAttributes,extraBytes);
        string stage=Path.Combine(folder,"staging");
        bool rejected=false;
        try
        {
            UpdateClient.PrepareFromZip(zip,expectedVersion??fixtureVersion,badZipChecksum ? new string('0',64) : Sha(File.ReadAllBytes(zip)),stage);
        }
        catch(InvalidDataException) { rejected=true; }
        catch(Exception e) { Check(false,name+" threw unexpected "+e.GetType().Name+": "+e.Message); return; }
        bool clean=!Directory.Exists(stage) || (Directory.GetFiles(stage).Length==0 && Directory.GetDirectories(stage).Length==0);
        Check(rejected && clean,name+" (rejected without a prepared update)");
    }
    private static void RejectAction(string name,Action action)
    {
        try { action(); Check(false,name); }
        catch(InvalidDataException) { Check(true,name); }
    }
    private static void CheckFrozenManifest()
    {
        string folder=CaseFolder();
        var files=Payload();
        string zip=Zip(folder,files);
        PreparedUpdate update=UpdateClient.PrepareFromZip(zip,fixtureVersion,Sha(File.ReadAllBytes(zip)),Path.Combine(folder,"staging"));
        PreparedUpdateFile expected=null;
        foreach(var file in update.Files) if(file.RelativePath=="AzerothSpeedHook.dll") expected=file;
        string before=Sha(files["AzerothSpeedHook.dll"]);
        long length=files["AzerothSpeedHook.dll"].Length;
        string staged=Path.Combine(update.Directory,"AzerothSpeedHook.dll");
        File.WriteAllText(staged,"Changed after release verification; must never become the new expected checksum.",Utf8);
        Check(expected!=null && expected.Sha256==before && expected.Length==length && Sha(File.ReadAllBytes(staged))!=expected.Sha256,
            "staged mutation cannot change the original verified manifest");
        bool collectionReadOnly=false;
        try { ((IList<PreparedUpdateFile>)update.Files).Clear(); }
        catch(NotSupportedException) { collectionReadOnly=true; }
        Check(collectionReadOnly && update.Files.Count==files.Count,"prepared manifest cannot be changed through its collection interface");
        bool propertiesReadOnly=true;
        foreach(string property in new[]{"RelativePath","Sha256","Length"})
        {
            var info=typeof(PreparedUpdateFile).GetProperty(property,BindingFlags.Instance|BindingFlags.NonPublic);
            if(info==null || info.CanWrite) propertiesReadOnly=false;
        }
        Check(propertiesReadOnly,"prepared file identity, checksum and length expose no setters");
        var supplied=new List<PreparedUpdateFile> {new PreparedUpdateFile("AzerothSpeedHook.dll",before,length)};
        var copied=new PreparedUpdate(fixtureVersion,update.Directory,update.ZipSha256,supplied);
        supplied.Clear();
        Check(copied.Files.Count==1 && copied.Files[0].Sha256==before,"prepared manifest snapshots the caller's collection");
    }
    private static int Main()
    {
        string own=Assembly.GetExecutingAssembly().Location;
        Version fileVersion;
        if(!Version.TryParse(FileVersionInfo.GetVersionInfo(own).FileVersion,out fileVersion))
        {
            Console.WriteLine("FAIL UpdateClientTests must be built with the project's AssemblyInfo.cs."); return 1;
        }
        fixtureVersion=new Version(fileVersion.Major,fileVersion.Minor,Math.Max(0,fileVersion.Build));
        fixtureExe=File.ReadAllBytes(own);
        // Keep fixtures beside this test executable; some managed Windows
        // environments redirect TEMP to storage that disallows directory moves.
        root=Path.Combine(Path.GetDirectoryName(own),"AU_"+Guid.NewGuid().ToString("N").Substring(0,8));
        Directory.CreateDirectory(root);
        try
        {
            Check(UpdateClient.ParseStableVersion("v1.2.3")==new Version(1,2,3),"parse a stable three-part version");
            RejectAction("reject a prerelease version",()=>UpdateClient.ParseStableVersion("v1.2.3-beta1"));
            RejectAction("reject a two-part tag",()=>UpdateClient.ParseStableVersion("v1.2"));
            RejectAction("reject a four-part tag",()=>UpdateClient.ParseStableVersion("v1.2.3.4"));
            RejectAction("reject a tag with leading zeros",()=>UpdateClient.ParseStableVersion("v01.2.3"));
            RejectAction("reject an overflowing version",()=>UpdateClient.ParseStableVersion("v999999999999.2.3"));
            string asset="Azeroth-Survivors-Speed-Trainer-v1.2.3-win-x64.zip";
            string correct="https://github.com/dreamion08/azeroth-survivors-speed-trainer/releases/download/v1.2.3/"+asset;
            Check(UpdateClient.ValidateAssetUrl(correct,"v1.2.3",asset).AbsoluteUri==correct,"allow the exact project's HTTPS release URL");
            RejectAction("reject another GitHub repository",()=>UpdateClient.ValidateAssetUrl(correct.Replace("dreamion08/","other-owner/"),"v1.2.3",asset));
            RejectAction("reject non-HTTPS asset URL",()=>UpdateClient.ValidateAssetUrl(correct.Replace("https:","http:"),"v1.2.3",asset));
            RejectAction("reject a lookalike GitHub hostname",()=>UpdateClient.ValidateAssetUrl(correct.Replace("github.com","github.com.evil.invalid"),"v1.2.3",asset));
            RejectAction("reject asset URL query substitution",()=>UpdateClient.ValidateAssetUrl(correct+"?download=other","v1.2.3",asset));
            RejectAction("reject a different asset filename",()=>UpdateClient.ValidateAssetUrl(correct+".exe","v1.2.3",asset));
            string hash=new string('a',64);
            Check(UpdateClient.ParseChecksumText(hash+"  "+asset+"\n",asset)==hash,"parse checksum naming the exact archive");
            Check(UpdateClient.ParseChecksumText(hash+" *"+asset+"\r\n",asset)==hash,"parse standard binary-mode checksum");
            RejectAction("reject a checksum for another filename",()=>UpdateClient.ParseChecksumText(hash+"  other.zip\n",asset));
            RejectAction("reject multiple checksum-asset lines",()=>UpdateClient.ParseChecksumText(hash+"  "+asset+"\n"+hash+"  other.zip",asset));
            AcceptArchive("prepare valid versioned runtime archive",Payload());
            var withDocs=Payload(); withDocs["docs/full-preview.png"]=new byte[]{137,80,78,71};
            AcceptArchive("allow documented runtime files and docs folder",withDocs,"docs/",new byte[0]);
            CheckFrozenManifest();
            RejectArchive("reject wrong ZIP checksum",badZipChecksum:true);
            RejectArchive("reject parent traversal",extraName:"../outside.txt");
            RejectArchive("reject absolute path",extraName:"/outside.txt");
            RejectArchive("reject drive-qualified path",extraName:"C:/outside.txt");
            RejectArchive("reject backslash traversal",extraName:@"docs\..\outside.txt");
            RejectArchive("reject unknown executable",extraName:"unexpected.exe");
            RejectArchive("reject duplicate archive filename",extraName:"README.md");
            RejectArchive("reject case-colliding archive filename",extraName:"readme.md");
            RejectArchive("reject symlink archive entry",extraName:"docs/full-preview.png",extraAttributes:unchecked((int)0xA1FF0000));
            RejectArchive("reject missing updater",files=>files.Remove("Azeroth-Speed-Updater.exe"));
            RejectArchive("reject missing metadata",files=>files.Remove("build-info.json"));
            RejectArchive("reject missing file checksum list",files=>files.Remove("SHA256SUMS.txt"));
            RejectArchive("reject changed binary payload",files=>files["AzerothSpeedHook.dll"]=Bytes("modified after checksum generation"));
            RejectArchive("reject unchecked updater binary",files=>files["SHA256SUMS.txt"]=Bytes(Sha(files["Azeroth-Speed.exe"])+"  Azeroth-Speed.exe\n"+Sha(files["AzerothSpeedHook.dll"])+"  AzerothSpeedHook.dll\n"));
            RejectArchive("reject metadata version mismatch",files=>files["build-info.json"]=Bytes("{\"version\":\"999.0.0\",\"platform\":\"win-x64\"}"));
            RejectArchive("reject metadata platform mismatch",files=>files["build-info.json"]=Bytes("{\"version\":\""+fixtureVersion+"\",\"platform\":\"win-x86\"}"));
            Version next=new Version(fixtureVersion.Major,fixtureVersion.Minor,fixtureVersion.Build+1);
            RejectArchive("reject executable version mismatch",files=>files["build-info.json"]=Bytes("{\"version\":\""+next+"\",\"platform\":\"win-x64\"}"),expectedVersion:next);
            RejectArchive("reject uncompressed ZIP bomb",extraName:"docs/full-preview.png",extraBytes:new byte[25*1024*1024]);
            Console.WriteLine(failures==0 ? "All update-client tests passed." : failures+" update-client tests failed.");
            return failures==0 ? 0 : 1;
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
