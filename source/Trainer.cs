using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint kind, uint protection);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint kind);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attrs, UIntPtr stack, IntPtr start, IntPtr argument, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)] internal static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int size);
    [DllImport("kernel32.dll")] internal static extern ulong GetTickCount64();
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);

    internal static Exception Error(string operation)
    {
        return new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    internal static void LoadHook(Process game, string dll,string expectedGamePath)
    {
        // Resolve the actual DLL owning LoadLibraryW, including forwarded exports.
        IntPtr local = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
        IntPtr owner;
        if (local == IntPtr.Zero || !GetModuleHandleEx(6, local, out owner)) throw Error("Resolve LoadLibraryW");
        var ownerPath = new StringBuilder(32768);
        if (GetModuleFileName(owner, ownerPath, ownerPath.Capacity)==0) throw Error("Resolve loader module");
        ProcessModule remoteOwner = null;
        foreach (ProcessModule m in game.Modules)
        {
            if (String.Equals(m.ModuleName, System.IO.Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase))
            {
                if (!String.Equals(m.FileName, dll, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different copy of the speed module is already loaded. Restart Azeroth Survivors first.");
                return;
            }
            if (String.Equals(m.ModuleName, System.IO.Path.GetFileName(ownerPath.ToString()), StringComparison.OrdinalIgnoreCase)) remoteOwner=m;
        }
        if (remoteOwner==null) throw new InvalidOperationException("Could not find the Windows loader in Azeroth Survivors.");
        IntPtr remoteLoader = new IntPtr(remoteOwner.BaseAddress.ToInt64() + local.ToInt64() - owner.ToInt64());
        // Only this verified game receives a process handle, allocation, or thread.
        IntPtr process = OpenProcess(0x0010103A, false, game.Id);
        if (process==IntPtr.Zero) throw Error("Open Azeroth Survivors");
        IntPtr allocation=IntPtr.Zero, thread=IntPtr.Zero;
        bool threadFinished=false;
        try
        {
            var targetPath=new StringBuilder(32768);
            int targetPathLength=targetPath.Capacity;
            if (!QueryFullProcessImageName(process,0,targetPath,ref targetPathLength) ||
                !Target.SamePath(targetPath.ToString(),expectedGamePath) ||
                WaitForSingleObject(process,0)!=258)
                throw new InvalidOperationException("Azeroth Survivors changed or exited during attachment. Please retry.");
            byte[] bytes=Encoding.Unicode.GetBytes(dll+"\0");
            allocation=VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)bytes.Length, 0x3000, 4);
            if (allocation==IntPtr.Zero) throw Error("Allocate module path in Azeroth Survivors");
            UIntPtr written;
            if (!WriteProcessMemory(process,allocation,bytes,(UIntPtr)bytes.Length,out written) || written.ToUInt64()!=(ulong)bytes.Length)
                throw Error("Write module path in Azeroth Survivors");
            uint threadId;
            thread=CreateRemoteThread(process,IntPtr.Zero,UIntPtr.Zero,remoteLoader,allocation,0,out threadId);
            if (thread==IntPtr.Zero) throw Error("Load speed module in Azeroth Survivors");
            uint wait=WaitForSingleObject(thread,10000);
            threadFinished=wait==0;
            if (!threadFinished) throw new InvalidOperationException("The game did not finish loading the speed module. Restart the game before retrying.");
        }
        finally
        {
            // A timed-out loader might still read the path: retain that tiny buffer.
            if (allocation!=IntPtr.Zero && (thread==IntPtr.Zero || threadFinished)) VirtualFreeEx(process,allocation,UIntPtr.Zero,0x8000);
            if (thread!=IntPtr.Zero) CloseHandle(thread);
            CloseHandle(process);
        }
    }
}

internal static class GameTheme
{
    internal static readonly Color Canvas=Color.FromArgb(17,13,9);
    internal static readonly Color Panel=Color.FromArgb(25,18,12);
    internal static readonly Color Gold=Color.FromArgb(241,206,112);
    internal static readonly Color Border=Color.FromArgb(118,89,31);
    internal static readonly Color Body=Color.FromArgb(213,197,161);
    internal static readonly Color Muted=Color.FromArgb(182,161,122);
    internal static readonly Color Ready=Color.FromArgb(176,199,127);
    internal static readonly Color Warning=Color.FromArgb(234,169,112);

    internal static GraphicsPath Rounded(RectangleF r,float radius)
    {
        float d=radius*2;
        var p=new GraphicsPath();
        p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90);
        p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); p.AddArc(r.X,r.Bottom-d,d,d,90,90);
        p.CloseFigure(); return p;
    }
    internal static void Rule(Graphics g,float x1,float x2,float y,Color color)
    {
        using(var pen=new Pen(color)) g.DrawLine(pen,x1,y,x2,y);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    internal static void DarkCaption(IntPtr handle)
    {
        try
        {
            int dark=1;
            DwmSetWindowAttribute(handle,20,ref dark,4);
            int caption=Canvas.R | (Canvas.G<<8) | (Canvas.B<<16);
            int text=Gold.R | (Gold.G<<8) | (Gold.B<<16);
            DwmSetWindowAttribute(handle,35,ref caption,4);
            DwmSetWindowAttribute(handle,36,ref text,4);
        }
        catch(DllNotFoundException) { }
        catch(EntryPointNotFoundException) { }
    }
}

internal sealed class GamePanel : Panel
{
    internal GamePanel()
    {
        DoubleBuffered=true; BackColor=GameTheme.Panel;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var path=GameTheme.Rounded(new RectangleF(.5f,.5f,Width-1,Height-1),5))
        using(var fill=new LinearGradientBrush(ClientRectangle,Color.FromArgb(29,22,15),Color.FromArgb(14,11,8),90))
        using(var pen=new Pen(GameTheme.Border))
        {
            e.Graphics.FillPath(fill,path); e.Graphics.DrawPath(pen,path);
        }
    }
}

// Keep native Button input, tab navigation and accessibility; paint its surface.
internal sealed class GameButton : Button
{
    private bool hovered,pressed,selected;
    internal bool SpeedTile;
    internal Size DesignSize;
    internal string Shortcut="";
    internal bool Primary;
    internal bool Selected
    {
        get { return selected; }
        set { if(selected!=value) { selected=value; Invalidate(); } }
    }
    internal GameButton()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        FlatStyle=FlatStyle.Flat;
        FlatAppearance.BorderSize=0;
        UseVisualStyleBackColor=false;
        Cursor=Cursors.Hand;
        BackColor=GameTheme.Panel;
        ForeColor=GameTheme.Gold;
    }
    protected override void OnMouseEnter(EventArgs e) { hovered=true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered=false; pressed=false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if(e.Button==MouseButtons.Left) pressed=true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed=false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { if(!Enabled) {hovered=false; pressed=false;} Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if(e.KeyCode==Keys.Space) {pressed=true; Invalidate();} base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if(e.KeyCode==Keys.Space) {pressed=false; Invalidate();} base.OnKeyUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
        float scale=DesignSize.Width>0 ? Math.Min(Width/(float)DesignSize.Width,Height/(float)DesignSize.Height) : 1;
        Color top=Color.FromArgb(44,25,16), bottom=Color.FromArgb(22,13,9);
        Color edge=GameTheme.Border, ink=GameTheme.Gold, secondary=GameTheme.Muted;
        if(Primary || selected) { top=Color.FromArgb(85,24,14); bottom=Color.FromArgb(39,9,7); }
        if(hovered && Enabled) { top=Color.FromArgb(98,41,20); bottom=Color.FromArgb(48,18,10); edge=GameTheme.Gold; }
        if(selected && Enabled) edge=GameTheme.Gold;
        if(pressed && Enabled) { var swap=top; top=bottom; bottom=swap; }
        if(!Enabled) { top=Color.FromArgb(30,25,18); bottom=Color.FromArgb(22,18,13); edge=Color.FromArgb(82,67,40); ink=Color.FromArgb(159,144,112); secondary=Color.FromArgb(139,126,102); }
        using(var path=GameTheme.Rounded(new RectangleF(1,1,Width-3,Height-3),4*scale))
        using(var fill=new LinearGradientBrush(ClientRectangle,top,bottom,90))
        using(var pen=new Pen(edge,selected && Enabled ? 2*scale : scale))
        { g.FillPath(fill,path); g.DrawPath(pen,path); }
        if(Enabled)
            GameTheme.Rule(g,8*scale,Width-10*scale,3*scale,Color.FromArgb(selected ? 178 : 70,GameTheme.Gold));
        int dy=pressed && Enabled ? (int)Math.Max(1,scale) : 0;
        using(var font=new Font("Georgia",(SpeedTile ? 28 : 17)*scale,GraphicsUnit.Pixel))
        {
            var textRect=SpeedTile ? new Rectangle(4,(int)(17*scale)+dy,Width-8,(int)(36*scale)) : new Rectangle(4,dy,Width-8,Height-2);
            TextRenderer.DrawText(g,Text,font,textRect,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
        }
        if(SpeedTile)
        {
            using(var font=new Font("Segoe UI",11*scale,GraphicsUnit.Pixel))
                TextRenderer.DrawText(g,Shortcut,font,new Rectangle(4,(int)(61*scale)+dy,Width-8,(int)(18*scale)),secondary,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            if(selected && Enabled)
            {
                using(var pen=new Pen(GameTheme.Gold,1.7f*scale))
                { float x=Width-23*scale,y=13*scale; g.DrawLines(pen,new[]{new PointF(x,y+3*scale),new PointF(x+3*scale,y+6*scale),new PointF(x+9*scale,y)}); }
            }
        }
        if(Focused && ShowFocusCues && Enabled)
        {
            using(var pen=new Pen(GameTheme.Gold,scale))
            { pen.DashStyle=DashStyle.Dot; g.DrawRectangle(pen,6*scale,6*scale,Width-13*scale,Height-13*scale); }
        }
    }
}

internal sealed class TrainerForm : Form
{
    private readonly Label status = new Label();
    private readonly Label speedLabel = new Label();
    private readonly Label hotkeyLabel = new Label();
    private readonly GameButton attach = new GameButton();
    private readonly GameButton disconnect = new GameButton();
    private readonly GameButton[] speedButtons = new GameButton[5];
    private readonly Label connectionLabel = new Label();
    private readonly GameButton miniButton = new GameButton();
    private readonly GameButton browseButton = new GameButton();
    private readonly ToolTip statusDetails = new ToolTip();
    private readonly int[] speeds = {100,150,200,300,500};
    private readonly bool[] registered = new bool[5];
    private readonly WheelInput wheelInput;
    private readonly bool captureWheel;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private Process game;
    private MemoryMappedFile mapping;
    private MemoryMappedViewAccessor view;
    private DateTime attachedAt;
    private bool closing;
    private bool controlOwned;
    private int lastRequested=100;
    private int acknowledgedPercent=100;
    private bool connectionReady;
    private bool timingProbeStarted,timingObserved;
    private long timingProbeBaseline;
    private bool miniMode;
    private Rectangle fullBounds;
    private bool fullTopMost;
    private double fullOpacity=1;
    private int wheelRemainder;
    private bool dragHeld,dragMoved;
    private Point dragMouseStart,dragWindowStart;
    private string selectedGamePath;
    private bool explicitGameSelection;

    internal TrainerForm() : this(true) { }
    internal TrainerForm(bool enableWheelInput)
    {
        captureWheel=enableWheelInput;
        wheelInput=new WheelInput(OnWheelDelta);
        Text="Azeroth Survivors | Speed Trainer 1.0";
        ClientSize=new Size(740,526);
        FormBorderStyle=FormBorderStyle.None;
        MaximizeBox=false;
        StartPosition=FormStartPosition.CenterScreen;
        AutoScaleMode=AutoScaleMode.Dpi;
        AutoScaleDimensions=new SizeF(96,96);
        DoubleBuffered=true;
        KeyPreview=true;
        SetStyle(ControlStyles.StandardClick|ControlStyles.StandardDoubleClick,true);
        Font=new Font("Segoe UI",10);
        BackColor=GameTheme.Canvas;
        ForeColor=GameTheme.Body;
        var title=MakeLabel("AZEROTH SURVIVORS",24,42,692,55);
        title.Font=new Font("Garamond",29,FontStyle.Regular);
        title.ForeColor=GameTheme.Gold;
        title.TextAlign=ContentAlignment.MiddleCenter;
        WireDragSurface(title);
        var subtitle=MakeLabel("S P E E D   T R A I N E R   ·   1.0",24,101,692,24);
        subtitle.Font=new Font("Segoe UI",9);
        subtitle.TextAlign=ContentAlignment.MiddleCenter;
        subtitle.ForeColor=GameTheme.Muted;
        WireDragSurface(subtitle);
        browseButton.Text="Game files…";
        browseButton.DesignSize=new Size(126,26);
        browseButton.SetBounds(24,15,126,26);
        browseButton.AccessibleName="Choose Azeroth Survivors game executable";
        browseButton.Click+=BrowseGame;
        statusDetails.SetToolTip(browseButton,"Choose AzerothSurvivors.exe, or let Attach detect the running game.");
        Controls.Add(browseButton);
        miniButton.Text="Mini mode";
        miniButton.DesignSize=new Size(98,26);
        miniButton.SetBounds(546,15,98,26);
        miniButton.Enabled=false;
        miniButton.AccessibleName="Switch to mini speed display";
        miniButton.Click+=delegate { EnterMiniMode(); };
        statusDetails.SetToolTip(miniButton,"Attach to enable the mini speed display.");
        Controls.Add(miniButton);
        var minimizeButton=new GameButton {Text="−",DesignSize=new Size(30,26),AccessibleName="Minimize window"};
        minimizeButton.SetBounds(652,15,30,26);
        minimizeButton.Click+=delegate { WindowState=FormWindowState.Minimized; };
        statusDetails.SetToolTip(minimizeButton,"Minimize to taskbar");
        Controls.Add(minimizeButton);
        var closeButton=new GameButton {Text="×",DesignSize=new Size(30,26),AccessibleName="Close trainer and restore normal speed"};
        closeButton.SetBounds(690,15,30,26);
        closeButton.Click+=delegate { Close(); };
        statusDetails.SetToolTip(closeButton,"Close and restore 1x");
        Controls.Add(closeButton);
        var panel=new GamePanel();
        panel.SetBounds(24,146,692,266);
        Controls.Add(panel);
        var heading=new Label {Text="Game speed",Font=new Font("Georgia",14),ForeColor=GameTheme.Gold,BackColor=Color.Transparent};
        heading.SetBounds(22,18,330,29); panel.Controls.Add(heading);
        connectionLabel.SetBounds(418,22,250,23);
        connectionLabel.Text="Disconnected";
        connectionLabel.ForeColor=GameTheme.Muted;
        connectionLabel.BackColor=Color.Transparent;
        connectionLabel.TextAlign=ContentAlignment.MiddleRight;
        panel.Controls.Add(connectionLabel);
        status.SetBounds(22,61,646,46);
        status.Text="Start Azeroth Survivors, then attach to choose your pace.";
        status.ForeColor=GameTheme.Body;
        status.BackColor=Color.Transparent;
        status.AutoEllipsis=true;
        status.TextChanged+=delegate { statusDetails.SetToolTip(status,status.Text); };
        panel.Controls.Add(status);
        for(int i=0;i<speeds.Length;i++)
        {
            int percent=speeds[i];
            var b=new GameButton {Text=(percent/100.0).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"x",Enabled=false,SpeedTile=true,Shortcut="Ctrl+F"+(i+6),DesignSize=new Size(120,93)};
            b.SetBounds(22+i*132,119,120,93);
            b.AccessibleName="Set game speed to "+b.Text;
            b.AccessibleDescription=b.Shortcut;
            b.TabIndex=i;
            b.Click+=delegate { SetSpeed(percent); };
            speedButtons[i]=b;
            panel.Controls.Add(b);
        }
        speedLabel.SetBounds(22,230,646,22);
        speedLabel.Font=new Font("Segoe UI",9);
        speedLabel.ForeColor=GameTheme.Muted;
        speedLabel.BackColor=Color.Transparent;
        speedLabel.Text="Closing the trainer restores normal speed.";
        panel.Controls.Add(speedLabel);
        attach.Text="Attach to Azeroth";
        attach.SetBounds(24,432,334,45);
        attach.DesignSize=new Size(334,45);
        attach.Primary=true;
        attach.TabIndex=1;
        attach.Click+=AttachClicked;
        Controls.Add(attach);
        disconnect.Text="Restore 1x / Disconnect";
        disconnect.SetBounds(370,432,346,45);
        disconnect.DesignSize=new Size(346,45);
        disconnect.TabIndex=2;
        disconnect.Enabled=false;
        disconnect.Click+=delegate { Disconnect(); };
        Controls.Add(disconnect);
        hotkeyLabel.SetBounds(24,487,692,19);
        hotkeyLabel.Text="Shift + wheel anywhere in Azeroth. Ctrl+F6–F10 selects a speed.";
        hotkeyLabel.Font=new Font("Segoe UI",8.5f);
        hotkeyLabel.ForeColor=GameTheme.Muted;
        hotkeyLabel.BackColor=Color.Transparent;
        hotkeyLabel.TextAlign=ContentAlignment.MiddleCenter;
        Controls.Add(hotkeyLabel);
        timer.Interval=100;
        timer.Tick+=Tick;
        timer.Start();
        FormClosing+=OnClosing;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        GameTheme.DarkCaption(Handle);
        if(captureWheel && connectionReady && wheelInput!=null) wheelInput.Attach(Handle);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if(wheelInput!=null) wheelInput.Detach();
        // Opacity/style changes may recreate a handle on some Windows versions.
        // Clear registrations so the heartbeat tick can register the new handle.
        for(int i=0;i<registered.Length;i++)
        {
            if(registered[i] && IsHandleCreated) Native.UnregisterHotKey(Handle,100+i);
            registered[i]=false;
        }
        base.OnHandleDestroyed(e);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g=e.Graphics;
        g.SmoothingMode=SmoothingMode.AntiAlias;
        if(miniMode)
        {
            using(var fill=new LinearGradientBrush(ClientRectangle,Color.FromArgb(47,24,12),GameTheme.Canvas,90)) g.FillRectangle(fill,ClientRectangle);
            using(var pen=new Pen(GameTheme.Border)) g.DrawRectangle(pen,1,1,Width-3,Height-3);
            using(var font=new Font("Georgia",28,GraphicsUnit.Pixel))
                TextRenderer.DrawText(g,SpeedText(acknowledgedPercent),font,ClientRectangle,GameTheme.Gold,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            return;
        }
        using(var fill=new LinearGradientBrush(ClientRectangle,Color.FromArgb(39,20,11),GameTheme.Canvas,90)) g.FillRectangle(fill,ClientRectangle);
        float sx=ClientSize.Width/740f,sy=ClientSize.Height/526f;
        var saved=g.Save();
        g.ScaleTransform(sx,sy);
        using(var border=new Pen(GameTheme.Border)) g.DrawRectangle(border,8,8,723,509);
        using(var soft=new Pen(Color.FromArgb(59,43,21))) g.DrawRectangle(soft,11,11,717,503);
        GameTheme.Rule(g,168,354,133,GameTheme.Border);
        GameTheme.Rule(g,386,572,133,GameTheme.Border);
        using(var pen=new Pen(GameTheme.Gold))
            g.DrawPolygon(pen,new[]{new Point(370,128),new Point(375,133),new Point(370,138),new Point(365,133)});
        // Small brass corners echo the game's inset menu panels.
        using(var pen=new Pen(GameTheme.Gold))
        {
            foreach(int x in new[]{8,731}) foreach(int y in new[]{8,517})
            { int dx=x==8 ? 1 : -1,dy=y==8 ? 1 : -1; g.DrawLine(pen,x,y,x+16*dx,y); g.DrawLine(pen,x,y,x,y+16*dy); }
        }
        g.Restore(saved);
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing) { if(wheelInput!=null) wheelInput.Dispose(); statusDetails.Dispose(); timer.Dispose(); }
        base.Dispose(disposing);
    }

    private Label MakeLabel(string text,int x,int y,int w,int h)
    {
        var l=new Label {Text=text,BackColor=Color.Transparent}; l.SetBounds(x,y,w,h); Controls.Add(l); return l;
    }
    private static string SpeedText(int percent)
    {
        return (percent/100.0).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"x";
    }
    private void WireDragSurface(Control surface)
    {
        surface.Cursor=Cursors.SizeAll;
        surface.MouseDown+=BeginWindowDrag;
        surface.MouseMove+=MoveWindowDrag;
        surface.MouseUp+=EndWindowDrag;
    }
    private void BeginWindowDrag(object sender,MouseEventArgs e)
    {
        if(e.Button!=MouseButtons.Left || e.Clicks>1) return;
        dragHeld=true; dragMoved=false;
        dragMouseStart=Cursor.Position; dragWindowStart=Location;
        ((Control)sender).Capture=true;
    }
    private void MoveWindowDrag(object sender,MouseEventArgs e)
    {
        if(!dragHeld || (Control.MouseButtons&MouseButtons.Left)==0) return;
        Point mouse=Cursor.Position;
        int dx=mouse.X-dragMouseStart.X,dy=mouse.Y-dragMouseStart.Y;
        if(!dragMoved && Math.Abs(dx)<SystemInformation.DragSize.Width/2 && Math.Abs(dy)<SystemInformation.DragSize.Height/2) return;
        dragMoved=true;
        Location=new Point(dragWindowStart.X+dx,dragWindowStart.Y+dy);
    }
    private void EndWindowDrag(object sender,MouseEventArgs e)
    {
        dragHeld=false; ((Control)sender).Capture=false;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        BeginWindowDrag(this,e); base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        MoveWindowDrag(this,e); base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        EndWindowDrag(this,e); base.OnMouseUp(e);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if(miniMode && e.Button==MouseButtons.Left) ExitMiniMode(true);
        base.OnMouseDoubleClick(e);
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        if(miniMode && keyData==Keys.Escape) { ExitMiniMode(true); return true; }
        return base.ProcessCmdKey(ref msg,keyData);
    }
    private void EnterMiniMode()
    {
        if(miniMode || !connectionReady) return;
        fullBounds=Bounds; fullTopMost=TopMost; fullOpacity=Opacity;
        SyncHotkeys(false);
        miniMode=true; wheelRemainder=0; dragHeld=false;
        SuspendLayout();
        foreach(Control c in Controls) c.Visible=false;
        // The full controls retain their layout while this same form is compact.
        ClientSize=new Size(112,62);
        var screen=Screen.FromRectangle(fullBounds).WorkingArea;
        Location=new Point(Math.Max(screen.Left,Math.Min(fullBounds.Right-Width,screen.Right-Width)),Math.Max(screen.Top,Math.Min(fullBounds.Top,screen.Bottom-Height)));
        TopMost=true;
        Opacity=0.5;
        Cursor=Cursors.SizeAll;
        AccessibleName="Azeroth speed "+SpeedText(acknowledgedPercent);
        statusDetails.SetToolTip(this,"Shift + wheel anywhere while Azeroth or the trainer is active. Double-click or Esc: full window. Drag to move.");
        ResumeLayout(false);
        Invalidate();
    }
    private void ExitMiniMode(bool activate)
    {
        if(!miniMode) return;
        SuspendLayout();
        SyncHotkeys(false);
        miniMode=false; wheelRemainder=0; dragHeld=false; Capture=false;
        TopMost=fullTopMost;
        Opacity=fullOpacity;
        Bounds=fullBounds;
        foreach(Control c in Controls) c.Visible=true;
        Cursor=Cursors.Default;
        AccessibleName="Azeroth Survivors speed trainer";
        statusDetails.SetToolTip(this,null);
        ResumeLayout(false);
        Invalidate(true);
        if(activate && !closing) Activate();
    }
    private void OnWheelDelta(int delta)
    {
        HandleSpeedWheel(delta,(Native.GetAsyncKeyState(0x10)&0x8000)!=0,HasAllowedFocus());
    }
    private bool HandleSpeedWheel(int delta,bool shift,bool allowedFocus)
    {
        if(!shift || !allowedFocus || !connectionReady)
        {
            wheelRemainder=0;
            return false;
        }
        wheelRemainder+=delta;
        int steps=wheelRemainder/120;
        wheelRemainder%=120;
        if(steps!=0)
        {
            int index=Array.IndexOf(speeds,lastRequested);
            if(index<0) index=0;
            int next=Math.Max(0,Math.Min(speeds.Length-1,index+steps));
            if(next!=index) SetSpeed(speeds[next]);
        }
        return true;
    }
    private void AttachClicked(object sender,EventArgs args)
    {
        if(view!=null) return;
        Process candidate=null;
        try
        {
            attach.Enabled=false;
            connectionLabel.Text="Connecting";
            connectionLabel.ForeColor=GameTheme.Gold;
            status.Text="Checking game compatibility…";
            status.Refresh();
            var candidates=Process.GetProcessesByName(Target.ProcessName);
            try
            {
                if(candidates.Length==0) throw new InvalidOperationException("Start AzerothSurvivors.exe first, then click Attach.");
                if(explicitGameSelection)
                {
                    foreach(var running in candidates)
                    {
                        string runningPath;
                        try
                        {
                            var module=running.MainModule;
                            if(module==null) continue;
                            runningPath=module.FileName;
                        }
                        catch(System.ComponentModel.Win32Exception) { continue; }
                        catch(InvalidOperationException) { continue; }
                        if(Target.SamePath(runningPath,selectedGamePath))
                        {
                            if(candidate!=null) throw new InvalidOperationException("More than one copy of the selected game is running. Close the extra copy first.");
                            candidate=running;
                        }
                    }
                    if(candidate==null) throw new InvalidOperationException("The selected game is not running or accessible. Start that copy with matching privileges, or choose its executable with Game files…");
                }
                else
                {
                    if(candidates.Length!=1) throw new InvalidOperationException("Several game installations are running. Use Game files… to select the one to control.");
                    candidate=candidates[0];
                }
                string actual=Target.NormalizeGamePath(candidate.MainModule.FileName);
                string dll=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AzerothSpeedHook.dll");
                if(!File.Exists(dll)) throw new FileNotFoundException("Keep AzerothSpeedHook.dll beside the trainer executable.");
                using(var file=new FileStream(actual,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    // Accept compatible updates by their actual engine/timer
                    // structure, rather than pinning one executable fingerprint.
                    TargetCompatibility.Verify(file);
                    mapping=MemoryMappedFile.CreateOrOpen("Local\\AzerothSurvivorsSpeed_"+candidate.Id,Target.ControlSize,MemoryMappedFileAccess.ReadWrite);
                    using(var header=mapping.CreateViewAccessor(0,48,MemoryMappedFileAccess.ReadWrite))
                    {
                        uint magic=header.ReadUInt32(0),version=header.ReadUInt32(4);
                        if(magic!=0 && (magic!=Target.Magic || version!=Target.ProtocolVersion))
                            throw new InvalidOperationException("An incompatible speed module is already active. Restart Azeroth Survivors.");
                    }
                    view=mapping.CreateViewAccessor(0,Target.ControlSize,MemoryMappedFileAccess.ReadWrite);
                    controlOwned=true;
                    timingProbeStarted=false;
                    timingObserved=false;
                    view.Write(32,(ulong)0);
                    view.Write(12,100);
                    Target.WriteExpectedPath(view,actual);
                    view.Write(8,Process.GetCurrentProcess().Id);
                    view.Write(4,Target.ProtocolVersion);
                    view.Write(0,Target.Magic);
                    view.Write(32,Native.GetTickCount64());
                    Native.LoadHook(candidate,dll,actual);
                }
                selectedGamePath=actual;
                statusDetails.SetToolTip(browseButton,"Selected game: "+actual);
                browseButton.Enabled=false;
                game=candidate; candidate=null;
                attachedAt=DateTime.UtcNow;
                lastRequested=100;
                disconnect.Enabled=true;
                status.Text="Waiting for the game’s timing module…";
            }
            finally { foreach(var p in candidates) if(p!=game) p.Dispose(); }
        }
        catch(Exception e)
        {
            ReleaseMapping();
            connectionLabel.Text="Unable to connect";
            connectionLabel.ForeColor=GameTheme.Warning;
            status.Text="Attach failed. "+e.Message;
            MessageBox.Show(this,e.Message,"Azeroth speed trainer",MessageBoxButtons.OK,MessageBoxIcon.Information);
            attach.Enabled=true;
        }
    }
    private void BrowseGame(object sender,EventArgs args)
    {
        if(view!=null) return;
        using(var picker=new OpenFileDialog())
        {
            picker.Title="Choose Azeroth Survivors";
            picker.Filter="Azeroth Survivors (AzerothSurvivors.exe)|AzerothSurvivors.exe";
            picker.CheckFileExists=true;
            picker.Multiselect=false;
            if(!String.IsNullOrEmpty(selectedGamePath) && File.Exists(selectedGamePath))
                picker.InitialDirectory=Path.GetDirectoryName(selectedGamePath);
            if(picker.ShowDialog(this)!=DialogResult.OK) return;
            try
            {
                string verified=Target.VerifySelection(picker.FileName);
                selectedGamePath=verified;
                explicitGameSelection=true;
                statusDetails.SetToolTip(browseButton,"Selected game: "+verified);
                status.Text="Game selected. Start this copy of Azeroth Survivors, then click Attach.";
                connectionLabel.Text="Game selected";
                connectionLabel.ForeColor=GameTheme.Muted;
            }
            catch(Exception e)
            {
                status.Text="Game selection failed. "+e.Message;
                MessageBox.Show(this,e.Message,"Select Azeroth Survivors",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
        }
    }
    private void SetSpeed(int percent)
    {
        if(view==null || game==null || game.HasExited || view.ReadInt32(20)!=1) return;
        view.Write(32,Native.GetTickCount64());
        view.Write(12,percent);
        lastRequested=percent;
    }
    private bool HasAllowedFocus()
    {
        uint pid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out pid);
        return pid==(uint)Process.GetCurrentProcess().Id || (game!=null && pid==(uint)game.Id);
    }
    private void SyncHotkeys(bool active)
    {
        for(int i=0;i<speeds.Length;i++)
        {
            if(active && !registered[i]) registered[i]=Native.RegisterHotKey(Handle,100+i,0x4002,(uint)(0x75+i));
            else if(!active && registered[i]) { Native.UnregisterHotKey(Handle,100+i); registered[i]=false; }
        }
        hotkeyLabel.Text=captureWheel && connectionReady && !wheelInput.IsAttached
            ? "Wheel shortcut unavailable. Use the speed buttons or Ctrl+F6–F10."
            : active && registered.Any(r=>!r)
            ? "Some hotkeys are in use by another app. Use the speed buttons."
            : "Shift + wheel anywhere in Azeroth. Ctrl+F6–F10 selects a speed.";
    }
    protected override void WndProc(ref Message m)
    {
        if(wheelInput!=null) wheelInput.ProcessMessage(ref m);
        // Raw Input also delivers over this window. Use legacy wheel messages
        // only as a fallback, otherwise one detent would be counted twice.
        if(m.Msg==0x20A && (wheelInput==null || !wheelInput.IsAttached))
        {
            long word=m.WParam.ToInt64();
            int delta=unchecked((short)(word>>16));
            if(HandleSpeedWheel(delta,(word&4)!=0,HasAllowedFocus())) { m.Result=IntPtr.Zero; return; }
        }
        if(m.Msg==0x312 && HasAllowedFocus())
        {
            int index=m.WParam.ToInt32()-100;
            if(index>=0 && index<speeds.Length) SetSpeed(speeds[index]);
        }
        base.WndProc(ref m);
    }
    private void Tick(object sender,EventArgs args)
    {
        if(closing) return;
        bool ready=false;
        if(view!=null && game!=null)
        {
            if(game.HasExited) { Disconnect(); status.Text="Game exited. Start the game and attach again."; return; }
            view.Write(32,Native.GetTickCount64());
            int state=view.ReadInt32(20), applied=view.ReadInt32(16);
            if(state==3)
            {
                uint error=view.ReadUInt32(24);
                Disconnect(); status.Text="The timing hook failed (Windows error "+error+")."; return;
            }
            // An existing mapping can contain calls from an earlier attachment.
            // Wait for 1x acknowledgement, then observe NEW calls this session.
            if(!timingObserved)
            {
                if(state==1 && applied==100)
                {
                    long calls=view.ReadInt64(40);
                    if(!timingProbeStarted) { timingProbeBaseline=calls; timingProbeStarted=true; }
                    else if(calls>timingProbeBaseline) timingObserved=true;
                }
                else timingProbeStarted=false;
            }
            ready=state==1 && timingObserved;
            if(ready)
            {
                int previous=acknowledgedPercent;
                acknowledgedPercent=applied;
                if(miniMode && previous!=applied)
                {
                    AccessibleName="Azeroth speed "+SpeedText(applied);
                    Invalidate();
                }
                connectionLabel.Text="Connected";
                connectionLabel.ForeColor=GameTheme.Ready;
                speedLabel.Text="Current speed: "+(applied/100.0).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"x. Closing the trainer restores 1x.";
                status.Text=applied!=lastRequested ? "Changing game speed…" : "Choose a speed below, or use its keyboard shortcut.";
            }
            else if((DateTime.UtcNow-attachedAt).TotalSeconds>6)
            {
                Disconnect(); status.Text="The game's timing hook did not become active. Restart Azeroth; this build may require a trainer update."; return;
            }
        }
        if(connectionReady!=ready)
            statusDetails.SetToolTip(miniButton,ready ? "Always on top, 50% opacity. Shift + wheel works anywhere in Azeroth; double-click restores this panel." : "Attach to enable the mini speed display.");
        connectionReady=ready;
        if(captureWheel)
        {
            if(ready && !wheelInput.IsAttached) wheelInput.Attach(Handle);
            else if(!ready && wheelInput.IsAttached) wheelInput.Detach();
        }
        miniButton.Enabled=ready;
        if(miniMode && !ready) ExitMiniMode(false);
        for(int i=0;i<speedButtons.Length;i++)
        {
            speedButtons[i].Enabled=ready;
            speedButtons[i].Selected=ready && view.ReadInt32(16)==speeds[i];
        }
        SyncHotkeys(ready && HasAllowedFocus());
    }
    private void ReleaseMapping()
    {
        if(view!=null)
        {
            if(controlOwned)
            {
                view.Write(12,100);
                view.Write(32,(ulong)0);
            }
            view.Dispose(); view=null;
        }
        if(mapping!=null) { mapping.Dispose(); mapping=null; }
        controlOwned=false;
    }
    private void Disconnect()
    {
        ExitMiniMode(false);
        wheelInput.Detach();
        connectionReady=false;
        timingProbeStarted=false;
        timingObserved=false;
        miniButton.Enabled=false;
        acknowledgedPercent=100;
        wheelRemainder=0;
        SyncHotkeys(false);
        if(view!=null)
        {
            view.Write(12,100);
            view.Write(32,Native.GetTickCount64());
            var watch=Stopwatch.StartNew();
            while(view.ReadInt32(16)!=100 && watch.ElapsedMilliseconds<700) Thread.Sleep(20);
        }
        ReleaseMapping();
        if(game!=null) { game.Dispose(); game=null; }
        foreach(var b in speedButtons) { b.Enabled=false; b.Selected=false; }
        attach.Enabled=true;
        browseButton.Enabled=true;
        disconnect.Enabled=false;
        connectionLabel.Text="Disconnected";
        connectionLabel.ForeColor=GameTheme.Muted;
        speedLabel.Text="Normal speed requested. Restart the game to fully unload the trainer.";
        status.Text="Disconnected. Attach again to change the game speed.";
    }
    private void OnClosing(object sender,FormClosingEventArgs args)
    {
        closing=true;
        timer.Stop();
        Disconnect();
    }
}

internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        bool created;
        using(var singleton=new Mutex(true,"Local\\AzerothSurvivorsSpeedTrainerUI_v1",out created))
        {
            if(!created) { MessageBox.Show("The standalone Azeroth speed trainer is already running."); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrainerForm());
        }
    }
}
