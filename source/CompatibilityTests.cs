using System;
using System.IO;
using System.Text;

internal static class CompatibilityTests
{
    private static int failures;
    private static void Put16(byte[] b,int offset,ushort value) { Buffer.BlockCopy(BitConverter.GetBytes(value),0,b,offset,2); }
    private static void Put32(byte[] b,int offset,uint value) { Buffer.BlockCopy(BitConverter.GetBytes(value),0,b,offset,4); }
    private static void Put64(byte[] b,int offset,ulong value) { Buffer.BlockCopy(BitConverter.GetBytes(value),0,b,offset,8); }
    private static void Text(byte[] b,int offset,string value) { byte[] s=Encoding.ASCII.GetBytes(value+"\0"); Buffer.BlockCopy(s,0,b,offset,s.Length); }
    private static byte[] Fixture()
    {
        var b=new byte[0x1400];
        Put16(b,0,0x5A4D); Put32(b,60,0x80);
        Put32(b,0x80,0x4550); Put16(b,0x84,0x8664); Put16(b,0x86,1); Put16(b,0x94,240); Put16(b,0x96,0x22);
        int o=0x98;
        Put16(b,o,0x20B); Put32(b,o+32,0x1000); Put32(b,o+36,0x200); Put32(b,o+56,0x2000); Put32(b,o+60,0x400); Put32(b,o+108,16);
        Put32(b,o+120,0x1100); Put32(b,o+124,40);
        int section=o+240;
        Text(b,section,".rdata"); Put32(b,section+8,0x1000); Put32(b,section+12,0x1000); Put32(b,section+16,0x1000); Put32(b,section+20,0x400);
        Put32(b,0x500,0x1200); Put32(b,0x50C,0x1300); Put32(b,0x510,0x1280);
        Put64(b,0x600,0x1340); Put64(b,0x608,0x1380); Put64(b,0x680,0x1340); Put64(b,0x688,0x1380);
        Text(b,0x700,"kernel32.dll"); Text(b,0x742,"QueryPerformanceCounter"); Text(b,0x782,"QueryPerformanceFrequency");
        Text(b,0x800,"wenilla_rogue_app"); Text(b,0x840,"bevy_time::");
        return b;
    }
    private static void Check(bool ok,string text)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ")+text); if(!ok) failures++;
    }
    private static void Accept(string name,byte[] bytes)
    {
        using(var stream=new MemoryStream(bytes))
        {
            stream.Position=7;
            try { TargetCompatibility.Verify(stream); Check(stream.Position==7,name+" (position restored)"); }
            catch(Exception e) { Check(false,name+": "+e.Message); }
        }
    }
    private static void Reject(string name,byte[] bytes)
    {
        using(var stream=new MemoryStream(bytes))
        {
            stream.Position=Math.Min(7,stream.Length);
            long before=stream.Position;
            bool rejected=false;
            try { TargetCompatibility.Verify(stream); }
            catch(InvalidDataException) { rejected=true; }
            catch(NotSupportedException) { rejected=true; }
            catch(Exception e) { Check(false,name+" threw unexpected "+e.GetType().Name); return; }
            Check(rejected && stream.Position==before,name+" rejected and stream position restored");
        }
    }
    private static byte[] Change(Action<byte[]> edit) { byte[] bytes=Fixture(); edit(bytes); return bytes; }
    private static int Main(string[] args)
    {
        Accept("valid x64 named Windows timing imports",Fixture());
        Accept("ordinary build timestamp update",Change(b=>Put32(b,0x88,0xF1234567)));
        Accept("ordinary game content update",Change(b=>{Text(b,0x1100,"New game data for another build"); b[0x1200]=241;}));
        Accept("KernelBase timing provider",Change(b=>Text(b,0x700,"kernelbase.dll")));
        Accept("Windows profile API-set provider",Change(b=>Text(b,0x700,"api-ms-win-core-profile-l1-1-0.dll")));
        byte[] crossing=new byte[0x21000];
        Buffer.BlockCopy(Fixture(),0,crossing,0,0x1400);
        crossing[0x800]=0; crossing[0x840]=0;
        Text(crossing,65530,"wenilla_rogue_app"); Text(crossing,131068,"bevy_time::");
        Accept("identity markers across stream chunk boundaries",crossing);
        Reject("32-bit machine",Change(b=>Put16(b,0x84,0x14c)));
        Reject("PE32 optional header",Change(b=>Put16(b,0x98,0x10b)));
        Reject("DLL instead of executable",Change(b=>Put16(b,0x96,0x2022)));
        Reject("non-executable image",Change(b=>Put16(b,0x96,0x20)));
        Reject("bad DOS signature",Change(b=>b[0]=0));
        Reject("out of range PE header",Change(b=>Put32(b,60,0xFFFFFFF0)));
        Reject("truncated file",new byte[31]);
        Reject("truncated section backing",Change(b=>Put32(b,0x188+16,0x4000)));
        Reject("invalid data directory count",Change(b=>Put32(b,0x98+108,17)));
        Reject("missing normal imports",Change(b=>Put32(b,0x98+120,0)));
        Reject("out of range import RVA",Change(b=>Put32(b,0x98+120,0xFFFFFFF0)));
        Reject("unterminated descriptor range",Change(b=>Put32(b,0x98+124,20)));
        Reject("missing OriginalFirstThunk",Change(b=>Put32(b,0x500,0)));
        Reject("missing FirstThunk",Change(b=>Put32(b,0x510,0)));
        Reject("unaligned IAT",Change(b=>Put32(b,0x510,0x1281)));
        Reject("missing named counter import",Change(b=>b[0x742]=(byte)'X'));
        Reject("missing frequency import",Change(b=>b[0x782]=(byte)'X'));
        Reject("ordinal-only counter import",Change(b=>Put64(b,0x600,0x8000000000000001UL)));
        Reject("64-bit import name outside RVA range",Change(b=>Put64(b,0x600,0x100001340UL)));
        Reject("import in unbacked virtual section",Change(b=>{Put32(b,0x98+56,0x3000);Put32(b,0x188+8,0x2000);Put64(b,0x600,0x2800);}));
        Reject("counter available only as delay import",Change(b=>{Put64(b,0x600,0x8000000000000001UL);Put32(b,0x98+112+13*8,0x1500);Put32(b,0x98+116+13*8,64);Put32(b,0x900+12,0x1280);Put32(b,0x900+16,0x1200);}));
        Reject("unsupported timing provider",Change(b=>Text(b,0x700,"user32.dll")));
        Reject("game identity marker removed",Change(b=>b[0x800]=0));
        Reject("Bevy timing marker removed",Change(b=>b[0x840]=0));
        foreach(string path in args)
        {
            try
            {
                using(var file=File.OpenRead(path)) TargetCompatibility.Verify(file);
                Check(true,"actual executable compatible: "+Path.GetFileName(path));
            }
            catch(Exception e) { Check(false,"actual executable: "+e.Message); }
        }
        Console.WriteLine(failures==0 ? "All compatibility checks passed." : failures+" compatibility checks failed.");
        return failures==0 ? 0 : 1;
    }
}
