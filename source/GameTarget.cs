using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

internal static class Target
{
    internal const string ExecutableName="AzerothSurvivors.exe";
    internal const string ProcessName="AzerothSurvivors";
    internal const uint Magic=0x415A5350;
    internal const uint ProtocolVersion=3;
    internal const int MaxPathChars=32768;
    internal const int ExpectedPathOffset=56;
    internal const int ControlSize=ExpectedPathOffset+MaxPathChars*2;

    internal static string NormalizeGamePath(string path)
    {
        if(String.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\?\",StringComparison.Ordinal) || path.StartsWith(@"\\.\",StringComparison.Ordinal))
            throw new ArgumentException("Choose a normal Windows path to AzerothSurvivors.exe.");
        bool drive=path.Length>=3 && Char.IsLetter(path[0]) && path[1]==':' && (path[2]=='\\' || path[2]=='/');
        bool unc=path.StartsWith(@"\\",StringComparison.Ordinal);
        if(!drive && !unc) throw new ArgumentException("Choose an absolute path to AzerothSurvivors.exe.");
        string full=Path.GetFullPath(path);
        if(!String.Equals(Path.GetFileName(full),ExecutableName,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select AzerothSurvivors.exe. Other executables are not supported.");
        if(full.Length>=MaxPathChars) throw new ArgumentException("The selected game path is too long.");
        return full;
    }
    internal static bool SamePath(string actual,string expected)
    {
        try { return String.Equals(NormalizeGamePath(actual),NormalizeGamePath(expected),StringComparison.OrdinalIgnoreCase); }
        catch(ArgumentException) { return false; }
        catch(NotSupportedException) { return false; }
        catch(PathTooLongException) { return false; }
    }
    internal static string VerifySelection(string path)
    {
        string full=NormalizeGamePath(path);
        using(var file=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read)) TargetCompatibility.Verify(file);
        return full;
    }
    internal static void WriteExpectedPath(MemoryMappedViewAccessor view,string path)
    {
        string full=NormalizeGamePath(path);
        byte[] bytes=Encoding.Unicode.GetBytes(full+"\0");
        view.WriteArray(ExpectedPathOffset,bytes,0,bytes.Length);
        view.Write(48,(uint)full.Length);
        view.Write(52,(uint)0);
    }
}
