// taskmon.cs -- Taskbar system monitor (C# source)
//
// Displays NET / CPU / GPU / MEM stats as sparkline graphs overlaid on the
// right side of the Windows taskbar, positioned just to the left of the
// system clock / notification area.
//
// BUILD:   run build.bat (uses csc.exe from .NET Framework 4 -- no SDK needed)
// RUN:     wscript.exe taskmon.vbs   (silent, no console window)
// DEV:     build-and-run.bat         (kill + build + launch in one step)
// KILL:    kill.bat                  (finds taskmon.ps1 process by command line)
//
// Settings persist to %LOCALAPPDATA%\taskmon\settings.json.
// Right-click the overlay to open the Settings dialog or Quit.
//
// Architecture:
//   taskmon.vbs           silent VBS launcher (no console flash)
//   taskmon.ps1           PowerShell bootstrap: loads pre-built DLL, calls App::Run()
//   taskmon.cs  (this)    full C# application compiled to taskmon.dll by build.bat
//
// Key classes:
//   Native        Win32 P/Invoke (SetWindowPos, FindWindow, GetWindowRect, NVML, ...)
//   Settings      JSON-backed config with thoroughly commented serialisation
//   Metrics       PerformanceCounter (CPU/MEM/NET) + NVML P/Invoke (GPU util/temp)
//   CircularBuffer  Fixed-size ring buffer for sparkline history (60 samples)
//   DBPanel       Double-buffered Panel -- zero-flicker GDI+ rendering surface
//   OverlayForm   Frameless HWND_TOPMOST WinForms window sitting on the taskbar
//   SettingsForm  Tabbed settings dialog: Display / Colours / Behaviour
//   DarkRenderer  Dark-themed ToolStripRenderer for the right-click context menu
//   App           Entry point called by taskmon.ps1
//
// Known limitation:
//   In exclusive-fullscreen mode (rare -- most modern games use borderless) the
//   overlay may briefly disappear and return within ~100 ms via the z-order timer.
//   Windowed / borderless fullscreen is unaffected.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskMon {

// ============================================================================
// Native P/Invoke -- Win32 window management + NVML (NVIDIA Management Library)
// ============================================================================
static class Native {
    public const int  WS_EX_NOACTIVATE = 0x08000000; // click won't steal focus
    public const int  WS_EX_TOOLWINDOW = 0x00000080; // hide from Alt+Tab
    public const uint SWP_NOMOVE       = 0x0002;
    public const uint SWP_NOSIZE       = 0x0001;
    public const uint SWP_NOACTIVATE   = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hInsert, int x, int y, int cx, int cy, uint flags);

    // NVML lives in C:\Windows\System32\nvml.dll on any system with NVIDIA drivers.
    // All functions return 0 on success.
    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2",                     ExactSpelling = true)]
    public static extern int NvmlInit();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown",                    ExactSpelling = true)]
    public static extern int NvmlShutdown();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2",   ExactSpelling = true)]
    public static extern int NvmlGetDevice(uint idx, out IntPtr dev);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature",        ExactSpelling = true)]
    public static extern int NvmlGetTemp(IntPtr dev, uint sensor, out uint temp);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates",   ExactSpelling = true)]
    public static extern int NvmlGetUtil(IntPtr dev, out NvmlUtil util);

    // GlobalMemoryStatusEx -- used once at startup to get total physical RAM.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MEMORYSTATUSEX {
        public uint  dwLength;            // must be set to 64 before calling
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;        // total installed RAM in bytes
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX stat);

    // Used by DoLayout() to locate the system tray so we sit just to its left.
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    public const uint SWP_NOZORDER    = 0x0004;
    public const int  WS_EX_LAYERED   = 0x00080000;
    public const int  ULW_ALPHA       = 2;

    // UpdateLayeredWindow -- renders a per-pixel-alpha bitmap onto the window.
    // Background pixels use alpha=1 (visually transparent, still receive mouse input).
    // Click-through only happens at alpha=0; alpha>=1 receives mouse events.
    [StructLayout(LayoutKind.Sequential)]
    public struct PTWIN  { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SZWIN  { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER {
        public int   biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int   biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter;
        public int   biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int    ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref PTWIN pptDst, ref SZWIN psize, IntPtr hdcSrc, ref PTWIN pptSrc,
        int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool   DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool   DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
}

[StructLayout(LayoutKind.Sequential)]
public struct NvmlUtil {
    public uint gpu;    // GPU engine utilisation %
    public uint memory; // GPU memory bandwidth utilisation %
}

// =============================================================================
// Settings -- JSON-backed, thoroughly commented for manual editing
// =============================================================================
public class Settings {
    // -- Which panels appear in the overlay -----------------------------------
    public bool   ShowNetUp      = true;   // upload speed sparkline
    public bool   ShowNetDown    = true;   // download speed sparkline
    public bool   ShowCpu        = true;   // CPU utilisation panel
    // "Aggregate" = one sparkline for overall CPU%.
    // "PerCore"   = XMeters-style grid: one vertical bar per logical core.
    public string CpuMode        = "Aggregate";
    public bool   ShowGpuUtil    = true;   // NVIDIA GPU utilisation % (requires nvml.dll)
    public bool   ShowGpuTemp    = true;   // NVIDIA GPU temperature degC (requires nvml.dll)
    public bool   ShowMemory     = true;   // system memory usage %

    // -- Network adapter ------------------------------------------------------
    // "auto" picks the adapter with current traffic.
    // Paste an exact adapter name (from Device Manager) to pin to one adapter.
    public string NetworkAdapter = "auto";

    // -- Behaviour ------------------------------------------------------------
    // Milliseconds between metric samples + redraws. 500 / 1000 / 2000.
    public int    UpdateIntervalMs = 1000;
    // 1.0 = fully opaque; 0.5 = 50% transparent (lets you see icons beneath).
    public double Opacity          = 1.0;
    // Launch taskmon automatically when Windows starts.
    public bool   RunOnStartup     = true;

    // -- Sparkline colours (HTML hex) -----------------------------------------
    public string ColorNetUp    = "#FF4040"; // upload   -- red
    public string ColorNetDown  = "#00FF88"; // download -- green
    public string ColorCpu      = "#FFB300";
    public string ColorGpu      = "#FF6B35"; // GPU utilisation
    public string ColorGpuTemp  = "#FFDD44"; // GPU temperature (distinct from util)
    public string ColorMemory   = "#CC44FF";

    // -- Persistence ----------------------------------------------------------
    static string FilePath { get {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "taskmon", "settings.json");
    }}

    public static Settings Load() {
        try { if (File.Exists(FilePath)) return Parse(File.ReadAllText(FilePath)); }
        catch { }
        return new Settings();
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, Serialize());
        } catch { }
    }

    public Settings Clone() { return (Settings)MemberwiseClone(); }

    // Writes a richly commented JSON file so it's easy to hand-edit.
    string Serialize() {
        var b = new StringBuilder();
        b.AppendLine("{");
        b.AppendLine("  \"_info\": \"taskmon settings -- edit here or use right-click > Settings in the overlay\",");
        b.AppendLine();
        b.AppendLine("  \"_display\": \"which panels appear in the taskbar overlay\",");
        Jb(b, "ShowNetUp",      ShowNetUp);
        Jb(b, "ShowNetDown",    ShowNetDown);
        Jb(b, "ShowCpu",        ShowCpu);
        Js(b, "CpuMode",        CpuMode,
            "Aggregate = one sparkline for overall CPU%  |  PerCore = XMeters-style grid");
        Jb(b, "ShowGpuUtil",    ShowGpuUtil);
        Jb(b, "ShowGpuTemp",    ShowGpuTemp);
        Jb(b, "ShowMemory",     ShowMemory);
        b.AppendLine();
        b.AppendLine("  \"_network\": \"auto picks the busiest adapter; paste exact Device Manager name to pin\",");
        Js(b, "NetworkAdapter", NetworkAdapter);
        b.AppendLine();
        b.AppendLine("  \"_behaviour\": \"\",");
        Ji(b, "UpdateIntervalMs", UpdateIntervalMs,
            "milliseconds between samples: 500 (snappy) | 1000 (recommended) | 2000 (quiet)");
        Jd(b, "Opacity",          Opacity,
            "1.00 = fully opaque  |  0.50 = half transparent  (min 0.30)");
        Jb(b, "RunOnStartup",     RunOnStartup,
            "true = launch automatically when Windows starts");
        b.AppendLine();
        b.AppendLine("  \"_colours\": \"HTML hex colour for each sparkline -- e.g. #FF0000 for red\",");
        Js(b, "ColorNetUp",     ColorNetUp);
        Js(b, "ColorNetDown",   ColorNetDown);
        Js(b, "ColorCpu",       ColorCpu);
        Js(b, "ColorGpu",       ColorGpu);
        Js(b, "ColorGpuTemp",   ColorGpuTemp);
        Js(b, "ColorMemory",    ColorMemory, null, last: true);
        b.AppendLine("}");
        return b.ToString();
    }

    void Jb(StringBuilder b, string k, bool v,   string c=null, bool last=false) {
        b.Append(string.Format("  \"{0}\": {1}", k, v ? "true" : "false"));
        if (!last) b.Append(",");
        if (c != null) b.Append("  // " + c);
        b.AppendLine();
    }
    void Js(StringBuilder b, string k, string v, string c=null, bool last=false) {
        b.Append(string.Format("  \"{0}\": \"{1}\"",
            k, v.Replace("\\","\\\\").Replace("\"","\\\"")));
        if (!last) b.Append(",");
        if (c != null) b.Append("  // " + c);
        b.AppendLine();
    }
    void Ji(StringBuilder b, string k, int v,    string c=null, bool last=false) {
        b.Append(string.Format("  \"{0}\": {1}", k, v));
        if (!last) b.Append(",");
        if (c != null) b.Append("  // " + c);
        b.AppendLine();
    }
    void Jd(StringBuilder b, string k, double v, string c=null, bool last=false) {
        b.Append(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "  \"{0}\": {1:0.00}", k, v));
        if (!last) b.Append(",");
        if (c != null) b.Append("  // " + c);
        b.AppendLine();
    }

    // Minimal JSON parser -- handles the flat key/value structure we write above.
    // Ignores "_comment" keys and lines starting with "//".
    static Settings Parse(string json) {
        var s = new Settings();
        foreach (Match m in Regex.Matches(json,
            "\"(\\w+)\"\\s*:\\s*(?:\"([^\"]*)\"|\\b(true|false)\\b|([\\d.]+))")) {
            string k=m.Groups[1].Value, sv=m.Groups[2].Value,
                   bv=m.Groups[3].Value, nv=m.Groups[4].Value;
            switch (k) {
                case "ShowNetUp":        s.ShowNetUp        = bv=="true"; break;
                case "ShowNetDown":      s.ShowNetDown      = bv=="true"; break;
                case "ShowCpu":          s.ShowCpu          = bv=="true"; break;
                case "CpuMode":          s.CpuMode          = sv;         break;
                case "ShowGpuUtil":      s.ShowGpuUtil      = bv=="true"; break;
                case "ShowGpuTemp":      s.ShowGpuTemp      = bv=="true"; break;
                case "ShowMemory":       s.ShowMemory       = bv=="true"; break;
                case "NetworkAdapter":   s.NetworkAdapter   = sv;         break;
                case "UpdateIntervalMs": int.TryParse(nv, out s.UpdateIntervalMs); break;
                case "Opacity":
                    double.TryParse(nv,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out s.Opacity); break;
                case "RunOnStartup": s.RunOnStartup = bv == "true"; break;
                case "ColorNetUp":    s.ColorNetUp    = sv; break;
                case "ColorNetDown":  s.ColorNetDown  = sv; break;
                case "ColorCpu":      s.ColorCpu      = sv; break;
                case "ColorGpu":      s.ColorGpu      = sv; break;
                case "ColorGpuTemp":  s.ColorGpuTemp  = sv; break;
                case "ColorMemory":   s.ColorMemory   = sv; break;
            }
        }
        return s;
    }
}

// =============================================================================
// CircularBuffer -- fixed-size ring buffer for sparkline history
// =============================================================================
class CircularBuffer {
    readonly float[] _d;
    int _head;
    public readonly int Capacity;
    public CircularBuffer(int n) { _d = new float[n]; Capacity = n; }
    // Append a new sample, overwriting the oldest.
    public void Push(float v) { _d[_head] = v; _head = (_head + 1) % Capacity; }
    // Fill dst[] with samples in oldest-first order (left = oldest, right = newest).
    public void CopyTo(float[] dst) {
        for (int i = 0; i < Capacity; i++) dst[i] = _d[(_head + i) % Capacity];
    }
}

// =============================================================================
// Metrics -- all PerformanceCounter + NVML handles; call Sample() each tick
// =============================================================================
class Metrics : IDisposable {
    // -- Current values (written by timer tick, read by paint -- same UI thread)
    public float   CpuTotal;
    public float[] CpuCores;   // one entry per logical core
    public float   MemPct;
    public float   NetUpBps;
    public float   NetDnBps;
    public float   GpuUtil;
    public uint    GpuTempC;
    public bool    NvmlOk;
    // Auto-scaling ceiling for network sparklines (decays slowly when traffic drops)
    public float   NetPeak = 1024 * 1024f;

    // -- Sparkline history buffers ---------------------------------------------
    public readonly CircularBuffer  HCpu, HMem, HNetUp, HNetDn, HGpu;
    public readonly CircularBuffer[] HCores; // one per logical core
    public readonly int CoreCount;

    PerformanceCounter    _pcCpuTotal;
    PerformanceCounter[]  _pcCores;
    PerformanceCounter    _pcMem;
    PerformanceCounter    _pcNetUp, _pcNetDn;
    ulong  _memTotalMb;
    IntPtr _nvDev;
    bool   _disposed;

    public Metrics(string netAdapter) {
        CoreCount = Environment.ProcessorCount;
        CpuCores  = new float[CoreCount];
        HCpu   = new CircularBuffer(60);
        HMem   = new CircularBuffer(60);
        HNetUp = new CircularBuffer(60);
        HNetDn = new CircularBuffer(60);
        HGpu   = new CircularBuffer(60);
        HCores = new CircularBuffer[CoreCount];
        for (int i = 0; i < CoreCount; i++) HCores[i] = new CircularBuffer(60);

        // -- CPU --------------------------------------------------------------
        // _Total gives the aggregate across all cores; individual instances give
        // per-core values used in the XMeters-style grid.
        _pcCpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
        _pcCores    = new PerformanceCounter[CoreCount];
        for (int i = 0; i < CoreCount; i++)
            _pcCores[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);

        // -- Memory -----------------------------------------------------------
        // Available MBytes is polled each tick; total is read once via Win32.
        _pcMem = new PerformanceCounter("Memory", "Available MBytes", true);
        var ms = new Native.MEMORYSTATUSEX { dwLength = 64 };
        Native.GlobalMemoryStatusEx(ref ms);
        _memTotalMb = ms.ullTotalPhys / (1024 * 1024);

        // -- Network ----------------------------------------------------------
        InitNet(netAdapter);

        // -- GPU via NVML -----------------------------------------------------
        // NVML gives us GPU util% and temp directly -- no subprocess needed.
        // Falls back gracefully if nvml.dll is missing or GPU index 0 isn't NVIDIA.
        try {
            if (Native.NvmlInit() == 0 && Native.NvmlGetDevice(0, out _nvDev) == 0)
                NvmlOk = true;
        } catch { NvmlOk = false; }

        // Rate-based PerformanceCounters always return 0 on the very first call.
        // Call NextValue() once now so the first real Sample() shows correct values.
        _pcCpuTotal.NextValue();
        if (_pcCores != null) foreach (var p in _pcCores) p.NextValue();
        _pcMem.NextValue();
        if (_pcNetUp != null) { _pcNetUp.NextValue(); _pcNetDn.NextValue(); }
    }

    void InitNet(string adapter) {
        try {
            var cat  = new PerformanceCounterCategory("Network Interface");
            var all  = cat.GetInstanceNames();
            // Filter out virtual/tunnel adapters for auto-selection.
            var pool = (adapter == "auto")
                ? all.Where(n =>
                    !n.Contains("Loopback") && !n.Contains("ISATAP") &&
                    !n.Contains("Pseudo")   && !n.Contains("Teredo") &&
                    !n.Contains("6to4")).ToArray()
                : all.Where(n =>
                    n.IndexOf(adapter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (pool.Length == 0) pool = all;
            string pick = pool[0];
            _pcNetUp = new PerformanceCounter("Network Interface", "Bytes Sent/sec",     pick, true);
            _pcNetDn = new PerformanceCounter("Network Interface", "Bytes Received/sec", pick, true);
            _pcNetUp.NextValue(); _pcNetDn.NextValue();
        } catch { /* silently skip network if counters unavailable */ }
    }

    // Called once per timer tick on the UI thread.  All counter reads take microseconds.
    public void Sample() {
        // CPU
        CpuTotal = Clamp100(_pcCpuTotal.NextValue());
        HCpu.Push(CpuTotal);
        for (int i = 0; i < CoreCount; i++) {
            CpuCores[i] = Clamp100(_pcCores[i].NextValue());
            HCores[i].Push(CpuCores[i]);
        }

        // Memory: percent used = (total - available) / total * 100
        float avail = _pcMem.NextValue();
        MemPct = _memTotalMb > 0
            ? Clamp100((float)((_memTotalMb - avail) / _memTotalMb * 100.0))
            : 0f;
        HMem.Push(MemPct);

        // Network: auto-scale peak decays slowly so the sparkline re-centres
        if (_pcNetUp != null) {
            NetUpBps = Math.Max(0f, _pcNetUp.NextValue());
            NetDnBps = Math.Max(0f, _pcNetDn.NextValue());
            float peak = Math.Max(NetUpBps, NetDnBps);
            if (peak > NetPeak) NetPeak = peak;
            // Very slow decay: ~0.05% per sample -- keeps graph readable
            NetPeak = Math.Max(1024 * 1024f, NetPeak * 0.9995f);
        }
        HNetUp.Push(NetUpBps);
        HNetDn.Push(NetDnBps);

        // GPU -- microsecond NVML calls, safe on UI thread
        if (NvmlOk) {
            try {
                NvmlUtil u;
                if (Native.NvmlGetUtil(_nvDev, out u) == 0) GpuUtil = u.gpu;
                uint t;
                if (Native.NvmlGetTemp(_nvDev, 0, out t) == 0) GpuTempC = t;
            } catch { /* NVML calls failing at runtime -- skip silently */ }
        }
        HGpu.Push(GpuUtil);
    }

    static float Clamp100(float v) { return Math.Max(0f, Math.Min(100f, v)); }

    public void Dispose() {
        if (_disposed) return; _disposed = true;
        if (_pcCpuTotal != null) _pcCpuTotal.Dispose();
        if (_pcCores != null) foreach (var p in _pcCores) if (p != null) p.Dispose();
        if (_pcMem   != null) _pcMem.Dispose();
        if (_pcNetUp != null) _pcNetUp.Dispose();
        if (_pcNetDn != null) _pcNetDn.Dispose();
        if (NvmlOk) { try { Native.NvmlShutdown(); } catch { } }
    }
}

// =============================================================================
// DBPanel -- double-buffered Panel eliminates all GDI+ flicker
// =============================================================================
class DBPanel : Panel {
    public DBPanel() {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint            |
            ControlStyles.OptimizedDoubleBuffer, true);
        ResizeRedraw = true;
    }
}

// =============================================================================
// OverlayForm -- frameless, always-on-top window overlaid on the taskbar
// =============================================================================
public class OverlayForm : Form {
    public  Settings S;
    Metrics  _m;
    internal string _scriptDir; // set by App.Run() -- needed to update startup reg entry
    internal System.Windows.Forms.Timer _timer;
    System.Windows.Forms.Timer _zTimer;
    ContextMenuStrip _menu;
    NotifyIcon _notify;
    int _assertingZ;

    // Pre-allocated GDI resources -- created once in AllocGdi(), never in paint loop.
    Font       _fLbl, _fVal;
    SolidBrush _dimBrush, _coreBgBrush;
    Pen        _divPen;
    Color      _cUp, _cDn, _cCpu, _cGpu, _cGpuTemp, _cMem;
    float[]    _buf = new float[60]; // scratch buffer for CopyTo inside UpdateLayer

    // -- Layout constants ------------------------------------------------------
    const int SW     = 70;  // standard section width (px)
    const int CBAR_W = 9;   // per-core bar width (px)
    const int CBAR_G = 2;   // per-core bar gap (px)

    public OverlayForm(Settings s, string scriptDir = null) {
        S          = s;
        _scriptDir = scriptDir;
        _m         = new Metrics(s.NetworkAdapter);
        AllocGdi();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition   = FormStartPosition.Manual;
        TopMost         = true;
        ShowInTaskbar   = false;
        BackColor       = Color.Black; // never painted; UpdateLayeredWindow owns the visual
        // Suppress default WinForms painting -- our visual comes entirely from UpdateLayer().
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        MouseDown += (o, e) => {
            if (e.Button == MouseButtons.Right) {
                // SetForegroundWindow lets the menu's message filter detect outside
                // clicks, so it auto-closes when the user clicks elsewhere.
                Native.SetForegroundWindow(Handle);
                _menu.Show(Cursor.Position);
            }
        };

        BuildMenu();
        BuildNotifyIcon();
        DoLayout();

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(250, s.UpdateIntervalMs) };
        _timer.Tick += (o, e) => Tick();
        _timer.Start();

        _zTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _zTimer.Tick += (o, e) => AssertTopmost();
        _zTimer.Start();
    }

    // WS_EX_NOACTIVATE: clicks do not steal keyboard focus.
    // WS_EX_TOOLWINDOW: keeps the overlay out of the Alt+Tab list.
    protected override CreateParams CreateParams { get {
        var p = base.CreateParams;
        p.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
        return p;
    }}

    void Tick() {
        _m.Sample();
        UpdateLayer();
    }

    void AssertTopmost() {
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void WndProc(ref Message m) {
        base.WndProc(ref m);
        const int WM_WINDOWPOSCHANGED = 0x0047;
        if (m.Msg == WM_WINDOWPOSCHANGED && _assertingZ == 0) {
            ++_assertingZ;
            AssertTopmost();
            --_assertingZ;
        }
    }

    // Position the overlay just to the left of the system tray / clock area.
    internal void DoLayout() {
        var scr = Screen.PrimaryScreen;
        int tbH = Math.Max(32, scr.Bounds.Height - scr.WorkingArea.Height);
        int w   = CalcW();
        int x   = TrayLeftEdge(scr) - w;
        Size     = new Size(w, tbH);
        Location = new Point(x, scr.Bounds.Bottom - tbH);
        // UpdateLayeredWindow encodes position and size, so re-render after any layout change.
        if (IsHandleCreated) UpdateLayer();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        DoLayout();
    }

    // Returns the screen-coordinate left edge of the system tray notification area,
    // accounting for the "show hidden icons" overflow Button which sits to the left
    // of TrayNotifyWnd on some Windows builds and would otherwise be covered.
    static int TrayLeftEdge(Screen scr) {
        try {
            IntPtr trayWnd = Native.FindWindow("Shell_TrayWnd", null);
            if (trayWnd == IntPtr.Zero) return scr.Bounds.Right;

            IntPtr notifyWnd = Native.FindWindowEx(
                trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notifyWnd == IntPtr.Zero) return scr.Bounds.Right;

            Native.RECT nr;
            if (!Native.GetWindowRect(notifyWnd, out nr)) return scr.Bounds.Right;

            // The overflow "show hidden icons" Button lives inside TrayNotifyWnd.
            // Its left edge is the true leftmost point we must not cover.
            IntPtr overflowBtn = Native.FindWindowEx(
                notifyWnd, IntPtr.Zero, "Button", null);
            if (overflowBtn != IntPtr.Zero) {
                Native.RECT br;
                if (Native.GetWindowRect(overflowBtn, out br))
                    return Math.Min(nr.Left, br.Left);
            }

            return nr.Left;
        } catch { }
        return scr.Bounds.Right;
    }

    int CalcW() {
        int w = 0;
        if (S.ShowNetUp)   w += SW;
        if (S.ShowNetDown) w += SW;
        if (S.ShowCpu) {
            if (S.CpuMode == "PerCore") {
                // 24 cores -> 3 rows x 8 cols; width = cols * (barW + gap) - last gap + padding
                int cols = (_m.CoreCount + 2) / 3;
                w += 4 + cols * (CBAR_W + CBAR_G) - CBAR_G + 4;
            } else {
                w += SW;
            }
        }
        if (S.ShowGpuUtil || S.ShowGpuTemp) w += SW;
        if (S.ShowMemory)  w += SW;
        return Math.Max(60, w);
    }

    void AllocGdi() {
        _fLbl        = new Font("Segoe UI", 6.5f, FontStyle.Regular, GraphicsUnit.Point);
        _fVal        = new Font("Segoe UI", 7.5f, FontStyle.Bold,    GraphicsUnit.Point);
        _dimBrush    = new SolidBrush(Color.FromArgb(0x80, 0x80, 0x80));
        _coreBgBrush = new SolidBrush(Color.FromArgb(0x30, 0x30, 0x30));
        _divPen      = new Pen(Color.FromArgb(0x3C, 0x3C, 0x3C), 1f);
        RefreshColors();
    }

    internal void RefreshColors() {
        _cUp  = ParseHex(S.ColorNetUp,   "#00FF88");
        _cDn  = ParseHex(S.ColorNetDown, "#00AAFF");
        _cCpu = ParseHex(S.ColorCpu,     "#FFB300");
        _cGpu     = ParseHex(S.ColorGpu,     "#FF6B35");
        _cGpuTemp = ParseHex(S.ColorGpuTemp, "#FFDD44");
        _cMem     = ParseHex(S.ColorMemory,  "#CC44FF");
    }

    protected override void Dispose(bool d) {
        if (d) {
            if (_timer  != null) { _timer.Stop();  _timer.Dispose(); }
            if (_zTimer != null) { _zTimer.Stop(); _zTimer.Dispose(); }
            if (_notify != null) { _notify.Visible = false; _notify.Dispose(); }
            if (_m           != null) _m.Dispose();
            if (_fLbl        != null) _fLbl.Dispose();
            if (_fVal        != null) _fVal.Dispose();
            if (_dimBrush    != null) _dimBrush.Dispose();
            if (_coreBgBrush != null) _coreBgBrush.Dispose();
            if (_divPen      != null) _divPen.Dispose();
        }
        base.Dispose(d);
    }

    // Creates the 16×16 sparkline bar icon used for both the tray and the settings title bar.
    internal static Icon MakeAppIcon() {
        using (var bmp = new Bitmap(16, 16))
        using (var g = Graphics.FromImage(bmp)) {
            g.Clear(Color.Transparent);
            var bars = new[] {
                new { C = Color.FromArgb(0xFF, 0x40, 0x40), X = 1,  H = 5  },
                new { C = Color.FromArgb(0xFF, 0xB3, 0x00), X = 5,  H = 9  },
                new { C = Color.FromArgb(0xFF, 0x6B, 0x35), X = 9,  H = 6  },
                new { C = Color.FromArgb(0xCC, 0x44, 0xFF), X = 13, H = 11 },
            };
            foreach (var b in bars)
                g.FillRectangle(new SolidBrush(b.C), b.X, 15 - b.H, 2, b.H);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    void BuildNotifyIcon() {
        _notify = new NotifyIcon {
            Icon             = MakeAppIcon(),
            Text             = "taskmon",
            ContextMenuStrip = _menu,
            Visible          = true
        };
        _notify.DoubleClick += (o, e) => OpenSettings();
    }

    void BuildMenu() {
        _menu = new ContextMenuStrip {
            BackColor = Color.FromArgb(0x2D, 0x2D, 0x2D),
            ForeColor = Color.White,
            Renderer  = new DarkRenderer()
        };
        var miSet  = new ToolStripMenuItem("Settings...");
        miSet.Click += (o, e) => OpenSettings();
        var miQuit = new ToolStripMenuItem("Quit");
        miQuit.Click += (o, e) => Application.Exit();
        _menu.Items.AddRange(new ToolStripItem[] {
            miSet, new ToolStripSeparator(), miQuit
        });
    }

    // Opens the settings dialog with the z-order timer paused so the overlay
    // doesn't cover the ComboBox dropdown or the dialog itself.
    void OpenSettings() {
        _zTimer.Stop();
        try {
            using (var dlg = new SettingsForm(S, this)) dlg.ShowDialog();
        } finally {
            _zTimer.Start();
        }
    }

    // -- Layered window rendering ---------------------------------------------
    // Renders the overlay using UpdateLayeredWindow with per-pixel alpha.
    // Background pixels use alpha=1: visually transparent (0.4% opacity) but
    // alpha > 0 so they still receive mouse events -- no click-through.
    internal void UpdateLayer() {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        int w = Width, h = Height;

        IntPtr screenDC = IntPtr.Zero, memDC = IntPtr.Zero;
        IntPtr hDib = IntPtr.Zero, oldObj = IntPtr.Zero;
        try {
            screenDC = Native.GetDC(IntPtr.Zero);
            memDC    = Native.CreateCompatibleDC(screenDC);

            // Create a 32bpp top-down DIB for GDI+ to draw into.
            IntPtr bits;
            var bmi = new Native.BITMAPINFO {
                bmiHeader = new Native.BITMAPINFOHEADER {
                    biSize     = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                    biWidth    = w,
                    biHeight   = -h, // negative = top-down scanlines
                    biPlanes   = 1,
                    biBitCount = 32
                }
            };
            hDib   = Native.CreateDIBSection(screenDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
            oldObj = Native.SelectObject(memDC, hDib);

            // GDI+ renders into the DIB.  Format32bppPArgb stores premultiplied alpha,
            // which is what UpdateLayeredWindow (AC_SRC_ALPHA) requires.
            using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits))
            using (var g = Graphics.FromImage(bmp)) {
                // Alpha=1 background: visually ~transparent but receives mouse input.
                g.Clear(Color.FromArgb(1, 0, 0, 0));

                g.SmoothingMode     = SmoothingMode.None;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                DrawContent(g, w, h);
            }

            // Compose onto screen with per-pixel alpha (SourceConstantAlpha scaled by Opacity).
            byte globalAlpha = (byte)Math.Max(1, Math.Min(255,
                (int)(Clamp01(S.Opacity) * 255)));
            var blend = new Native.BLENDFUNCTION {
                BlendOp             = 0,   // AC_SRC_OVER
                BlendFlags          = 0,
                SourceConstantAlpha = globalAlpha,
                AlphaFormat         = 1    // AC_SRC_ALPHA -- use per-pixel alpha
            };
            var dst = new Native.PTWIN { x = Left, y = Top };
            var src = new Native.PTWIN { x = 0,    y = 0   };
            var sz  = new Native.SZWIN { cx = w,   cy = h  };
            Native.UpdateLayeredWindow(Handle, screenDC,
                ref dst, ref sz, memDC, ref src, 0, ref blend, Native.ULW_ALPHA);
        } finally {
            if (oldObj   != IntPtr.Zero) Native.SelectObject(memDC, oldObj);
            if (hDib     != IntPtr.Zero) Native.DeleteObject(hDib);
            if (memDC    != IntPtr.Zero) Native.DeleteDC(memDC);
            if (screenDC != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    void DrawContent(Graphics g, int w, int h) {
        // No background fill -- background pixels stay at alpha=1 (set in UpdateLayer),
        // making them visually transparent while still receiving mouse events.
        int x = 0;

        if (S.ShowNetUp)
            DrawSection(g, ref x, h, "UPLOAD \u2191",
                SpeedStr(_m.NetUpBps), _cUp, _m.HNetUp, _m.NetPeak);
        if (S.ShowNetDown)
            DrawSection(g, ref x, h, "DOWNLOAD \u2193",
                SpeedStr(_m.NetDnBps), _cDn, _m.HNetDn, _m.NetPeak);
        if (S.ShowCpu) {
            if (S.CpuMode == "PerCore")
                DrawCoreGrid(g, ref x, h);
            else
                DrawSection(g, ref x, h, "CPU",
                    string.Format("{0:F0}%", _m.CpuTotal), _cCpu, _m.HCpu, 100f);
        }
        if (S.ShowGpuUtil || S.ShowGpuTemp)
            DrawGpuSection(g, ref x, h);
        if (S.ShowMemory)
            DrawSection(g, ref x, h, "MEM",
                string.Format("{0:F0}%", _m.MemPct), _cMem, _m.HMem, 100f);
    }

    // GPU section: sparkline driven by utilisation; value line shows util% in one
    // colour and temp°C in a distinct colour so they're easy to tell apart at a glance.
    void DrawGpuSection(Graphics g, ref int x, int h) {
        if (x > 0) g.DrawLine(_divPen, x, 2, x, h - 2);
        DrawSparkline(g, _m.HGpu, new Rectangle(x, 0, SW, h), _cGpu, 100f);
        g.DrawString("GPU", _fLbl, _dimBrush,
            new RectangleF(x + 3, 1, SW - 6, h * 0.45f));

        // Value line: util and/or temp in separate colours.
        if (S.ShowGpuUtil && S.ShowGpuTemp) {
            // Draw util% left-aligned in GPU colour, temp°C right-aligned in temp colour.
            string util = string.Format("{0:F0}%", _m.GpuUtil);
            string temp = string.Format("{0}\u00B0C", _m.GpuTempC);
            var rf = new RectangleF(x + 2, h * 0.42f, SW - 4, h * 0.58f);
            var sfL = new StringFormat { Alignment = StringAlignment.Near,
                                         LineAlignment = StringAlignment.Center };
            var sfR = new StringFormat { Alignment = StringAlignment.Far,
                                         LineAlignment = StringAlignment.Center };
            using (var bu = new SolidBrush(_cGpu))
            using (var bt = new SolidBrush(_cGpuTemp)) {
                g.DrawString(util, _fVal, bu, rf, sfL);
                g.DrawString(temp, _fVal, bt, rf, sfR);
            }
        } else if (S.ShowGpuUtil) {
            using (var b = new SolidBrush(_cGpu))
                g.DrawString(string.Format("{0:F0}%", _m.GpuUtil), _fVal, b,
                    new RectangleF(x + 2, h * 0.42f, SW - 4, h * 0.58f),
                    new StringFormat { Alignment = StringAlignment.Center,
                                       LineAlignment = StringAlignment.Center });
        } else {
            using (var b = new SolidBrush(_cGpuTemp))
                g.DrawString(string.Format("{0}\u00B0C", _m.GpuTempC), _fVal, b,
                    new RectangleF(x + 2, h * 0.42f, SW - 4, h * 0.58f),
                    new StringFormat { Alignment = StringAlignment.Center,
                                       LineAlignment = StringAlignment.Center });
        }
        x += SW;
    }

    // Draw one metric section: sparkline fills the background, text is overlaid.
    void DrawSection(Graphics g, ref int x, int h,
                     string lbl, string val, Color col,
                     CircularBuffer hist, float scale) {
        if (x > 0) g.DrawLine(_divPen, x, 2, x, h - 2);
        // Sparkline fills the full section rectangle as a subtle coloured background.
        DrawSparkline(g, hist, new Rectangle(x, 0, SW, h), col, scale);
        // Label: small dim text at the top of the section.
        g.DrawString(lbl, _fLbl, _dimBrush,
            new RectangleF(x + 3, 1, SW - 6, h * 0.45f));
        // Value: bold coloured text centred in the lower half.
        using (var b = new SolidBrush(col))
            g.DrawString(val, _fVal, b,
                new RectangleF(x + 2, h * 0.42f, SW - 4, h * 0.58f),
                new StringFormat {
                    Alignment     = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming      = StringTrimming.Character
                });
        x += SW;
    }

    // Filled-area sparkline.  Semi-transparent fill + full-opacity edge line.
    void DrawSparkline(Graphics g, CircularBuffer hist,
                       Rectangle r, Color col, float scale) {
        hist.CopyTo(_buf);
        int n = _buf.Length; if (n < 2 || r.Width < 2) return;
        float sc = scale > 0f ? scale : 100f;

        var pts = new PointF[n + 2];
        pts[0]     = new PointF(r.Left,  r.Bottom);
        pts[n + 1] = new PointF(r.Right, r.Bottom);
        for (int i = 0; i < n; i++) {
            float pct = Math.Min(_buf[i] / sc, 1f);
            pts[i + 1] = new PointF(
                r.Left + (float)i / (n - 1) * r.Width,
                r.Bottom - pct * r.Height);
        }
        // 45/255 ~= 18% alpha fill -- subtle hint of colour under the text
        using (var fill = new SolidBrush(Color.FromArgb(45, col)))
            g.FillPolygon(fill, pts);
        // Full-brightness edge line for crisp readability
        var line = new PointF[n];
        for (int i = 0; i < n; i++) line[i] = pts[i + 1];
        using (var pen = new Pen(col, 1.2f)) g.DrawLines(pen, line);
    }

    // XMeters-style per-core CPU grid.
    // 24 cores -> 3 rows x 8 cols of vertical bars with a green->yellow->red heat map.
    void DrawCoreGrid(Graphics g, ref int x, int h) {
        const int ROWS = 3;
        int cols  = (_m.CoreCount + ROWS - 1) / ROWS; // ceil(24/3) = 8
        int gridW = cols * (CBAR_W + CBAR_G) - CBAR_G;
        int secW  = 4 + gridW + 4;

        if (x > 0) g.DrawLine(_divPen, x, 2, x, h - 2);
        int gx = x + 4;

        // "CPU" label above the grid
        g.DrawString("CPU", _fLbl, _dimBrush,
            new RectangleF(gx, 0, gridW, 9),
            new StringFormat { Alignment = StringAlignment.Center });

        int rowsH = h - 10;                          // vertical space for bars
        int rowH  = (rowsH - (ROWS - 1) * 2) / ROWS; // bar height with 2px row gaps

        for (int row = 0; row < ROWS; row++) {
            int yt = 10 + row * (rowH + 2);
            for (int col = 0; col < cols; col++) {
                int idx = row * cols + col;
                if (idx >= _m.CoreCount) break;

                float pct = Math.Min(_m.CpuCores[idx] / 100f, 1f);
                int bx = gx + col * (CBAR_W + CBAR_G);
                int bh = Math.Max(1, (int)(pct * rowH));
                int by = yt + (rowH - bh); // bars grow upward from bottom of row

                g.FillRectangle(_coreBgBrush, bx, yt, CBAR_W, rowH);
                using (var br = new SolidBrush(HeatColor(pct)))
                    g.FillRectangle(br, bx, by, CBAR_W, bh);
            }
        }
        x += secW;
    }

    // Smooth heat-map colour: green (0%) -> yellow (50%) -> red (100%)
    static Color HeatColor(float p) {
        if (p <= 0.5f) {
            int r = (int)(p * 2f * 255f);
            return Color.FromArgb(r, 200, 0);
        } else {
            int g2 = (int)((1f - (p - 0.5f) * 2f) * 200f);
            return Color.FromArgb(255, g2, 0);
        }
    }

    static string SpeedStr(float bps) {
        if (bps >= 1073741824f) return string.Format("{0:F1}GB/s", bps / 1073741824f);
        if (bps >=    1048576f) return string.Format("{0:F1}MB/s", bps / 1048576f);
        if (bps >=       1024f) return string.Format("{0:F0}KB/s", bps / 1024f);
        return string.Format("{0:F0}B/s", bps);
    }
    static double Clamp01(double v) { return Math.Max(0.1, Math.Min(1.0, v)); }
    static Color  ParseHex(string h, string fb) {
        try { return ColorTranslator.FromHtml(h); }
        catch { return ColorTranslator.FromHtml(fb); }
    }
}

// =============================================================================
// SettingsForm -- tabbed dialog: Display | Colours | Behaviour
// =============================================================================
public class SettingsForm : Form {
    Settings    _d;    // working draft -- only pushed to live settings on Apply
    OverlayForm _host;

    // Display tab controls
    CheckBox    _cbUp, _cbDn, _cbCpu, _cbGU, _cbGT, _cbMem;
    RadioButton _rbAgg, _rbPC;
    ComboBox    _cboNet;
    // Colours tab controls (swatch buttons)
    Button _bUp, _bDn, _bCpu, _bGpu, _bGpuTemp, _bMem;
    // Behaviour tab controls
    RadioButton _rb500, _rb1k, _rb2k;
    TrackBar    _tbOp;
    Label       _lblOp;
    CheckBox    _cbStartup;

    // Light-mode palette -- avoids fighting WinForms dark-theme rendering in
    // title bars, tab strips, and ComboBox dropdowns.
    static readonly Color BG      = Color.FromArgb(0xF3, 0xF3, 0xF3); // page bg
    static readonly Color BG2     = Color.White;                        // footer / alternate
    static readonly Color ACCENT  = Color.FromArgb(0x00, 0x5B, 0xB5); // section headers
    static readonly Color SEP     = Color.FromArgb(0xCC, 0xCC, 0xCC);
    static readonly Color FG      = Color.FromArgb(0x1A, 0x1A, 0x1A);
    static readonly Color FG_DIM  = Color.FromArgb(0x66, 0x66, 0x66);

    public SettingsForm(Settings s, OverlayForm host) {
        _d    = s.Clone();
        _host = host;

        Text            = "taskmon \u2014 Settings";
        Size            = new Size(460, 460);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = MinimizeBox = false;
        StartPosition   = FormStartPosition.CenterScreen;
        Font            = new Font("Segoe UI", 9f);
        Icon            = OverlayForm.MakeAppIcon();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(TabDisplay());
        tabs.TabPages.Add(TabColours());
        tabs.TabPages.Add(TabBehaviour());

        var foot = new Panel {
            Dock = DockStyle.Bottom, Height = 50,
            BackColor = Color.FromArgb(0xE8, 0xE8, 0xE8)
        };
        foot.Paint += (o, e) =>
            e.Graphics.DrawLine(new Pen(SEP), 0, 0, foot.Width, 0);
        // Primary action: Windows accent blue
        var bOk = new Button {
            Text = "Apply \u0026 Close", Location = new Point(222, 10), Size = new Size(116, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0x00, 0x78, 0xD4), ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        bOk.FlatAppearance.BorderColor = Color.FromArgb(0x00, 0x5A, 0xA8);
        var bCan = new Button {
            Text = "Cancel", Location = new Point(348, 10), Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0xE0, 0xE0, 0xE0), ForeColor = FG
        };
        bCan.FlatAppearance.BorderColor = SEP;
        bOk.Click  += (o, e) => { Apply(); Close(); };
        bCan.Click += (o, e) => Close();
        foot.Controls.Add(bOk); foot.Controls.Add(bCan);

        Controls.Add(tabs);
        Controls.Add(foot);
    }

    // -- Tab helpers -----------------------------------------------------------
    TabPage TP(string t) {
        return new TabPage(t) { BackColor = BG };
    }

    // Section header: blue-tinted bold label + thin separator line.
    void SecHead(Control p, string icon, string text, ref int y) {
        p.Controls.Add(new Label {
            Text = icon + "  " + text,
            Location = new Point(8, y), Size = new Size(420, 18),
            ForeColor = ACCENT,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        });
        p.Controls.Add(new Panel {
            Location = new Point(8, y + 19), Size = new Size(420, 1), BackColor = SEP
        });
        y += 26;
    }

    // Checkbox row with a 10×10 colored indicator dot on the left.
    void MetricRow(TabPage p, ref CheckBox cb, string text, Color dot, bool chk, ref int y) {
        p.Controls.Add(new Panel { Location = new Point(10, y + 5), Size = new Size(10, 10), BackColor = dot });
        cb = new CheckBox {
            Text = text, Location = new Point(26, y), Size = new Size(400, 20),
            Checked = chk
        };
        p.Controls.Add(cb);
        y += 22;
    }

    RadioButton RB(string t, int x, int y, bool v) {
        return new RadioButton {
            Text = t, Location = new Point(x, y), Size = new Size(400, 19), Checked = v
        };
    }

    // -- Display tab ----------------------------------------------------------
    TabPage TabDisplay() {
        var p = TP("Display"); int y = 10;

        SecHead(p, "\u25A0", "Metrics to show", ref y);
        MetricRow(p, ref _cbUp,  "Upload speed  (NET \u2191)",                    SafeColor(_d.ColorNetUp),   _d.ShowNetUp,   ref y);
        MetricRow(p, ref _cbDn,  "Download speed  (NET \u2193)",                  SafeColor(_d.ColorNetDown), _d.ShowNetDown, ref y);
        MetricRow(p, ref _cbCpu, "CPU utilisation",                               SafeColor(_d.ColorCpu),     _d.ShowCpu,     ref y);
        MetricRow(p, ref _cbGU,  "GPU utilisation %  (NVIDIA RTX 5070 Ti)",      SafeColor(_d.ColorGpu),     _d.ShowGpuUtil, ref y);
        MetricRow(p, ref _cbGT,  "GPU temperature \u00B0C  (NVIDIA RTX 5070 Ti)", SafeColor(_d.ColorGpuTemp), _d.ShowGpuTemp, ref y);
        MetricRow(p, ref _cbMem, "Memory usage %",                               SafeColor(_d.ColorMemory),  _d.ShowMemory,  ref y);
        y += 6;

        SecHead(p, "\u25A3", "CPU display mode", ref y);
        _rbAgg = RB("Aggregate \u2014 single sparkline for overall CPU%",             18, y, _d.CpuMode != "PerCore"); y += 21;
        _rbPC  = RB("Per-core bars \u2014 XMeters-style grid  (24 bars, 3\u00D78)",  18, y, _d.CpuMode == "PerCore"); y += 10;
        p.Controls.Add(_rbAgg); p.Controls.Add(_rbPC);
        y += 16;

        SecHead(p, "\u25BA", "Network adapter", ref y);
        _cboNet = new ComboBox {
            Location = new Point(18, y), Size = new Size(410, 22),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        try {
            _cboNet.Items.Add("auto");
            foreach (var n in new PerformanceCounterCategory("Network Interface")
                .GetInstanceNames()
                .Where(n2 => !n2.Contains("Loopback") && !n2.Contains("ISATAP"))
                .OrderBy(n2 => n2))
                _cboNet.Items.Add(n);
        } catch { _cboNet.Items.Add("auto"); }
        _cboNet.SelectedItem = _d.NetworkAdapter;
        if (_cboNet.SelectedItem == null) _cboNet.SelectedIndex = 0;
        p.Controls.Add(_cboNet);
        return p;
    }

    // -- Colours tab ----------------------------------------------------------
    TabPage TabColours() {
        var p = TP("Colours"); int y = 10;
        int dummy = y; SecHead(p, "\u25CF", "Sparkline colours  \u2014  click swatch to change", ref dummy); y = dummy;
        ColRow(p, "Upload  (NET \u2191)",    _d.ColorNetUp,   ref _bUp,      ref y);
        ColRow(p, "Download  (NET \u2193)",  _d.ColorNetDown, ref _bDn,      ref y);
        ColRow(p, "CPU",                     _d.ColorCpu,     ref _bCpu,     ref y);
        ColRow(p, "GPU utilisation",         _d.ColorGpu,     ref _bGpu,     ref y);
        ColRow(p, "GPU temperature",         _d.ColorGpuTemp, ref _bGpuTemp, ref y);
        ColRow(p, "Memory",                  _d.ColorMemory,  ref _bMem,     ref y);
        p.Controls.Add(new Label {
            Text      = "Changes take effect on Apply \u0026 Close.",
            Location  = new Point(14, y + 4), Size = new Size(390, 18),
            ForeColor = FG_DIM, Font = new Font("Segoe UI", 8f)
        });
        return p;
    }

    void ColRow(TabPage p, string lbl, string hex, ref Button btn, ref int y) {
        var dot = new Panel { Location = new Point(12, y + 9), Size = new Size(10, 10), BackColor = SafeColor(hex) };
        p.Controls.Add(dot);
        p.Controls.Add(new Label { Text = lbl, Location = new Point(28, y + 6), Size = new Size(130, 20) });
        var b = new Button {
            Location = new Point(164, y + 2), Size = new Size(56, 26),
            FlatStyle = FlatStyle.Flat, Text = "", BackColor = SafeColor(hex)
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(0xAA, 0xAA, 0xAA);
        b.Click += (o, e) => {
            using (var dlg = new ColorDialog { Color = b.BackColor, FullOpen = true })
                if (dlg.ShowDialog() == DialogResult.OK) b.BackColor = dlg.Color;
        };
        var hexLbl = new Label {
            Location  = new Point(228, y + 8), Size = new Size(90, 16),
            ForeColor = FG_DIM, Font = new Font("Consolas", 8.5f)
        };
        b.BackColorChanged += (o, e) => {
            dot.BackColor = b.BackColor;
            hexLbl.Text   = ColorTranslator.ToHtml(b.BackColor).ToUpper();
        };
        hexLbl.Text = hex.ToUpper();
        p.Controls.Add(b); p.Controls.Add(hexLbl);
        btn = b; y += 32;
    }

    // -- Behaviour tab --------------------------------------------------------
    TabPage TabBehaviour() {
        var p = TP("Behaviour"); int y = 10;
        SecHead(p, "\u23F1", "Update interval", ref y);
        _rb500 = RB("500 ms \u2014 snappier, slightly more CPU",  18, y, _d.UpdateIntervalMs == 500);  y += 22;
        _rb1k  = RB("1 second \u2014 recommended",                18, y, _d.UpdateIntervalMs == 1000); y += 22;
        _rb2k  = RB("2 seconds \u2014 very quiet",                18, y, _d.UpdateIntervalMs == 2000); y += 10;
        if (!_rb500.Checked && !_rb1k.Checked && !_rb2k.Checked) _rb1k.Checked = true;
        p.Controls.Add(_rb500); p.Controls.Add(_rb1k); p.Controls.Add(_rb2k);
        y += 16;

        SecHead(p, "\u25D1", "Window opacity", ref y);
        _tbOp = new TrackBar {
            Location = new Point(18, y), Size = new Size(400, 30),
            Minimum = 30, Maximum = 100, TickFrequency = 10,
            Value = (int)(_d.Opacity * 100)
        };
        y += 32;
        _lblOp = new Label {
            Location = new Point(18, y), Size = new Size(400, 18), ForeColor = Color.Silver
        };
        UpdateOpLbl();
        _tbOp.ValueChanged += (o, e) => UpdateOpLbl();
        p.Controls.Add(_tbOp); p.Controls.Add(_lblOp);
        y += 28;

        y += 6;
        SecHead(p, "\u25B6", "Startup", ref y);
        _cbStartup = new CheckBox {
            Text     = "Launch taskmon automatically when Windows starts",
            Location = new Point(18, y), Size = new Size(400, 20),
            Checked  = _d.RunOnStartup
        };
        p.Controls.Add(_cbStartup);
        y += 26;

        p.Controls.Add(new Label {
            Text      = "Note: changing the network adapter takes effect on next restart.",
            Location  = new Point(18, y), Size = new Size(400, 18),
            ForeColor = FG_DIM, Font = new Font("Segoe UI", 8f)
        });
        return p;
    }

    void UpdateOpLbl() {
        string note = _tbOp.Value == 100 ? " (fully opaque)"
                    : _tbOp.Value <= 50  ? " (very transparent)" : "";
        _lblOp.Text = string.Format("Opacity: {0}%{1}", _tbOp.Value, note);
    }


    // -- Apply -----------------------------------------------------------------
    // Reads all controls into _draft, copies to live settings, saves JSON,
    // then updates the overlay immediately.
    void Apply() {
        _d.ShowNetUp        = _cbUp.Checked;
        _d.ShowNetDown      = _cbDn.Checked;
        _d.ShowCpu          = _cbCpu.Checked;
        _d.CpuMode          = _rbPC.Checked ? "PerCore" : "Aggregate";
        _d.ShowGpuUtil      = _cbGU.Checked;
        _d.ShowGpuTemp      = _cbGT.Checked;
        _d.ShowMemory       = _cbMem.Checked;
        _d.NetworkAdapter   = (_cboNet.SelectedItem ?? "auto").ToString();
        _d.ColorNetUp       = ColorTranslator.ToHtml(_bUp.BackColor);
        _d.ColorNetDown     = ColorTranslator.ToHtml(_bDn.BackColor);
        _d.ColorCpu         = ColorTranslator.ToHtml(_bCpu.BackColor);
        _d.ColorGpu         = ColorTranslator.ToHtml(_bGpu.BackColor);
        _d.ColorGpuTemp     = ColorTranslator.ToHtml(_bGpuTemp.BackColor);
        _d.ColorMemory      = ColorTranslator.ToHtml(_bMem.BackColor);
        _d.UpdateIntervalMs = _rb500.Checked ? 500 : _rb2k.Checked ? 2000 : 1000;
        _d.Opacity          = _tbOp.Value / 100.0;
        _d.RunOnStartup     = _cbStartup.Checked;

        CopyTo(_d, _host.S);
        _d.Save();
        // Apply startup registry entry immediately
        if (_host._scriptDir != null)
            App.ApplyStartup(_d.RunOnStartup, _host._scriptDir);

        // Live-update the overlay without a restart
        _host.RefreshColors();
        _host._timer.Interval = Math.Max(250, _d.UpdateIntervalMs);
        _host.DoLayout(); // repositions and calls UpdateLayer()
    }

    static void CopyTo(Settings s, Settings d) {
        d.ShowNetUp = s.ShowNetUp; d.ShowNetDown = s.ShowNetDown;
        d.ShowCpu = s.ShowCpu; d.CpuMode = s.CpuMode;
        d.ShowGpuUtil = s.ShowGpuUtil; d.ShowGpuTemp = s.ShowGpuTemp;
        d.ShowMemory = s.ShowMemory; d.NetworkAdapter = s.NetworkAdapter;
        d.UpdateIntervalMs = s.UpdateIntervalMs; d.Opacity = s.Opacity;
        d.RunOnStartup = s.RunOnStartup;
        d.ColorNetUp = s.ColorNetUp; d.ColorNetDown = s.ColorNetDown;
        d.ColorCpu = s.ColorCpu; d.ColorGpu = s.ColorGpu; d.ColorGpuTemp = s.ColorGpuTemp;
        d.ColorMemory = s.ColorMemory;
    }

    static Color SafeColor(string h) {
        try { return ColorTranslator.FromHtml(h); } catch { return Color.DimGray; }
    }
}

// =============================================================================
// DarkRenderer -- makes the right-click context menu match the dark overlay theme
// =============================================================================
class DarkRenderer : ToolStripProfessionalRenderer {
    class DarkCT : ProfessionalColorTable {
        public override Color MenuItemSelected            { get { return Color.FromArgb(0x45, 0x45, 0x55); } }
        public override Color MenuItemBorder              { get { return Color.FromArgb(0x55, 0x55, 0x66); } }
        public override Color MenuBorder                  { get { return Color.FromArgb(0x44, 0x44, 0x44); } }
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(0x2D, 0x2D, 0x2D); } }
        public override Color ImageMarginGradientBegin    { get { return Color.FromArgb(0x2D, 0x2D, 0x2D); } }
        public override Color ImageMarginGradientMiddle   { get { return Color.FromArgb(0x2D, 0x2D, 0x2D); } }
        public override Color ImageMarginGradientEnd      { get { return Color.FromArgb(0x2D, 0x2D, 0x2D); } }
        public override Color SeparatorDark               { get { return Color.FromArgb(0x44, 0x44, 0x44); } }
        public override Color SeparatorLight              { get { return Color.FromArgb(0x44, 0x44, 0x44); } }
    }
    public DarkRenderer() : base(new DarkCT()) {}
}

// =============================================================================
// App -- entry point called by taskmon.ps1
// =============================================================================
public static class App {
    const string REG_KEY  = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string REG_NAME = "taskmon";

    // scriptDir is passed from taskmon.ps1 ($PSScriptRoot) so we can find taskmon.vbs.
    public static void Run(string scriptDir = null) {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var s = Settings.Load();
        // Apply startup registration on every launch so it stays in sync with the setting.
        if (scriptDir != null)
            ApplyStartup(s.RunOnStartup, scriptDir);
        using (var f = new OverlayForm(s, scriptDir)) {
            f.Show();
            Application.Run(f);
        }
    }

    // Adds or removes the HKCU Run entry.
    // cmd = wscript.exe "<path to taskmon.vbs>"
    internal static void ApplyStartup(bool enable, string scriptDir) {
        try {
            using (var key = Registry.CurrentUser.OpenSubKey(REG_KEY, writable: true)) {
                if (key == null) return;
                if (enable) {
                    var vbs = Path.Combine(scriptDir, "taskmon.vbs");
                    key.SetValue(REG_NAME,
                        string.Format("wscript.exe \"{0}\"", vbs));
                } else {
                    key.DeleteValue(REG_NAME, throwOnMissingValue: false);
                }
            }
        } catch { }
    }
}

} // namespace TaskMon
