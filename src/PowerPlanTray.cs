// PowerPlanTray - 任务栏电源计划快速切换
// 编译: csc /target:winexe /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll PowerPlanTray.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PowerPlanTray
{
    internal static class Native
    {
        [DllImport("powrprof.dll", SetLastError = true)]
        internal static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);

        [DllImport("powrprof.dll", SetLastError = true)]
        internal static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint PowerReadFriendlyName(IntPtr root, ref Guid guid,
            IntPtr subGroup, IntPtr setting, StringBuilder buffer, ref uint size);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr h);

        [DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr h);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        internal static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

        [DllImport("user32.dll")]
        internal static extern bool BringWindowToTop(IntPtr hWnd);

        // uxtheme 未公开导出：让系统菜单跟随深色主题，失败就保持浅色
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        private static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        private static extern void FlushMenuThemes();

        internal static void EnableDarkMenus(IntPtr hwnd)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (!(v is int) || (int)v != 0) return;
                }
                SetPreferredAppMode(2);
                AllowDarkModeForWindow(hwnd, true);
                FlushMenuThemes();
            }
            catch { }
        }

        // 前台锁会挡住 SetForegroundWindow，先临时共享输入队列再抢前台
        internal static void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return;
            uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint my = GetCurrentThreadId();
            bool attached = false;
            if (fgThread != 0 && fgThread != my) attached = AttachThreadInput(fgThread, my, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(fgThread, my, false);
            }
        }

        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string text);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y,
            int reserved, IntPtr hWnd, IntPtr rect);

        internal const uint MF_STRING = 0x0000;
        internal const uint MF_GRAYED = 0x0001;
        internal const uint MF_CHECKED = 0x0008;
        internal const uint MF_SEPARATOR = 0x0800;

        internal const uint TPM_LEFTALIGN = 0x0000;
        internal const uint TPM_RIGHTBUTTON = 0x0002;
        internal const uint TPM_RETURNCMD = 0x0100;
        internal const uint TPM_WORKAREA = 0x10000;

        internal const uint WM_NULL = 0x0000;
    }

    internal class PlanInfo
    {
        public Guid Guid;
        public string Name;        // 本地化名称
        public string EnglishName; // 用于排序/配色
        public int Rank;
        public int Level;          // 图标电量
        public Color Color;
    }

    internal static class Plans
    {
        // Win11“电源模式”覆盖层，不属于可切换电源计划，菜单里不列出
        private static readonly string[] OverlayGuids = new string[]
        {
            "3af9b8d9-7c97-431d-ad78-34a8bfea439f", // High performance overlay
            "961cc777-2547-4f9d-8174-7d86181b8a7a", // Better battery life overlay
            "ded574b5-45a0-4f42-8737-46345c09c238"  // Max performance overlay
        };

        internal static Guid GetActive()
        {
            IntPtr p;
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out p) == 0 && p != IntPtr.Zero)
            {
                Guid g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                Native.LocalFree(p);
                return g;
            }
            return Guid.Empty;
        }

        internal static bool SetActive(Guid g)
        {
            Guid gg = g;
            return Native.PowerSetActiveScheme(IntPtr.Zero, ref gg) == 0;
        }

        internal static string ReadFriendly(Guid g, string fallbackRaw)
        {
            uint size = 0;
            Guid gg = g;
            Native.PowerReadFriendlyName(IntPtr.Zero, ref gg, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (size > 0 && size < 4096)
            {
                StringBuilder sb = new StringBuilder((int)size / 2 + 4);
                gg = g;
                if (Native.PowerReadFriendlyName(IntPtr.Zero, ref gg, IntPtr.Zero, IntPtr.Zero, sb, ref size) == 0)
                {
                    string s = sb.ToString().Trim('\0', ' ');
                    if (s.Length > 0) return s;
                }
            }
            // 兜底：MUI 字符串形如 "@dll,-id,English"
            if (!string.IsNullOrEmpty(fallbackRaw))
            {
                int i = fallbackRaw.LastIndexOf(',');
                if (i >= 0 && i + 1 < fallbackRaw.Length) return fallbackRaw.Substring(i + 1);
                return fallbackRaw.TrimStart('@');
            }
            return g.ToString();
        }

        internal static List<PlanInfo> Load()
        {
            List<PlanInfo> list = new List<PlanInfo>();
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes"))
            {
                if (root == null) return list;
                foreach (string sub in root.GetSubKeyNames())
                {
                    Guid g;
                    if (!Guid.TryParse(sub, out g)) continue;
                    if (Array.IndexOf(OverlayGuids, sub.ToLowerInvariant()) >= 0) continue;

                    string raw = null;
                    using (RegistryKey k = root.OpenSubKey(sub))
                    {
                        if (k != null) raw = k.GetValue("FriendlyName") as string;
                    }

                    PlanInfo p = new PlanInfo();
                    p.Guid = g;
                    p.EnglishName = EnglishOf(raw);
                    p.Name = ReadFriendly(g, raw);
                    Style(p);
                    list.Add(p);
                }
            }
            list.Sort(delegate (PlanInfo a, PlanInfo b)
            {
                int c = a.Rank.CompareTo(b.Rank);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
            return list;
        }

        private static string EnglishOf(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int i = raw.LastIndexOf(',');
            if (i >= 0 && i + 1 < raw.Length) return raw.Substring(i + 1).Trim();
            return raw.Trim();
        }

        private static void Style(PlanInfo p)
        {
            string e = p.EnglishName.ToLowerInvariant();
            if (e.Contains("ultimate"))
            {
                p.Rank = 4; p.Level = 100; p.Color = Color.FromArgb(203, 60, 60);
            }
            else if (e.Contains("high performance"))
            {
                p.Rank = 3; p.Level = 78; p.Color = Color.FromArgb(232, 130, 40);
            }
            else if (e.Contains("power saver"))
            {
                p.Rank = 1; p.Level = 22; p.Color = Color.FromArgb(40, 168, 100);
            }
            else if (e.Contains("balanced"))
            {
                p.Rank = 2; p.Level = 50; p.Color = Color.FromArgb(0, 120, 212);
            }
            else
            {
                p.Rank = 5; p.Level = 50; p.Color = Color.FromArgb(130, 130, 130);
            }
        }

        internal static PlanInfo Find(List<PlanInfo> plans, Guid g)
        {
            foreach (PlanInfo p in plans) if (p.Guid == g) return p;
            return null;
        }
    }

    internal class TrayApp : IDisposable
    {
        private const int CmdPlanBase = 1000;
        private const int CmdPowerCpl = 2000;
        private const int CmdSettings = 2001;
        private const int CmdStartup = 2002;
        private const int CmdRefresh = 2003;
        private const int CmdExit = 2004;

        private readonly NotifyIcon notify;
        private readonly Form host;
        private readonly System.Windows.Forms.Timer timer;
        private Icon currentIcon;
        private Guid lastActive = Guid.Empty;

        internal TrayApp()
        {
            Native.SetProcessDPIAware();

            // 隐藏宿主窗口：让系统菜单能正常取得前台与鼠标捕获，点击别处即可收起
            host = new Form();
            host.FormBorderStyle = FormBorderStyle.None;
            host.ShowInTaskbar = false;
            host.StartPosition = FormStartPosition.Manual;
            host.Bounds = new Rectangle(-32000, -32000, 1, 1);
            host.Opacity = 0;
            host.Show();
            Native.EnableDarkMenus(host.Handle);

            notify = new NotifyIcon();
            notify.Visible = true;
            notify.MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) ShowMenu();
            };

            Refresh(GetActive());

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { PollActive(); };
            timer.Start();
        }

        private void ShowMenu()
        {
            List<PlanInfo> plans = Plans.Load();
            Guid active = GetActive();
            bool hasStartup = File.Exists(StartupLnk);

            IntPtr h = Native.CreatePopupMenu();
            if (h == IntPtr.Zero) return;
            try
            {
                Native.AppendMenu(h, Native.MF_STRING | Native.MF_GRAYED, UIntPtr.Zero, "电源计划");
                Native.AppendMenu(h, Native.MF_SEPARATOR, UIntPtr.Zero, null);

                if (plans.Count == 0)
                {
                    Native.AppendMenu(h, Native.MF_STRING | Native.MF_GRAYED, UIntPtr.Zero, "未能读取电源计划");
                }
                for (int i = 0; i < plans.Count; i++)
                {
                    uint flags = Native.MF_STRING;
                    if (plans[i].Guid == active) flags |= Native.MF_CHECKED;
                    Native.AppendMenu(h, flags, (UIntPtr)(CmdPlanBase + i), plans[i].Name);
                }

                Native.AppendMenu(h, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                Native.AppendMenu(h, Native.MF_STRING, (UIntPtr)CmdPowerCpl, "电源选项（控制面板）");
                Native.AppendMenu(h, Native.MF_STRING, (UIntPtr)CmdSettings, "Windows 设置 · 电源和电池");
                Native.AppendMenu(h, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                Native.AppendMenu(h, hasStartup ? (Native.MF_STRING | Native.MF_CHECKED) : Native.MF_STRING,
                    (UIntPtr)CmdStartup, "开机自动启动");
                Native.AppendMenu(h, Native.MF_STRING, (UIntPtr)CmdRefresh, "刷新");
                Native.AppendMenu(h, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                Native.AppendMenu(h, Native.MF_STRING, (UIntPtr)CmdExit, "退出");

                Native.ForceForeground(host.Handle);

                Point pt = Cursor.Position;
                int cmd = Native.TrackPopupMenu(h,
                    Native.TPM_LEFTALIGN | Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_WORKAREA,
                    pt.X, pt.Y, 0, host.Handle, IntPtr.Zero);
                Native.PostMessage(host.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

                Dispatch(cmd, plans);
            }
            finally
            {
                Native.DestroyMenu(h);
            }
        }

        private void Dispatch(int cmd, List<PlanInfo> plans)
        {
            if (cmd >= CmdPlanBase && cmd < CmdPlanBase + plans.Count)
            {
                Switch(plans[cmd - CmdPlanBase]);
                return;
            }
            switch (cmd)
            {
                case CmdPowerCpl:
                    Shell("control.exe", "powercfg.cpl");
                    break;
                case CmdSettings:
                    Shell("ms-settings:powersleep", null);
                    break;
                case CmdStartup:
                    ToggleStartup();
                    break;
                case CmdRefresh:
                    Refresh(GetActive());
                    break;
                case CmdExit:
                    ExitApp();
                    break;
            }
        }

        private void ToggleStartup()
        {
            try
            {
                if (File.Exists(StartupLnk))
                {
                    File.Delete(StartupLnk);
                    ShowBalloon("开机自动启动", "已关闭", ToolTipIcon.None);
                }
                else
                {
                    CreateStartupShortcut();
                    ShowBalloon("开机自动启动", "已开启", ToolTipIcon.None);
                }
            }
            catch (Exception ex)
            {
                ShowBalloon("设置失败", ex.Message, ToolTipIcon.Warning);
            }
        }

        private void Switch(PlanInfo p)
        {
            if (Plans.SetActive(p.Guid))
            {
                lastActive = p.Guid;
                ApplyIcon(p);
                ShowBalloon("电源计划：" + p.Name, "已切换", ToolTipIcon.None);
            }
            else
            {
                ShowBalloon("切换失败", "系统拒绝了切换请求：" + p.Name, ToolTipIcon.Error);
            }
        }

        private void PollActive()
        {
            Guid g = GetActive();
            if (g != Guid.Empty && g != lastActive) Refresh(g);
        }

        private void Refresh(Guid active)
        {
            List<PlanInfo> plans = Plans.Load();
            PlanInfo p = Plans.Find(plans, active);
            lastActive = active;
            if (p != null) ApplyIcon(p);
        }

        private void ApplyIcon(PlanInfo p)
        {
            Icon ic = MakeIcon(p.Color, p.Level);
            Icon old = currentIcon;
            notify.Icon = ic;
            currentIcon = ic;
            if (old != null) old.Dispose();
            notify.Text = Truncate("电源计划：" + p.Name, 63);
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            try { notify.ShowBalloonTip(1500, title, text, icon); }
            catch { }
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static void Shell(string file, string arg)
        {
            try
            {
                if (arg == null) Process.Start(file);
                else Process.Start(file, arg);
            }
            catch { }
        }

        internal static string StartupLnk
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "PowerPlanTray.lnk");
            }
        }

        internal static void CreateStartupShortcut()
        {
            string lnk = StartupLnk;
            string exe = Application.ExecutablePath;
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type sct = sc.GetType();
            sct.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
            sct.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exe) });
            sct.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "任务栏电源计划快速切换" });
            sct.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
            sct.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            Marshal.ReleaseComObject(sc);
            Marshal.ReleaseComObject(shell);
        }

        // ---------- 图标绘制 ----------

        // 输出 16/20/24/32 多尺寸 ICO，16px 由 128px 高品质缩放，缩到托盘后仍清晰
        internal static Icon MakeIcon(Color color, int level)
        {
            int[] sizes = new int[] { 16, 20, 24, 32 };
            List<Bitmap> bmps = new List<Bitmap>();
            Bitmap master = RenderIcon(128, color, level);
            try
            {
                foreach (int sz in sizes)
                {
                    Bitmap d = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(d))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.DrawImage(master, new Rectangle(0, 0, sz, sz),
                            new Rectangle(0, 0, master.Width, master.Height), GraphicsUnit.Pixel);
                    }
                    bmps.Add(d);
                }
                return BuildIco(bmps);
            }
            finally
            {
                master.Dispose();
                foreach (Bitmap b in bmps) b.Dispose();
            }
        }

        private static Bitmap RenderIcon(int px, Color color, int level)
        {
            Bitmap bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                float s = px / 32f;
                Color shell = Color.FromArgb(247, 247, 247);
                Color edge = Color.FromArgb(30, 30, 30);
                float pen = Math.Max(1.9f * s, 0.9f);

                RectangleF body = new RectangleF(1.8f * s, 7.2f * s, 22.4f * s, 17.6f * s);
                using (GraphicsPath p = RoundRectF(body, 3.4f * s))
                {
                    using (SolidBrush b = new SolidBrush(shell)) g.FillPath(b, p);
                    using (Pen pp = new Pen(edge, pen)) g.DrawPath(pp, p);
                }

                RectangleF cap = new RectangleF(24.6f * s, 12.4f * s, 4.8f * s, 7.2f * s);
                using (GraphicsPath p = RoundRectF(cap, 1.1f * s))
                {
                    using (SolidBrush b = new SolidBrush(shell)) g.FillPath(b, p);
                    using (Pen pp = new Pen(edge, pen)) g.DrawPath(pp, p);
                }

                float left = 4.4f * s;
                float wide = 17.2f * s * (level / 100f);
                if (wide > 0.5f)
                {
                    using (SolidBrush b = new SolidBrush(color))
                        g.FillRectangle(b, left, 9.6f * s, wide, 12.8f * s);
                }
            }
            return bmp;
        }

        private static GraphicsPath RoundRectF(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 多尺寸 ICO，各尺寸以 PNG 负载写入，保留透明通道
        private static Icon BuildIco(List<Bitmap> bitmaps)
        {
            List<byte[]> pngs = new List<byte[]>();
            foreach (Bitmap b in bitmaps)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }

            using (MemoryStream ico = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ico))
            {
                w.Write((ushort)0);            // reserved
                w.Write((ushort)1);            // type: icon
                w.Write((ushort)pngs.Count);   // count
                int offset = 6 + 16 * pngs.Count;
                for (int i = 0; i < pngs.Count; i++)
                {
                    Bitmap b = bitmaps[i];
                    w.Write((byte)(b.Width >= 256 ? 0 : b.Width));
                    w.Write((byte)(b.Height >= 256 ? 0 : b.Height));
                    w.Write((byte)0);          // palette
                    w.Write((byte)0);          // reserved
                    w.Write((ushort)1);        // planes
                    w.Write((ushort)32);       // bpp
                    w.Write((uint)pngs[i].Length);
                    w.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] png in pngs) w.Write(png);
                w.Flush();
                ico.Position = 0;
                Icon ic = new Icon(ico);
                return (Icon)ic.Clone();
            }
        }

        private static Guid GetActive()
        {
            return Plans.GetActive();
        }

        internal void ExitApp()
        {
            timer.Stop();
            notify.Visible = false;
            notify.Dispose();
            Application.Exit();
        }

        public void Dispose()
        {
            if (currentIcon != null) currentIcon.Dispose();
            if (timer != null) timer.Dispose();
            if (notify != null) notify.Dispose();
        }
    }

    internal static class Program
    {
        private static Mutex mutex;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string first = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            try
            {
                if (first == "--set" || first == "-s")
                {
                    if (args.Length < 2) return 2;
                    Guid g;
                    if (!Guid.TryParse(args[1], out g))
                    {
                        List<PlanInfo> list = Plans.Load();
                        PlanInfo hit = null;
                        foreach (PlanInfo p in list)
                            if (string.Equals(p.Name, args[1], StringComparison.CurrentCultureIgnoreCase) ||
                                string.Equals(p.EnglishName, args[1], StringComparison.OrdinalIgnoreCase)) { hit = p; break; }
                        if (hit == null) return 3;
                        g = hit.Guid;
                    }
                    return Plans.SetActive(g) ? 0 : 1;
                }
            }
            catch
            {
                return 1;
            }

            bool created;
            mutex = new Mutex(true, "Local\\PowerPlanTray_SingleInstance", out created);
            if (!created) return 0;

            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Log(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Log(e.ExceptionObject as Exception);
            };

            TrayApp app = new TrayApp();
            Application.Run();
            app.Dispose();
            return 0;
        }

        private static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerPlanTray");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
