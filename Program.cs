// BrightTool — minimalist monitor brightness control via DDC/CI (ddxva2 low-level API).
// Works with LG 22MK400H and any monitor/scale with DDC/CI enabled.
//
// Build (see build.cmd):
//   bright.exe     -> console tool:  bright | bright 70 | bright +10 | bright c 80
//   BrightTray.exe -> tray icon: slider for brightness/contrast (left-click), Exit on right-click.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BrightTool
{
    // ------------------------------------------------------------------
    // DDC/CI brightness over the Windows low-level monitor API (dxva2.dll)
    // ------------------------------------------------------------------
    internal static class Brightness
    {
        public const byte CODE_BRIGHTNESS = 0x10; // VCP code: luminance/brightness
        public const byte CODE_CONTRAST   = 0x12; // VCP code: contrast

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc;
        }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr pType, ref uint current, ref uint maximum);
        [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] monitors);

        // target list a monitor enumeration writes into (callback can't capture).
        static List<PHYSICAL_MONITOR> state;
        static bool CollectInto(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data)
        {
            var monitors = state;
            uint n = 0;
            if (monitors != null && GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref n) && n > 0)
            {
                var arr = new PHYSICAL_MONITOR[n];
                if (GetPhysicalMonitorsFromHMONITOR(hMonitor, n, arr))
                    monitors.AddRange(arr);
            }
            return true;
        }

        // Runs body with every physical monitor open; handles are destroyed afterwards.
        static void UsingMonitors(Action<List<PHYSICAL_MONITOR>> body)
        {
            var monitors = new List<PHYSICAL_MONITOR>();
            MonitorEnumProc cb = CollectInto;
            state = monitors;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            state = null;
            try { body(monitors); }
            finally { if (monitors.Count > 0) DestroyPhysicalMonitors((uint)monitors.Count, monitors.ToArray()); }
        }

        /// Current value (0..100) of a setting across all monitors. -1 when unreadable (e.g. DDC/CI off).
        /// Retries: some monitors need a moment to answer after a write.
        public static int Get(byte code)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int p = ReadOnce(code);
                if (p >= 0) return p;
                System.Threading.Thread.Sleep(150);
            }
            return -1;
        }

        static int ReadOnce(byte code)
        {
            double sum = 0, tot = 0; int ok = 0;
            UsingMonitors(mon =>
            {
                foreach (var m in mon)
                {
                    uint cur = 0, max = 0;
                    if (GetVCPFeatureAndVCPFeatureReply(m.hPhysicalMonitor, code, IntPtr.Zero, ref cur, ref max) && max > 0)
                    { sum += cur; tot += max; ok++; }
                }
            });
            return ok > 0 ? (int)Math.Round(sum * 100.0 / tot) : -1;
        }

        /// Sets a value (0..100) on every monitor that supports the setting.
        /// Returns 0 = all OK, 1 = partial, -1 = none changed.
        public static int Set(byte code, int pct)
        {
            if (pct < 0) pct = 0; if (pct > 100) pct = 100;
            int ok = 0, tried = 0;
            UsingMonitors(mon =>
            {
                foreach (var m in mon)
                {
                    uint cur = 0, max = 0;
                    if (GetVCPFeatureAndVCPFeatureReply(m.hPhysicalMonitor, code, IntPtr.Zero, ref cur, ref max) && max > 0)
                    {
                        tried++;
                        if (SetVCPFeature(m.hPhysicalMonitor, code, (uint)Math.Round(pct * max / 100.0)))
                            ok++;
                    }
                }
            });
            return tried > 0 && ok == tried ? 0 : (ok > 0 ? 1 : -1);
        }
    }

    // ------------------------------------------------------------------
    // Shared argument handling (used by both executables)
    // ------------------------------------------------------------------
    internal static class Cli
    {
        /// Applies one command. quiet=true suppresses output (tray exe called with args).
        /// Returns exit code: 0 success, 1 partial, 2 usage, 3 unsupported/failed.
        public static int Apply(string[] args, bool quiet)
        {
            string name = "brightness";
            byte code = Brightness.CODE_BRIGHTNESS;
            int skip = 0;

            if (args != null && args.Length > 0)
            {
                byte c; string n;
                if (SettingName(args[0], out c, out n)) { code = c; name = n; skip = 1; }
            }

            var rest = Slice(args, skip);
            if (rest.Length > 1) { if (!quiet) Usage(); return 2; }

            string arg = rest.Length == 1 ? rest[0] : null;

            if (arg == null || arg == "get")
            {
                int cur = Brightness.Get(code);
                if (cur < 0) { if (!quiet) NoDdc(name); return 3; }
                if (!quiet) Console.WriteLine("Current {0}: {1}%", name, cur);
                return 0;
            }

            if (arg == "help" || arg == "/?" || arg == "-h" || arg == "--help")
            { if (!quiet) Usage(); return 0; }

            int req, delta = 0;
            bool relative = arg.StartsWith("+") || arg.StartsWith("-");
            if (!int.TryParse(arg, out req) && !(relative && int.TryParse(arg.Substring(1), out req)))
            { if (!quiet) Usage(); return 2; }

            if (relative)
            {
                delta = req;
                int cur = Brightness.Get(code);
                if (cur < 0) { if (!quiet) NoDdc(name); return 3; }
                req = cur + delta;
            }
            if (req < 0) req = 0; if (req > 100) req = 100;

            int rc = Brightness.Set(code, req);
            if (rc < 0) { if (!quiet) NoDdc(name); return 3; }
            if (!quiet)
            {
                Console.WriteLine(rc == 0 ? "{0} set to {1}%" : "Set on some monitors: {1}%", Cap(name), req);
                if (rc != 0) Console.WriteLine("(Some monitors didn't respond to the command.)");
            }
            return rc == 0 ? 0 : 1;
        }

        // "b"/"bright"/"brightness" -> brightness, "c"/"contrast" -> contrast
        static bool SettingName(string s, out byte code, out string name)
        {
            switch (s.ToLowerInvariant())
            {
                case "b": case "bright": case "brightness":
                    code = Brightness.CODE_BRIGHTNESS; name = "brightness"; return true;
                case "c": case "contrast":
                    code = Brightness.CODE_CONTRAST; name = "contrast"; return true;
            }
            code = 0; name = null; return false;
        }

        static string[] Slice(string[] a, int skip)
        {
            var r = new string[a.Length - skip];
            for (int i = 0; i < r.Length; i++) r[i] = a[skip + i];
            return r;
        }

        static string Cap(string s) { return char.ToUpperInvariant(s[0]) + s.Substring(1); }

        static void Usage()
        {
            Console.WriteLine("Monitor brightness/contrast control (DDC/CI)");
            Console.WriteLine();
            Console.WriteLine("  bright               show current brightness");
            Console.WriteLine("  bright 70            set brightness to 70%");
            Console.WriteLine("  bright +10 / -20     nudge brightness");
            Console.WriteLine("  bright c             show current contrast");
            Console.WriteLine("  bright c 80          set contrast to 80%");
            Console.WriteLine();
            Console.WriteLine("Full setting names also work: bright brightness 70, bright contrast get.");
            Console.WriteLine("If nothing works, enable DDC/CI in the monitor's OSD menu");
            Console.WriteLine("(LG: Menu > General/Other > DDC/CI > On).");
        }

        static void NoDdc(string what)
        {
            Console.Error.WriteLine("Could not talk to the monitor (DDC/CI); {0} unavailable.", what);
            Console.Error.WriteLine("Check the monitor OSD: Menu > General/Other > DDC/CI > On.");
        }
    }

    // ------------------------------------------------------------------
    // Program entry
    // ------------------------------------------------------------------
    internal static class Program
    {
#if TRAY
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDPIAware();
            if (args != null && args.Length > 0)     // BrightTray.exe 70 -> set silently
            { Environment.Exit(Cli.Apply(args, quiet: true)); }

            Application.EnableVisualStyles();
            Application.Run(new TrayContext());
        }
#else
        static int Main(string[] args)
        {
            return Cli.Apply(args, quiet: false);
        }
#endif
    }

#if TRAY
    // Controls the HKCU Run entry so the tray app starts with Windows.
    static class RunAtLogin
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BrightTray";

        public static bool Enabled
        {
            get
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(ValueName) != null;
            }
        }

        public static void Set(bool on)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) throw new System.IO.IOException("Run key missing");
                if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else
                {
                    if (k.GetValue(ValueName) == null) return;
                    k.DeleteValue(ValueName);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Tray icon + slider flyout
    // ------------------------------------------------------------------
    sealed class TrayContext : ApplicationContext
    {
        readonly NotifyIcon icon;
        Flyout flyout;

        public TrayContext()
        {
            var login = new ToolStripMenuItem("Run at login")
            {
                CheckOnClick = true,
                Checked = RunAtLogin.Enabled,
            };
            login.Click += (s, e) =>
            {
                try { RunAtLogin.Set(login.Checked); }
                catch { login.Checked = RunAtLogin.Enabled; }   // revert on failure
            };

            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) => { login.Checked = RunAtLogin.Enabled; };
            menu.Items.Add(login);
            menu.Items.Add("Exit", null, (s, e) =>
            {
                icon.Visible = false;
                icon.Dispose();
                if (flyout != null && !flyout.IsDisposed) flyout.Close();
                ExitThread();
            });

            icon = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "Monitor brightness",
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
        }

        void ToggleFlyout()
        {
            if (flyout != null && !flyout.IsDisposed && flyout.Visible) { flyout.Close(); return; }
            flyout = new Flyout();

            var p = Cursor.Position;
            var wa = Screen.FromPoint(p).WorkingArea;
            int x = p.X - flyout.Width / 2;
            int y = p.Y < wa.Bottom - 300 ? wa.Top + 4 : wa.Bottom - flyout.Height - 4; // taskbar top or bottom
            if (x < wa.Left) x = wa.Left;
            if (x + flyout.Width > wa.Right) x = wa.Right - flyout.Width;
            flyout.Location = new Point(x, y);

            flyout.Sync();
            flyout.Show();
            flyout.Activate();
            flyout.SelectBar();
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bmp))
            using (var b = new SolidBrush(Color.FromArgb(255, 255, 205, 70)))
            using (var p = new Pen(b, 1.6f))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(b, 4.5f, 4.5f, 7f, 7f);
                for (int a = 0; a < 8; a++)
                {
                    double ang = a * Math.PI / 4;
                    float x1 = 8f + (float)Math.Cos(ang) * 5.6f, y1 = 8f + (float)Math.Sin(ang) * 5.6f;
                    float x2 = 8f + (float)Math.Cos(ang) * 7.2f, y2 = 8f + (float)Math.Sin(ang) * 7.2f;
                    g.DrawLine(p, x1, y1, x2, y2);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    sealed class Flyout : Form
    {
        readonly FluentSlider bar;
        readonly Label label;
        readonly Timer timer;
        readonly Button btnB, btnC;
        byte mode = Brightness.CODE_BRIGHTNESS;
        bool syncing;

        public Flyout()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = MaximizeBox = false;
            BackColor = Color.FromArgb(32, 32, 32);
            KeyPreview = true;
            Padding = new Padding(12, 8, 12, 10);
            ClientSize = new Size(276, 124);

            label = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = Ui(9.75f),
                ForeColor = Color.FromArgb(238, 238, 238),
            };
            bar = new FluentSlider { Dock = DockStyle.Fill };

            var header = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.FromArgb(32, 32, 32) };
            btnB = MakeToggle("Brightness");
            btnC = MakeToggle("Contrast");
            btnB.Click += (s, e) => Toggle(Brightness.CODE_BRIGHTNESS);
            btnC.Click += (s, e) => Toggle(Brightness.CODE_CONTRAST);
            header.Controls.Add(btnC);   // later-added control docks leftmost
            header.Controls.Add(btnB);

            Controls.Add(bar);
            Controls.Add(header);
            Controls.Add(label);

            timer = new Timer { Interval = 60 };
            timer.Tick += (s, e) => { timer.Stop(); Brightness.Set(mode, bar.Value); };
            bar.ValueChanged += (s, e) =>
            {
                label.Text = Title() + bar.Value + "%";
                if (!syncing) { timer.Stop(); timer.Start(); }
            };

            Deactivate += (s, e) => Close();
        }

        // DWM: 33 = corner preference, 2 = DWMWCP_ROUND (rounded corners + DWM shadow,
        // compositor-side, no cost to us). Older Windows ignores the call gracefully.
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, 4);
        }

        // Segoe UI Variable (regular weight) on Win11, graceful fallback on older builds.
        static Font Ui(float pt)
        {
            foreach (string name in new[] { "Segoe UI Variable Text", "Segoe UI" })
            {
                try { return new Font(new FontFamily(name), pt, FontStyle.Regular); }
                catch (ArgumentException) { }
            }
            return new Font(FontFamily.GenericSansSerif, pt, FontStyle.Regular);
        }

        Button MakeToggle(string text)
        {
            var b = new Button
            {
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Left,
                Width = 126,
                Text = text,
                Font = Ui(9.5f),
                BackColor = Color.FromArgb(32, 32, 32),
                ForeColor = Color.FromArgb(150, 150, 150),
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 43, 43);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(48, 48, 48);
            return b;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnClosed(EventArgs e)
        {
            timer.Stop();
            timer.Dispose();
            base.OnClosed(e);
        }

        public void SelectBar() { bar.Select(); bar.Focus(); }

        /// Reads the monitor's current value into the slider (for the active mode).
        public void Sync()
        {
            SetButtons();
            ReadCurrent();
        }

        void Toggle(byte m)
        {
            if (m == mode) return;
            mode = m;
            SetButtons();
            ReadCurrent();
        }

        void SetButtons()
        {
            var on = Color.FromArgb(76, 194, 255);    // Win11 dark-theme accent
            var off = Color.FromArgb(150, 150, 150);
            btnB.ForeColor = mode == Brightness.CODE_BRIGHTNESS ? on : off;
            btnC.ForeColor = mode == Brightness.CODE_CONTRAST ? on : off;
        }

        void ReadCurrent()
        {
            syncing = true;
            int p = Brightness.Get(mode);
            if (p < 0)
            {
                bar.Value = 50;
                label.Text = "Not supported (check monitor OSD)";
            }
            else
            {
                bar.Value = Math.Min(100, Math.Max(0, p));
                label.Text = Title() + p + "%";
            }
            syncing = false;
        }

        string Title() { return mode == Brightness.CODE_BRIGHTNESS ? "Brightness  " : "Contrast  "; }
    }

    // Windows 11 style slider, hand-painted with GDI+: thin rounded track, accent
    // fill, round thumb that grows on hover/drag. Double-buffered; the few brushes
    // are shared so painting costs next to nothing.
    sealed class FluentSlider : Control
    {
        const int EDGE = 12;             // side margin, leaves room for the thumb

        static readonly Pen trackPen = new Pen(Color.FromArgb(93, 93, 93), 4f);
        static readonly Pen fillPen = new Pen(Color.FromArgb(76, 194, 255), 4f);
        static readonly Brush thumbBrush = new SolidBrush(Color.White);
        static readonly Pen focusPen = new Pen(Color.FromArgb(110, 255, 255, 255), 2f);

        public event EventHandler ValueChanged;

        int val = 50;
        bool hot, dragging;

        public FluentSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(32, 32, 32);
            TabStop = true;
        }

        public int Value
        {
            get { return val; }
            set
            {
                int v = Math.Max(0, Math.Min(100, value));
                if (v == val) return;
                val = v;
                Invalidate();
                var h = ValueChanged; if (h != null) h(this, EventArgs.Empty);
            }
        }

        int FillX { get { return EDGE + (int)Math.Round((Width - 2 * EDGE) * (val / 100.0)); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cy = Height / 2;
            trackPen.StartCap = trackPen.EndCap = LineCap.Round;
            fillPen.StartCap = fillPen.EndCap = LineCap.Round;

            g.DrawLine(trackPen, EDGE, cy, Width - EDGE, cy);
            if (FillX > EDGE) g.DrawLine(fillPen, EDGE, cy, FillX, cy);

            float r = hot || dragging ? 9f : 7f;
            if (Focused) g.DrawEllipse(focusPen, FillX - r - 3f, cy - r - 3f, 2 * (r + 3f), 2 * (r + 3f));
            g.FillEllipse(thumbBrush, FillX - r, cy - r, 2 * r, 2 * r);
        }

        void SetFromX(int x)
        {
            x = Math.Max(EDGE, Math.Min(Width - EDGE, x));
            Value = (int)Math.Round((x - EDGE) * 100.0 / (Width - 2 * EDGE));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { dragging = true; Capture = true; Focus(); SetFromX(e.X); }
            else if (!hot) { hot = true; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFromX(e.X);
            else if (!hot) { hot = true; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; Capture = false; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot) { hot = false; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value += Math.Sign(e.Delta);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = 0;
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: step = -1; break;
                case Keys.Right: case Keys.Up: step = 1; break;
                case Keys.PageDown: step = -10; break;
                case Keys.PageUp: step = 10; break;
                case Keys.Home: Value = 0; e.Handled = e.SuppressKeyPress = true; return;
                case Keys.End: Value = 100; e.Handled = e.SuppressKeyPress = true; return;
            }
            if (step != 0) { Value += step; e.Handled = e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }
    }
#endif
}
