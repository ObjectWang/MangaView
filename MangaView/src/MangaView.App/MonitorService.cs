using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace MangaView.App;

public sealed record MonitorDescriptor(
    string DeviceName,
    Rect Bounds,
    Rect WorkArea,
    bool IsPrimary,
    uint DpiX,
    uint DpiY,
    string? ColorProfilePath);

public sealed record HdrStatus(bool Supported, bool Enabled, uint BitsPerColorChannel);

/// <summary>多显示器、ICC 配置和 Windows 高级颜色 / HDR 状态检测。</summary>
public static class MonitorService
{
    public static IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var result = new List<MonitorDescriptor>();
        foreach (Screen screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            IntPtr monitorHandle = MonitorFromPoint(new Point32(bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2), 2);
            GetDpiForMonitor(monitorHandle, 0, out uint dpiX, out uint dpiY);
            var work = screen.WorkingArea;
            result.Add(new MonitorDescriptor(
                screen.DeviceName,
                new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                new Rect(work.X, work.Y, work.Width, work.Height),
                screen.Primary,
                dpiX == 0 ? 96 : dpiX,
                dpiY == 0 ? 96 : dpiY,
                TryGetColorProfile(screen.DeviceName)));
        }
        return result;
    }

    public static MonitorDescriptor? GetMonitorFromWindow(IntPtr handle)
    {
        Screen? screen = handle == IntPtr.Zero ? Screen.PrimaryScreen : Screen.FromHandle(handle);
        if (screen is null) return null;
        var bounds = screen.Bounds;
        IntPtr monitorHandle = MonitorFromPoint(new Point32(bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2), 2);
        GetDpiForMonitor(monitorHandle, 0, out uint dpiX, out uint dpiY);
        var work = screen.WorkingArea;
        return new MonitorDescriptor(screen.DeviceName,
            new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            new Rect(work.X, work.Y, work.Width, work.Height),
            screen.Primary,
            dpiX == 0 ? 96 : dpiX,
            dpiY == 0 ? 96 : dpiY,
            TryGetColorProfile(screen.DeviceName));
    }

    public static bool IsPlacementVisible(Rect placement)
    {
        foreach (var monitor in GetMonitors())
        {
            Rect intersection = Rect.Intersect(placement, monitor.Bounds);
            if (!intersection.IsEmpty && intersection.Width >= 120 && intersection.Height >= 100)
                return true;
        }
        return false;
    }

    public static bool MoveWindowToMonitor(IntPtr handle, MonitorDescriptor monitor)
    {
        if (handle == IntPtr.Zero) return false;
        var area = monitor.WorkArea;
        return SetWindowPos(handle, IntPtr.Zero, (int)area.Left, (int)area.Top,
            (int)area.Width, (int)area.Height, 0x0040 | 0x0004);
    }

    public static HdrStatus GetHdrStatus()
    {
        try
        {
            uint pathCount = 0, modeCount = 0;
            if (GetDisplayConfigBufferSizes(2, out pathCount, out modeCount) != 0) return new HdrStatus(false, false, 0);
            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            if (QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return new HdrStatus(false, false, 0);

            bool supported = false, enabled = false;
            uint bits = 0;
            for (int i = 0; i < pathCount; i++)
            {
                var request = new DisplayConfigGetAdvancedColorInfo
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = 10,
                        Size = (uint)Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>(),
                        AdapterId = paths[i].TargetInfo.AdapterId,
                        Id = paths[i].TargetInfo.Id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref request) != 0) continue;
                supported |= (request.Value & 1) != 0;
                enabled |= (request.Value & 2) != 0;
                bits = Math.Max(bits, request.BitsPerColorChannel);
            }
            return new HdrStatus(supported, enabled, bits);
        }
        catch
        {
            return new HdrStatus(false, false, 0);
        }
    }

    private static string? TryGetColorProfile(string deviceName)
    {
        IntPtr dc = IntPtr.Zero;
        try
        {
            dc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            uint size = 0;
            GetICMProfile(dc, ref size, null);
            if (size == 0) return null;
            var buffer = new char[size];
            if (!GetICMProfile(dc, ref size, buffer)) return null;
            return new string(buffer).TrimEnd('\0');
        }
        catch
        {
            return null;
        }
        finally
        {
            if (dc != IntPtr.Zero) DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
        public Point32(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanlineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct DisplayConfigPathInfo
    {
        [FieldOffset(0)] public DisplayConfigPathSourceInfo SourceInfo;
        [FieldOffset(24)] public DisplayConfigPathTargetInfo TargetInfo;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathInfoArray, ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo deviceInfo);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string? driver, string? device, string? port, IntPtr initData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetICMProfile(IntPtr hdc, ref uint bufferSize, char[]? buffer);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);
}
