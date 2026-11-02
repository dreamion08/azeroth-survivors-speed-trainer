using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

internal static class GameTargetTests
{
    private static int failures;
    private static void Check(bool ok,string name) { Console.WriteLine((ok ? "PASS " : "FAIL ")+name); if(!ok) failures++; }
    private static void Accept(string path)
    {
        try { Check(Target.NormalizeGamePath(path).EndsWith(Target.ExecutableName,StringComparison.OrdinalIgnoreCase),"portable absolute installation accepted"); }
        catch(Exception e) { Check(false,"portable installation rejected: "+e.Message); }
    }
    private static void Reject(string path)
    {
        bool rejected=false;
        try { Target.NormalizeGamePath(path); }
        catch(ArgumentException) { rejected=true; }
        catch(NotSupportedException) { rejected=true; }
        catch(PathTooLongException) { rejected=true; }
        Check(rejected,"non-game or unsupported path rejected");
    }
    private static int Main()
    {
        Accept(@"C:\Games\Azeroth\AzerothSurvivors.exe");
        Accept(@"D:\Portable Games\AzerothSurvivors.exe");
        Accept(@"C:\Users\Player\Downloads\Game\AzerothSurvivors.exe");
        Accept(@"E:\Spel\Åzeroth\AzerothSurvivors.exe");
        Accept(@"\\server\games\AzerothSurvivors.exe");
        Reject(null); Reject(""); Reject("AzerothSurvivors.exe");
        Reject(@"C:AzerothSurvivors.exe"); Reject(@"C:\Games\OtherGame.exe");
        Reject(@"\\?\C:\Games\AzerothSurvivors.exe");
        Reject(@"\\.\C:\Games\AzerothSurvivors.exe");
        Check(Target.SamePath(@"D:\Games\AzerothSurvivors.exe",@"d:\games\AZEROTHSURVIVORS.EXE"),"comparison accepts Windows path case differences");
        Check(!Target.SamePath(@"D:\Games\AzerothSurvivors.exe",@"E:\Games\AzerothSurvivors.exe"),"other installation does not match selection");
        Check(!Target.SamePath(@"D:\Games\OtherGame.exe",@"D:\Games\AzerothSurvivors.exe"),"other executable cannot match selection");
        Check(Target.ControlSize==65592 && Target.ExpectedPathOffset==56 && Target.ProtocolVersion==3,"portable IPC size and version match native ABI");
        using(var map=MemoryMappedFile.CreateNew("Local\\AzerothPathTest_"+Guid.NewGuid(),Target.ControlSize))
        using(var view=map.CreateViewAccessor(0,Target.ControlSize))
        {
            string path=@"D:\Portable Games\Åzeroth\AzerothSurvivors.exe";
            Target.WriteExpectedPath(view,path);
            uint count=view.ReadUInt32(48);
            var bytes=new byte[(count+1)*2];
            view.ReadArray(Target.ExpectedPathOffset,bytes,0,bytes.Length);
            Check(count==path.Length && Encoding.Unicode.GetString(bytes)==path+"\0","full selected Unicode path is serialized with length and terminator");
            Target.WriteExpectedPath(view,@"C:\AzerothSurvivors.exe");
            count=view.ReadUInt32(48); bytes=new byte[(count+1)*2];
            view.ReadArray(Target.ExpectedPathOffset,bytes,0,bytes.Length);
            Check(Encoding.Unicode.GetString(bytes)==@"C:\AzerothSurvivors.exe"+"\0","shorter subsequent selection replaces previous path safely");
        }
        Console.WriteLine(failures==0 ? "All portable target checks passed." : failures+" target checks failed.");
        return failures==0 ? 0 : 1;
    }
}
