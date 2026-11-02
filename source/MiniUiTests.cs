using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class MiniUiTests
{
    private static int failures;
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object Get(TrainerForm f,string name) { return typeof(TrainerForm).GetField(name,Flags).GetValue(f); }
    private static void Set(TrainerForm f,string name,object value) { typeof(TrainerForm).GetField(name,Flags).SetValue(f,value); }
    private static object Call(TrainerForm f,string name,params object[] args) { return typeof(TrainerForm).GetMethod(name,Flags).Invoke(f,args); }
    private static void Check(bool value,string message)
    {
        Console.WriteLine((value ? "PASS " : "FAIL ")+message);
        if(!value) failures++;
    }
    private static void Tick(TrainerForm f) { Call(f,"Tick",null,EventArgs.Empty); }
    private static void Wheel(TrainerForm f,int delta,bool shift=true,bool allowedFocus=true) { Call(f,"HandleSpeedWheel",delta,shift,allowedFocus); }
    private static void Render(TrainerForm f,string path,bool halfAlpha)
    {
        ((Timer)Get(f,"timer")).Stop();
        f.ShowInTaskbar=false;
        f.StartPosition=FormStartPosition.Manual;
        f.Location=new Point(-16000,-16000);
        f.Show(); Application.DoEvents();
        using(var bitmap=new Bitmap(f.ClientSize.Width,f.ClientSize.Height))
        {
            f.DrawToBitmap(bitmap,new Rectangle(Point.Empty,f.ClientSize));
            if(!halfAlpha) bitmap.Save(path,ImageFormat.Png);
            else using(var alpha=new Bitmap(bitmap.Width,bitmap.Height,PixelFormat.Format32bppArgb))
            using(var graphics=Graphics.FromImage(alpha))
            using(var attributes=new ImageAttributes())
            {
                var matrix=new ColorMatrix(); matrix.Matrix33=.5f;
                attributes.SetColorMatrix(matrix);
                graphics.DrawImage(bitmap,new Rectangle(0,0,alpha.Width,alpha.Height),0,0,bitmap.Width,bitmap.Height,GraphicsUnit.Pixel,attributes);
                alpha.Save(path,ImageFormat.Png);
            }
        }
        f.Hide();
    }
    [STAThread] private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(args[0]);
        using(var f=new TrainerForm(false))
        {
            f.CreateControl();
            f.Bounds=new Rectangle(150,150,740,526);
            var timer=(Timer)Get(f,"timer");
            Check(f.FormBorderStyle==FormBorderStyle.None,"full window has no Windows title bar or border");
            Call(f,"EnterMiniMode");
            Check(!(bool)Get(f,"miniMode"),"mini mode cannot conceal an unattached trainer");
            Render(f,Path.Combine(args[0],"Full-Preview.png"),false);

            // Substitute only the UI's IPC endpoint. This process is never injected
            // and no real game/anti-cheat process is opened or modified.
            var map=MemoryMappedFile.CreateNew("Local\\AzerothMiniUiTest_"+Guid.NewGuid(),Target.ControlSize);
            var view=map.CreateViewAccessor(0,Target.ControlSize);
            view.Write(0,Target.Magic); view.Write(4,Target.ProtocolVersion); view.Write(12,100); view.Write(16,200); view.Write(20,1);
            view.Write(40,(long)100); // historical activity from an old attachment
            Set(f,"mapping",map); Set(f,"view",view);
            Set(f,"game",Process.GetCurrentProcess()); Set(f,"controlOwned",true);
            Set(f,"attachedAt",DateTime.UtcNow);
            Check(((GameButton)Get(f,"browseButton")).Enabled,"portable game selection is available before attachment");
            Tick(f);
            Check(!((GameButton)Get(f,"miniButton")).Enabled,"timing verification waits for normal-speed acknowledgement");
            view.Write(16,100);
            Tick(f);
            Check(!((GameButton)Get(f,"miniButton")).Enabled,"historical timing calls do not enable a new attachment");
            Tick(f);
            Check(!((GameButton)Get(f,"miniButton")).Enabled,"unchanged timing count keeps controls disabled");
            view.Write(40,(long)101);
            Tick(f);
            Check(((GameButton)Get(f,"miniButton")).Enabled,"mini button enables after ready acknowledgement");
            f.Bounds=new Rectangle(150,150,740,526);
            Rectangle original=f.Bounds;
            timer.Start();
            Call(f,"EnterMiniMode");
            Check((bool)Get(f,"miniMode") && f.ClientSize==new Size(112,62),"mini mode uses compact 112 by 62 client area");
            Check(f.TopMost && Math.Abs(f.Opacity-.5)<.001,"mini is always on top at exactly 50% opacity");
            Check(timer.Enabled,"mini keeps heartbeat timer running");
            timer.Stop();
            Check(f.Controls.Cast<Control>().All(c=>!c.Visible),"mini hides all full-panel controls");
            Wheel(f,120,false); Check(view.ReadInt32(12)==100,"plain wheel does not change speed");
            Wheel(f,120,true,false); Check(view.ReadInt32(12)==100,"wheel while another app is focused does not change speed");
            Wheel(f,60); Check(view.ReadInt32(12)==100,"half-detent is accumulated without skipping speed");
            Wheel(f,60); Check(view.ReadInt32(12)==150,"Shift wheel up requests 1.5x");
            Wheel(f,120); Check(view.ReadInt32(12)==200,"rapid next detent requests 2x before acknowledgement");
            Check((int)Get(f,"acknowledgedPercent")==100,"display retains acknowledged speed while request is pending");
            view.Write(16,200); Tick(f);
            Check((int)Get(f,"acknowledgedPercent")==200,"mini display updates after speed acknowledgement");
            Render(f,Path.Combine(args[0],"Mini-Preview.png"),true);
            Wheel(f,120); Check(view.ReadInt32(12)==300,"Shift wheel up requests 3x");
            Wheel(f,120); Check(view.ReadInt32(12)==500,"next detent reaches 5x without hovering mini");
            Wheel(f,120); Check(view.ReadInt32(12)==500,"upper 5x limit does not wrap");
            Wheel(f,-120); Check(view.ReadInt32(12)==300,"Shift wheel down from 5x requests 3x");
            Wheel(f,-360); Check(view.ReadInt32(12)==100,"downward detents reach minimum 1x");
            Wheel(f,-120); Check(view.ReadInt32(12)==100,"lower limit does not wrap");
            Call(f,"OnMouseDoubleClick",new MouseEventArgs(MouseButtons.Left,2,20,20,0));
            Check(!(bool)Get(f,"miniMode") && f.Bounds==original,"double-click restores full window size and position");
            Check(!f.TopMost && f.Opacity==1,"full mode restores ordinary stacking and full opacity");
            Wheel(f,120); Check(view.ReadInt32(12)==150,"Shift wheel also works with full panel instead of mini");
            Wheel(f,-120); Check(view.ReadInt32(12)==100,"full-panel shortcut can restore 1x");
            var buttons=(GameButton[])Get(f,"speedButtons");
            Check(buttons.Length==5 && buttons[4].AccessibleDescription=="Ctrl+F10","5x has a fifth button and Ctrl+F10 shortcut");
            Check(buttons.All(b=>b.Right<=b.Parent.ClientSize.Width),"five speed buttons fit inside the control panel");
            for(int i=0;i<20;i++) { Call(f,"EnterMiniMode"); Call(f,"ExitMiniMode",false); }
            Check(f.Bounds==original,"repeated toggles do not drift full layout");
            view.Write(16,100); Tick(f);
            Call(f,"EnterMiniMode"); view.Write(32,(ulong)0); Tick(f);
            Check(view.ReadUInt64(32)>0,"heartbeat continues updating while mini is active");
            view.Write(20,2); Tick(f);
            Check(!(bool)Get(f,"miniMode"),"connection expiry restores full panel");
            view.Write(20,1); Tick(f); Call(f,"EnterMiniMode");
            Call(f,"Disconnect");
            Check(!(bool)Get(f,"miniMode") && Get(f,"view")==null,"disconnect exits mini and releases IPC endpoint");
            Check(!f.TopMost && f.Opacity==1,"disconnect clears topmost and transparency");
            Check(((GameButton)Get(f,"browseButton")).Enabled,"disconnect permits selecting a different installation");
            f.Close();
        }
        Console.WriteLine(failures==0 ? "All mini UI integration checks passed." : failures+" checks failed.");
        return failures==0 ? 0 : 1;
    }
}
