using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal static class AppUpdates
{
    internal static Version CurrentVersion
    {
        get
        {
            Version v=Assembly.GetExecutingAssembly().GetName().Version;
            return new Version(v.Major,v.Minor,Math.Max(0,v.Build));
        }
    }
    internal static string StagingRoot(string installDirectory)
    {
        string normalized=Path.GetFullPath(installDirectory).TrimEnd('\\').ToUpperInvariant();
        string key;
        using(var sha=SHA256.Create()) key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-","").Substring(0,24);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AzerothSurvivorsSpeedTrainer","Updates",key);
    }
    internal static UpdatePlan MakePlan(PreparedUpdate update,string installDirectory)
    {
        var plan=new UpdatePlan {
            SchemaVersion=1,InstallDirectory=Path.GetFullPath(installDirectory),
            PreparedDirectory=Path.GetFullPath(update.Directory),Version=update.Version.ToString(3),
            Files=new System.Collections.Generic.List<UpdateFile>(),Restart=true
        };
        using(var process=Process.GetCurrentProcess())
        {
            plan.TrainerPid=process.Id;
            plan.TrainerStartUtcTicks=process.StartTime.ToUniversalTime().Ticks;
        }
        foreach(PreparedUpdateFile file in update.Files)
        {
            plan.Files.Add(new UpdateFile {RelativePath=file.RelativePath,Sha256=file.Sha256,Length=file.Length});
        }
        UpdateInstallerCore.ValidatePlan(plan);
        return plan;
    }
    internal static void Schedule(PreparedUpdate update,string installDirectory)
    {
        UpdatePlan plan=MakePlan(update,installDirectory);
        string planPath=Path.Combine(update.Directory,"update-plan-"+Guid.NewGuid().ToString("N")+".json");
        byte[] json=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(plan));
        using(var file=new FileStream(planPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) file.Write(json,0,json.Length);
        string planHash;
        using(var sha=SHA256.Create()) planHash=BitConverter.ToString(sha.ComputeHash(json)).Replace("-","").ToLowerInvariant();
        string helperPath=Path.Combine(update.Directory,"Azeroth-Speed-Updater.exe");
        var start=new ProcessStartInfo {
            FileName=helperPath,
            Arguments="--plan \""+planPath+"\" --sha256 "+planHash,UseShellExecute=false,
            WorkingDirectory=update.Directory,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden
        };
        // Deny replacement or writes while checking and launching the helper.
        // Expected hashes come from the verified ZIP, never the mutable stage.
        using(var helper=new FileStream(helperPath,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var sha=SHA256.Create())
        {
            UpdateFile expected=plan.Files.Find(f=>f.RelativePath=="Azeroth-Speed-Updater.exe");
            string hash=BitConverter.ToString(sha.ComputeHash(helper)).Replace("-","");
            if(expected==null || helper.Length!=expected.Length || !String.Equals(hash,expected.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The verified update helper changed before launch.");
            using(var child=Process.Start(start))
                if(child==null) throw new IOException("The update helper could not start. The current version has been kept.");
        }
    }
}
