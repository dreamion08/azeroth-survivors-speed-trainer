using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class WheelInputTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Device { public ushort UsagePage,Usage; public uint Flags; public IntPtr Window; }
    [DllImport("user32.dll",SetLastError=true)]
    private static extern uint GetRegisteredRawInputDevices([Out] Device[] devices,ref uint count,uint size);
    private static int failures;
    private static void Put16(byte[] packet,int offset,ushort value) { Buffer.BlockCopy(BitConverter.GetBytes(value),0,packet,offset,2); }
    private static void Put32(byte[] packet,int offset,uint value) { Buffer.BlockCopy(BitConverter.GetBytes(value),0,packet,offset,4); }
    private static byte[] Packet(short delta,ushort flags=0x0400,uint type=0)
    {
        var packet=new byte[WheelInput.HeaderSize+24];
        Put32(packet,0,type); Put32(packet,4,(uint)packet.Length);
        Put16(packet,WheelInput.HeaderSize+4,flags);
        Put16(packet,WheelInput.HeaderSize+6,unchecked((ushort)delta));
        return packet;
    }
    private static void Check(bool condition,string text)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ")+text);
        if(!condition) failures++;
    }
    private static void Accept(string text,byte[] packet,int expected)
    {
        int delta; bool accepted=WheelInput.TryReadWheel(packet,out delta);
        Check(accepted && delta==expected,text);
    }
    private static void Reject(string text,byte[] packet)
    {
        int delta; Check(!WheelInput.TryReadWheel(packet,out delta),text);
    }
    private static int Main()
    {
        Check(WheelInput.HeaderSize==8+2*IntPtr.Size,"raw input header matches process pointer size");
        Accept("vertical wheel up preserves signed120 delta",Packet(120),120);
        Accept("vertical wheel down preserves signed negative120 delta",Packet(-120),-120);
        Accept("high resolution wheel delta is not rounded",Packet(15),15);
        Accept("multiple wheel detents preserved",Packet(360),360);
        Accept("button and wheel flags can coexist",Packet(-240,0x0401),-240);
        Accept("large positive signed wheel delta preserved",Packet(Int16.MaxValue),Int16.MaxValue);
        Accept("large negative signed wheel delta preserved",Packet(Int16.MinValue),Int16.MinValue);
        Reject("no motion or button data is treated as wheel",Packet(120,0));
        Reject("ordinary button data is not wheel input",Packet(120,1));
        Reject("horizontal wheel does not change vertical speed",Packet(120,0x0800));
        Reject("ambiguous vertical and horizontal flags rejected",Packet(120,0x0C00));
        Reject("zero wheel distance ignored",Packet(0));
        Reject("keyboard packet ignored",Packet(120,0x0400,1));
        Reject("HID packet ignored",Packet(120,0x0400,2));
        Reject("null packet rejected",null);
        Reject("empty packet rejected",new byte[0]);
        Reject("truncated mouse payload rejected",new byte[WheelInput.HeaderSize+23]);
        var shortHeader=Packet(120); Put32(shortHeader,4,(uint)WheelInput.HeaderSize); Reject("declared size excludes payload",shortHeader);
        var tooLarge=Packet(120); Put32(tooLarge,4,(uint)tooLarge.Length+1); Reject("declared size exceeds available packet",tooLarge);
        var huge=Packet(120); Put32(huge,4,UInt32.MaxValue); Reject("overflowing declared size rejected",huge);
        Reject("oversized packet rejected",new byte[4097]);
        bool constructorRejects=false;
        try { new WheelInput(null); } catch(ArgumentNullException) { constructorRejects=true; }
        Check(constructorRejects,"callback is required");
        using(var input=new WheelInput(delegate {}))
        {
            Check(!input.IsAttached,"helper starts detached");
            Check(!input.Attach(IntPtr.Zero) && !input.IsAttached && input.LastError==87,"null target cannot register input");
            input.Detach(); input.Detach();
            Check(!input.IsAttached,"detaching an idle helper is harmless");
            input.Dispose(); input.Dispose();
            bool disposedRejects=false;
            try { input.Attach(new IntPtr(1)); } catch(ObjectDisposedException) { disposedRejects=true; }
            Check(disposedRejects,"disposed helper cannot register another window");
        }
        // Register only against hidden, message-only windows owned by this test
        // process. This neither moves the pointer nor injects/suppresses input.
        var first=new NativeWindow();
        var second=new NativeWindow();
        first.CreateHandle(new CreateParams {Parent=new IntPtr(-3)});
        second.CreateHandle(new CreateParams {Parent=new IntPtr(-3)});
        try
        {
            using(var input=new WheelInput(delegate {}))
            {
                bool attached=input.Attach(first.Handle);
                Check(attached && input.IsAttached,"Windows accepts mouse INPUTSINK on a message-only test window (error "+input.LastError+")");
                if(attached)
                {
                    Check(input.Attach(first.Handle),"attaching the same window is idempotent");
                    uint count=8;
                    var devices=new Device[count];
                    uint result=GetRegisteredRawInputDevices(devices,ref count,(uint)Marshal.SizeOf(typeof(Device)));
                    bool correct=false;
                    if(result!=UInt32.MaxValue)
                        for(int i=0;i<(int)result;i++)
                            if(devices[i].UsagePage==1 && devices[i].Usage==2 && devices[i].Window==first.Handle)
                                correct=devices[i].Flags==0x100;
                    Check(correct,"registration requests INPUTSINK without NOLEGACY or input suppression");
                    input.Detach();
                    Check(!input.IsAttached,"Windows registration can be removed");
                    Check(input.Attach(second.Handle),"a replacement window can receive a fresh registration");
                }
            }
        }
        finally { first.DestroyHandle(); second.DestroyHandle(); }
        Console.WriteLine(failures==0 ? "All wheel packet checks passed." : failures+" wheel checks failed.");
        return failures==0 ? 0 : 1;
    }
}
