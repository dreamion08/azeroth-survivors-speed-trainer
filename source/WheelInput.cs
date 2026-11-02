using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Receive ordinary mouse Raw Input in this process. INPUTSINK permits delivery
// while the game has focus, without installing a global hook or suppressing
// the game's normal mouse messages. The caller applies Shift/focus policy.
internal sealed class WheelInput : IDisposable
{
    private const int WmInput=0x00FF;
    private const uint RidInput=0x10000003;
    private const uint RidevRemove=0x00000001;
    private const uint RidevInputSink=0x00000100;
    private const int MaximumPacketSize=4096;
    private const int MousePayloadSize=24;
    private const ushort RiMouseWheel=0x0400;
    private const ushort RiMouseHorizontalWheel=0x0800;
    private static readonly uint NativeHeaderSize=(uint)Marshal.SizeOf(typeof(RawInputHeader));

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type,Size;
        public IntPtr Device,WParam;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage,Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll",SetLastError=true)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices,uint count,uint size);
    [DllImport("user32.dll",SetLastError=true)]
    private static extern uint GetRawInputData(IntPtr rawInput,uint command,IntPtr data,ref uint size,uint headerSize);

    private readonly Action<int> onWheel;
    private readonly byte[] packet=new byte[MaximumPacketSize];
    private IntPtr window,buffer;
    private bool disposed;
    internal bool IsAttached { get { return window!=IntPtr.Zero; } }
    internal int LastError { get; private set; }
    internal static int HeaderSize { get { return (int)NativeHeaderSize; } }

    internal WheelInput(Action<int> onWheel)
    {
        if(onWheel==null) throw new ArgumentNullException("onWheel");
        this.onWheel=onWheel;
    }

    internal bool Attach(IntPtr hwnd)
    {
        if(disposed) throw new ObjectDisposedException("WheelInput");
        if(hwnd==IntPtr.Zero) { LastError=87; return false; }
        if(window==hwnd) return true;
        Detach();
        var registration=new RawInputDevice {UsagePage=1,Usage=2,Flags=RidevInputSink,Target=hwnd};
        if(!RegisterRawInputDevices(new[]{registration},1,(uint)Marshal.SizeOf(typeof(RawInputDevice))))
        {
            LastError=Marshal.GetLastWin32Error();
            return false;
        }
        window=hwnd;
        LastError=0;
        return true;
    }

    internal void Detach()
    {
        if(window==IntPtr.Zero) return;
        // RIDEV_REMOVE requires a null HWND, not the original target HWND.
        var registration=new RawInputDevice {UsagePage=1,Usage=2,Flags=RidevRemove,Target=IntPtr.Zero};
        if(!RegisterRawInputDevices(new[]{registration},1,(uint)Marshal.SizeOf(typeof(RawInputDevice))))
            LastError=Marshal.GetLastWin32Error();
        window=IntPtr.Zero;
    }

    internal void ProcessMessage(ref Message message)
    {
        if(disposed || window==IntPtr.Zero || message.Msg!=WmInput || message.HWnd!=window || message.LParam==IntPtr.Zero) return;
        uint required=0;
        uint queried=GetRawInputData(message.LParam,RidInput,IntPtr.Zero,ref required,NativeHeaderSize);
        if(queried!=0 || required<NativeHeaderSize+MousePayloadSize || required>MaximumPacketSize) return;
        if(buffer==IntPtr.Zero) buffer=Marshal.AllocHGlobal(MaximumPacketSize);
        uint available=MaximumPacketSize;
        uint read=GetRawInputData(message.LParam,RidInput,buffer,ref available,NativeHeaderSize);
        if(read==UInt32.MaxValue || read<NativeHeaderSize+MousePayloadSize || read>MaximumPacketSize) return;
        Marshal.Copy(buffer,packet,0,(int)read);
        int delta;
        if(TryReadWheel(packet,(int)read,out delta)) onWheel(delta);
        // The owning form must still call base.WndProc so Windows can perform
        // the documented foreground WM_INPUT cleanup through DefWindowProc.
    }

    internal static bool TryReadWheel(byte[] packet,out int delta)
    {
        return TryReadWheel(packet,packet==null ? 0 : packet.Length,out delta);
    }
    private static bool TryReadWheel(byte[] bytes,int available,out int delta)
    {
        delta=0;
        int minimum=HeaderSize+MousePayloadSize;
        if(bytes==null || available<minimum || available>bytes.Length || available>MaximumPacketSize) return false;
        uint type=BitConverter.ToUInt32(bytes,0),declaredSize=BitConverter.ToUInt32(bytes,4);
        if(type!=0 || declaredSize<(uint)minimum || declaredSize>(uint)available) return false;
        // RAWMOUSE's button union is DWORD-aligned at payload offset4. Its low
        // word is button flags, and its high word is a SIGNED wheel distance.
        ushort flags=BitConverter.ToUInt16(bytes,HeaderSize+4);
        if((flags&RiMouseWheel)==0 || (flags&RiMouseHorizontalWheel)!=0) return false;
        delta=BitConverter.ToInt16(bytes,HeaderSize+6);
        return delta!=0;
    }

    public void Dispose()
    {
        if(disposed) return;
        Detach();
        if(buffer!=IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer=IntPtr.Zero; }
        disposed=true;
    }
}
