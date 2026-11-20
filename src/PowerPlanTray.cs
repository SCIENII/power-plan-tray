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

        // winexe 没有控制台，命令行模式下要把文字写回父进程的终端
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool WriteConsoleW(IntPtr handle, string text, int length,
            out int written, IntPtr reserved);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static IntPtr consoleOut;
        private static bool consoleTried;

        // 只有父进程带控制台时才输出；重定向到文件/管道拿不到，故另有 --list <文件>
        internal static bool TryConsole()
        {
            if (consoleTried) return consoleOut != IntPtr.Zero && consoleOut != new IntPtr(-1);
            consoleTried = true;
            if (!AttachConsole(-1)) return false;
            consoleOut = CreateFileW("CONOUT$", 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
            return consoleOut != IntPtr.Zero && consoleOut != new IntPtr(-1);
        }

        internal static void WriteLine(string text)
        {
            if (!TryConsole()) return;
            string line = text + "\r\n";
            int written;
            WriteConsoleW(consoleOut, line, line.Length, out written, IntPtr.Zero);
        }

        internal static void WriteLines(string[] lines)
        {
            foreach (string line in lines) WriteLine(line);
        }

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
        public string Label;       // 菜单显示名：重名时追加序号
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
            Disambiguate(list);
            return list;
        }

        // 同一台机器上可能装着同名计划（第三方工具导入的副本），菜单里必须能区分
        internal static void Disambiguate(List<PlanInfo> plans)
        {
            Dictionary<string, int> totals = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (PlanInfo p in plans)
            {
                int n;
                totals.TryGetValue(p.Name, out n);
                totals[p.Name] = n + 1;
            }
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (PlanInfo p in plans)
            {
                int total;
                totals.TryGetValue(p.Name, out total);
                if (total <= 1)
                {
                    p.Label = p.Name;
                    continue;
                }
                int index;
                seen.TryGetValue(p.Name, out index);
                index++;
                seen[p.Name] = index;
                p.Label = p.Name + " (" + index + ")";
            }
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
        private const int MaxPlans = CmdPowerCpl - CmdPlanBase;

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
                for (int i = 0; i < plans.Count && i < MaxPlans; i++)
                {
                    uint flags = Native.MF_STRING;
                    if (plans[i].Guid == active) flags |= Native.MF_CHECKED;
                    Native.AppendMenu(h, flags, (UIntPtr)(CmdPlanBase + i), plans[i].Label);
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
            if (cmd >= CmdPlanBase && cmd < CmdPlanBase + Math.Min(plans.Count, MaxPlans))
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
                ApplyIcon(p.Color, p.Level, p.Name);
                ShowBalloon("电源计划：" + p.Label, "已切换", ToolTipIcon.None);
            }
            else
            {
                ShowBalloon("切换失败", "系统拒绝了切换请求：" + p.Label, ToolTipIcon.Error);
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
            if (p != null)
            {
                ApplyIcon(p.Color, p.Level, "电源计划：" + p.Name);
                return;
            }
            // 活动方案没出现在注册表清单里（或读取失败）时也必须留一个图标，否则托盘直接空掉
            ApplyIcon(Color.FromArgb(130, 130, 130), 50,
                active == Guid.Empty ? "电源计划：未知" : "电源计划：" + active);
        }

        private void ApplyIcon(Color color, int level, string tooltip)
        {
            Icon ic = MakeIcon(color, level);
            Icon old = currentIcon;
            notify.Icon = ic;
            currentIcon = ic;
            if (old != null) old.Dispose();
            notify.Text = Truncate(tooltip, 63);
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
            object shell = null;
            object sc = null;
            try
            {
                shell = Activator.CreateInstance(t);
                sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type sct = sc.GetType();
                sct.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                sct.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exe) });
                sct.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "任务栏电源计划快速切换" });
                sct.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
                sct.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            finally
            {
                if (sc != null && Marshal.IsComObject(sc)) Marshal.ReleaseComObject(sc);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
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
                using (Icon raw = new Icon(ico))
                {
                    return (Icon)raw.Clone();
                }
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
        internal const string Version = "1.1.0";

        internal const int ExitOk = 0;
        internal const int ExitFailed = 1;
        internal const int ExitUsage = 2;
        internal const int ExitUnknownPlan = 3;
        internal const int ExitAmbiguous = 4;

        private static Mutex mutex;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                int code = CommandLine(args);
                if (code >= 0) return code;
            }
            catch (Exception ex)
            {
                Log(ex);
                Native.WriteLine("出错了：" + ex.Message);
                return ExitFailed;
            }

            bool created;
            mutex = new Mutex(true, "Local\\PowerPlanTray_SingleInstance", out created);
            if (!created)
            {
                Native.WriteLine("PowerPlanTray 已经在运行（托盘里那颗电池图标）。");
                return ExitOk;
            }

            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Log(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Log(e.ExceptionObject as Exception);
            };

            TrayApp app = new TrayApp();
            Application.Run();
            app.Dispose();
            return ExitOk;
        }

        // 返回 -1 表示没有命令行动作，应该启动托盘
        private static int CommandLine(string[] args)
        {
            string first = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (first)
            {
                case "":
                    return -1;
                case "--help":
                case "-h":
                case "-?":
                    Native.WriteLines(Usage());
                    return ExitOk;
                case "--version":
                case "-v":
                    Native.WriteLines(new string[]
                    {
                        "PowerPlanTray " + Version,
                        ".NET Framework " + Environment.Version,
                        Environment.OSVersion.VersionString,
                    });
                    return ExitOk;
                case "--list":
                case "-l":
                    return ListPlans(args.Length > 1 ? args[1] : null);
                case "--set":
                case "-s":
                    return SetPlan(args);
                default:
                    Native.WriteLine("无法识别的参数：" + args[0]);
                    Native.WriteLines(Usage());
                    return ExitUsage;
            }
        }

        private static string[] Usage()
        {
            return new string[]
            {
                "PowerPlanTray " + Version + " - 任务栏电源计划快速切换",
                "",
                "用法：",
                "  PowerPlanTray.exe                       启动托盘程序（无参数）",
                "  PowerPlanTray.exe --set <计划名|GUID>    切换电源计划",
                "  PowerPlanTray.exe --list [文件路径]      列出电源计划；.json 存 JSON，.txt 存文本",
                "  PowerPlanTray.exe --version             显示版本",
                "  PowerPlanTray.exe --help                显示本帮助",
                "",
                "返回码：0 成功 / 1 切换失败 / 2 用法错误 / 3 找不到计划 / 4 计划名重复（请改用 GUID）",
                "说明：程序是 winexe，这些文字只在从 cmd / PowerShell 启动时打印到终端；",
                "      需要机器可读结果时用 --list <文件> 或返回码。",
            };
        }

        private static int SetPlan(string[] args)
        {
            if (args.Length < 2 || string.IsNullOrEmpty(args[1]))
            {
                Native.WriteLines(Usage());
                return ExitUsage;
            }
            List<PlanInfo> plans = Plans.Load();
            Guid g;
            PlanInfo target;
            if (Guid.TryParse(args[1], out g))
            {
                target = Plans.Find(plans, g);
            }
            else
            {
                bool ambiguous;
                target = Match(plans, args[1], out ambiguous);
                if (ambiguous)
                {
                    Native.WriteLine("计划名「" + args[1] + "」对应多个计划，请改用 GUID：");
                    foreach (PlanInfo p in plans)
                    {
                        if (SameName(p, args[1])) Native.WriteLine("  " + p.Guid + "  " + p.Label);
                    }
                    return ExitAmbiguous;
                }
            }
            if (target == null)
            {
                Native.WriteLine("找不到电源计划：「" + args[1] + "」，可用 --list 查看。");
                return ExitUnknownPlan;
            }
            if (!Plans.SetActive(target.Guid))
            {
                Native.WriteLine("系统拒绝了切换请求：" + target.Label);
                return ExitFailed;
            }
            Native.WriteLine("已切换到：" + target.Label);
            return ExitOk;
        }

        private static bool SameName(PlanInfo p, string text)
        {
            return string.Equals(p.Name, text, StringComparison.CurrentCultureIgnoreCase)
                || string.Equals(p.Label, text, StringComparison.CurrentCultureIgnoreCase)
                || string.Equals(p.EnglishName, text, StringComparison.OrdinalIgnoreCase);
        }

        private static PlanInfo Match(List<PlanInfo> plans, string text, out bool ambiguous)
        {
            PlanInfo hit = null;
            int count = 0;
            foreach (PlanInfo p in plans)
            {
                if (!SameName(p, text)) continue;
                count++;
                if (hit == null) hit = p;
            }
            ambiguous = count > 1;
            return hit;
        }

        private static int ListPlans(string output)
        {
            List<PlanInfo> plans = Plans.Load();
            Guid active = Plans.GetActive();
            PlanInfo current = Plans.Find(plans, active);

            string[] table = Table(plans, active, current);
            Native.WriteLines(table);

            if (!string.IsNullOrEmpty(output))
            {
                try
                {
                    string full = Path.GetFullPath(output);
                    string dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    // 控制台写不进去时（被重定向、或从资源管理器启动），落盘是唯一可读的渠道
                    bool asText = string.Equals(Path.GetExtension(full), ".txt", StringComparison.OrdinalIgnoreCase);
                    File.WriteAllText(full, asText ? string.Join("\r\n", table) + "\r\n" : ToJson(plans, active),
                        new UTF8Encoding(false));
                    Native.WriteLine("");
                    Native.WriteLine("已写入：" + full);
                }
                catch (Exception ex)
                {
                    Native.WriteLine("写入文件失败：" + ex.Message);
                    return ExitFailed;
                }
            }
            return plans.Count > 0 ? ExitOk : ExitFailed;
        }

        private static string[] Table(List<PlanInfo> plans, Guid active, PlanInfo current)
        {
            List<string> lines = new List<string>();
            lines.Add("PowerPlanTray " + Version);
            lines.Add(plans.Count == 0
                ? "未能读取电源计划（注册表访问被拒绝？）"
                : "共 " + plans.Count + " 个计划，当前：" + (current != null ? current.Label : active.ToString()));
            lines.Add("");
            lines.Add("  GUID                                  名称                  英文名");
            foreach (PlanInfo p in plans)
            {
                lines.Add(string.Format("  {0}  {1}{2}  {3}",
                    p.Guid, p.Guid == active ? "* " : "  ", p.Label, p.EnglishName));
            }
            return lines.ToArray();
        }

        private static string ToJson(List<PlanInfo> plans, Guid active)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"version\": \"").Append(Escape(Version)).Append("\",\n");
            sb.Append("  \"active\": \"").Append(active.ToString()).Append("\",\n");
            sb.Append("  \"plans\": [\n");
            for (int i = 0; i < plans.Count; i++)
            {
                PlanInfo p = plans[i];
                sb.Append("    {\"guid\": \"").Append(p.Guid.ToString()).Append("\"");
                sb.Append(", \"name\": \"").Append(Escape(p.Name)).Append("\"");
                sb.Append(", \"label\": \"").Append(Escape(p.Label)).Append("\"");
                sb.Append(", \"englishName\": \"").Append(Escape(p.EnglishName)).Append("\"");
                sb.Append(", \"rank\": ").Append(p.Rank);
                sb.Append(", \"level\": ").Append(p.Level);
                sb.Append(", \"active\": ").Append(p.Guid == active ? "true" : "false");
                sb.Append("}").Append(i + 1 < plans.Count ? "," : "").Append("\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Escape(string text)
        {
            if (text == null) return "";
            StringBuilder sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
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
