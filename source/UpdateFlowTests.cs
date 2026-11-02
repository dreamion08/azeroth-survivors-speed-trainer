using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Runs the production update helper against disposable installation fixtures.
// No real trainer or game is started, stopped, or modified by these tests.
internal static class UpdateFlowTests
{
    private static int failures;
    private static string root;
    private static void Check(bool ok,string name) { Console.WriteLine((ok ? "PASS " : "FAIL ")+name); if(!ok) failures++; }
    private static string Hash(string path) { using(var file=File.OpenRead(path)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-",""); }
    private static bool Take(Mutex mutex) { try { return mutex.WaitOne(0); } catch(AbandonedMutexException) { return true; } }
    private static bool GamesRunning()
    {
        var processes=Process.GetProcessesByName("AzerothSurvivors");
        try { return processes.Length!=0; } finally { foreach(var process in processes) process.Dispose(); }
    }
    private static string Quote(string text)
    {
        if(text.IndexOf('"')>=0) throw new InvalidOperationException("Unexpected quote in test path.");
        return "\""+text+"\"";
    }
    private static string ReadLog(string path)
    {
        try { return File.ReadAllText(path); } catch(IOException) { return ""; }
    }
    private static bool Wait(Func<bool> done,int milliseconds,Stopwatch deadline)
    {
        long stop=Math.Min(10000,deadline.ElapsedMilliseconds+milliseconds);
        while(deadline.ElapsedMilliseconds<stop)
        {
            if(done()) return true;
            Thread.Sleep(20);
        }
        return done();
    }
    private static void CompileOldTrainer(string file)
    {
        string temporary=Path.Combine(root,"compiler"); Directory.CreateDirectory(temporary);
        using(var compiler=CodeDomProvider.CreateProvider("CSharp"))
        {
            var options=new CompilerParameters {GenerateExecutable=true,OutputAssembly=file,GenerateInMemory=false,CompilerOptions="/nologo /optimize+ /platform:x64",TempFiles=new TempFileCollection(temporary,false)};
            const string source="using System.Reflection; [assembly: AssemblyVersion(\"1.0.0.0\")] [assembly: AssemblyFileVersion(\"1.0.0.0\")] internal static class OldTrainerFixture { private static void Main() {} }";
            CompilerResults result=compiler.CompileAssemblyFromSource(options,source);
            if(result.Errors.HasErrors) throw new InvalidOperationException(String.Join(Environment.NewLine,result.Errors.Cast<CompilerError>().Select(e=>e.ToString())));
        }
    }
    private static int ChildWait(string ready,string release)
    {
        File.WriteAllText(ready,"ready");
        var watch=Stopwatch.StartNew();
        while(!File.Exists(release) && watch.ElapsedMilliseconds<10000) Thread.Sleep(20);
        return 0;
    }
    private static void Cleanup()
    {
        if(String.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        string full=Path.GetFullPath(root),parent=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
        if(!String.Equals(Path.GetDirectoryName(full),parent,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("AzerothUpdateFlowTests-",StringComparison.Ordinal)) throw new IOException("Unsafe test cleanup path.");
        // Refuse links at every level before deleting only this owned fixture.
        SafeDelete(full,full);
    }
    private static void SafeDelete(string path,string owned)
    {
        if(!UpdateInstallerCore.IsWithin(Path.GetFullPath(path),owned)) throw new IOException("Test cleanup escaped its fixture.");
        UpdateInstallerCore.AssertNoReparse(path);
        foreach(string file in Directory.GetFiles(path)) { UpdateInstallerCore.AssertNoReparse(file); File.Delete(file); }
        foreach(string child in Directory.GetDirectories(path)) SafeDelete(child,owned);
        Directory.Delete(path,false);
    }
    internal static int Main(string[] args)
    {
        if(args.Length==3 && args[0]=="--child-wait") return ChildWait(args[1],args[2]);
        if(args.Length<1 || args.Length>2) { Console.Error.WriteLine("Usage: UpdateFlowTests.exe <production-updater.exe> [production-dist-directory]"); return 2; }
        if(GamesRunning()) { Console.WriteLine("SKIP production updater flow: Azeroth Survivors is running; no game is touched."); return 0; }
        Process helper=null,child=null;
        string releaseFile=null;
        bool mutexOwned=false;
        using(var uiMutex=new Mutex(false,"Local\\AzerothSurvivorsSpeedTrainerUI_v1"))
        {
            try
            {
                if(!Take(uiMutex)) { Console.WriteLine("SKIP production updater flow: an existing trainer owns the application mutex."); return 0; }
                uiMutex.ReleaseMutex();
                string productionHelper=Path.GetFullPath(args[0]);
                string payload=Path.GetFullPath(args.Length==2 ? args[1] : Path.GetDirectoryName(productionHelper));
                if(!File.Exists(productionHelper)) throw new FileNotFoundException("The production updater was not supplied.",productionHelper);
                string productionTrainer=Path.Combine(payload,"Azeroth-Speed.exe");
                var info=FileVersionInfo.GetVersionInfo(productionTrainer);
                var version=new Version(info.FileMajorPart,info.FileMinorPart,info.FileBuildPart);
                if(version<=new Version(1,0,0)) throw new InvalidOperationException("The production payload must be newer than fixture version1.0.0.");
                root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AzerothUpdateFlowTests-"+Guid.NewGuid().ToString("N"));
                string install=Path.Combine(root,"old installation"),prepared=Path.Combine(root,"prepared release \u00c5");
                Directory.CreateDirectory(install); Directory.CreateDirectory(prepared);
                CompileOldTrainer(Path.Combine(install,"Azeroth-Speed.exe"));
                File.WriteAllText(Path.Combine(install,"AzerothSpeedHook.dll"),"old hook fixture");
                File.WriteAllText(Path.Combine(install,"Azeroth-Speed-Updater.exe"),"old updater fixture");
                File.WriteAllText(Path.Combine(install,"keep-user-file.txt"),"unrelated user data");
                string oldHash=Hash(Path.Combine(install,"Azeroth-Speed.exe"));
                foreach(string relative in UpdateClient.RuntimeFiles)
                {
                    string source=Path.Combine(payload,relative.Replace('/','\\'));
                    if(!File.Exists(source)) continue;
                    string destination=Path.Combine(prepared,relative.Replace('/','\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source,destination);
                }
                string stagedHelper=Path.Combine(prepared,"Azeroth-Speed-Updater.exe");
                File.Copy(productionHelper,stagedHelper,true);
                // This child is the trainer-identity fixture. It executes only
                // this test program's waiting branch, never the trainer binary.
                string readyFile=Path.Combine(root,"parent-ready.txt"); releaseFile=Path.Combine(root,"parent-release.txt");
                string self=Assembly.GetExecutingAssembly().Location;
                child=Process.Start(new ProcessStartInfo(self,"--child-wait "+Quote(readyFile)+" "+Quote(releaseFile)) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=root});
                if(child==null) throw new IOException("Could not start owned parent fixture.");
                var deadline=Stopwatch.StartNew();
                if(!Wait(()=>File.Exists(readyFile),2000,deadline)) throw new TimeoutException("Owned parent fixture did not become ready.");
                var manifest=new List<PreparedUpdateFile>();
                foreach(string relative in UpdateClient.RuntimeFiles)
                {
                    string file=Path.Combine(prepared,relative.Replace('/','\\'));
                    if(File.Exists(file)) manifest.Add(new PreparedUpdateFile(relative,Hash(file),new FileInfo(file).Length));
                }
                var update=new PreparedUpdate(version,prepared,new string('0',64),manifest);
                string stagedNative=Path.Combine(prepared,"AzerothSpeedHook.dll");
                byte[] verifiedNative=File.ReadAllBytes(stagedNative);
                bool modifiedRejected=false;
                try
                {
                    File.WriteAllText(stagedNative,"changed after the verified manifest was frozen");
                    try { AppUpdates.MakePlan(update,install); } catch(InvalidDataException) { modifiedRejected=true; }
                }
                finally { File.WriteAllBytes(stagedNative,verifiedNative); }
                Check(modifiedRejected,"MakePlan rejects staged bytes changed after verified manifest construction");
                UpdatePlan plan=AppUpdates.MakePlan(update,install);
                plan.TrainerPid=child.Id; plan.TrainerStartUtcTicks=child.StartTime.ToUniversalTime().Ticks; plan.Restart=false;
                Check(plan.Files.Any(f=>f.RelativePath=="Azeroth-Speed-Updater.exe") && plan.Version==version.ToString(3),"production MakePlan includes the new helper and release version");
                string planFile=Path.Combine(prepared,"update-plan.json"),logFile=Path.Combine(prepared,"update-result.txt");
                string planJson=new JavaScriptSerializer().Serialize(plan);
                File.WriteAllText(planFile,planJson,new UTF8Encoding(false));
                string planHash=Hash(planFile);
                using(var heldHelper=new FileStream(stagedHelper,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    Check(heldHelper.Length==new FileInfo(productionHelper).Length,"staged production helper is held against writes during launch");
                    helper=Process.Start(new ProcessStartInfo(stagedHelper,"--plan "+Quote(planFile)+" --sha256 "+planHash) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=prepared});
                }
                if(helper==null) throw new IOException("Could not start owned production helper.");
                if(!Wait(()=>ReadLog(logFile).Contains("Waiting for the trainer"),3000,deadline)) throw new InvalidOperationException("Production helper did not enter wait state. "+ReadLog(logFile));
                Check(!child.HasExited && !helper.HasExited && Hash(Path.Combine(install,"Azeroth-Speed.exe"))==oldHash,"production helper waits for the exact live trainer-identity child");
                if(!Take(uiMutex)) throw new InvalidOperationException("Another trainer acquired the mutex during the isolated test.");
                mutexOwned=true;
                File.WriteAllText(releaseFile,"exit now");
                Check(Wait(()=>child.HasExited,1500,deadline),"owned trainer-identity child exits normally");
                Thread.Sleep(250);
                Check(!helper.HasExited && Hash(Path.Combine(install,"Azeroth-Speed.exe"))==oldHash,"production helper waits while the application mutex is held");
                uiMutex.ReleaseMutex(); mutexOwned=false;
                if(!Wait(()=>helper.HasExited,5000,deadline)) throw new TimeoutException("Production helper exceeded the bounded10-second flow test deadline.");
                Check(helper.ExitCode==0,"production updater process exits successfully");
                Check(ReadLog(logFile).Contains("Successfully installed version "+version.ToString(3)),"production helper writes its successful update result");
                Check(Hash(Path.Combine(install,"Azeroth-Speed.exe"))==Hash(productionTrainer),"production helper installs the real new trainer without starting it");
                Check(Hash(Path.Combine(install,"AzerothSpeedHook.dll"))==Hash(Path.Combine(prepared,"AzerothSpeedHook.dll")),"production helper installs the real timing module without loading it");
                Check(Hash(Path.Combine(install,"Azeroth-Speed-Updater.exe"))==Hash(productionHelper),"staged helper replaces the inactive installed updater");
                Check(File.ReadAllText(Path.Combine(install,"keep-user-file.txt"))=="unrelated user data","production flow preserves unrelated files");
                Check(!Directory.GetDirectories(install,".azs-*").Any(),"production flow cleans its transaction folders");
                Check(Take(uiMutex),"production helper releases application mutex after installation");
                // Successful acquisition above must be released even if the
                // assertion failed (failure means this process does not own it).
                try { uiMutex.ReleaseMutex(); } catch(ApplicationException) { }
                helper.Dispose(); helper=null;
                // A semantically harmless byte change still must be rejected:
                // the helper is bound to the exact plan bytes scheduled by UI.
                File.WriteAllText(planFile,planJson+" ",new UTF8Encoding(false));
                helper=Process.Start(new ProcessStartInfo(stagedHelper,"--plan "+Quote(planFile)+" --sha256 "+planHash) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=prepared});
                if(helper==null || !Wait(()=>helper.HasExited,2000,deadline)) throw new TimeoutException("Production helper did not reject a changed plan in time.");
                Check(helper.ExitCode==1 && ReadLog(logFile).Contains("changed after the trainer prepared it"),"production helper rejects changed plan bytes before interpreting the plan");
                Check(Hash(Path.Combine(install,"Azeroth-Speed.exe"))==Hash(productionTrainer),"rejected modified plan leaves the installed trainer intact");
                Console.WriteLine(failures==0 ? "All production updater flow tests passed." : failures+" production updater flow tests failed.");
                return failures==0 ? 0 : 1;
            }
            catch(Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                if(mutexOwned) uiMutex.ReleaseMutex();
                if(releaseFile!=null && root!=null && Directory.Exists(root)) File.WriteAllText(releaseFile,"exit now");
                // On timeout/error, only the helper that this test started can
                // be terminated. No real trainer or game is ever stopped.
                if(helper!=null) { try { if(!helper.HasExited) { helper.Kill(); helper.WaitForExit(1000); } } finally { helper.Dispose(); } }
                if(child!=null) { child.WaitForExit(1000); child.Dispose(); }
                Cleanup();
            }
        }
    }
}
