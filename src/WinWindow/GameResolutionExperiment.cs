using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace WinWindow;

internal sealed record DisplayModeOption(string DeviceName, uint Width, uint Height, uint BitsPerPixel, uint Frequency)
{
    public override string ToString() => Frequency > 1
        ? $"{Width} × {Height} @ {Frequency} Hz"
        : $"{Width} × {Height}";
}

// Experimental display-mode lease. It changes only the monitor output mode;
// Unity's own render-size request is tested separately through startup arguments.
internal sealed class GameResolutionExperiment
{
    private const int EnumCurrentSettings = -1;
    private const uint DmBitsPerPel = 0x00040000;
    private const uint DmPelsWidth = 0x00080000;
    private const uint DmPelsHeight = 0x00100000;
    private const uint DmDisplayFrequency = 0x00400000;
    private const uint CdsTest = 0x00000002;
    private const uint CdsFullscreen = 0x00000004;
    private const int DispChangeSuccessful = 0;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _journalPath;
    private Lease? _lease;
    private DateTime _appliedAtUtc;
    private DateTime _notFullscreenSinceUtc;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    private sealed class Lease
    {
        public string DeviceName { get; set; } = string.Empty;
        public long Handle { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStarted { get; set; }
        public string ProcessPath { get; set; } = string.Empty;
        public uint OriginalWidth { get; set; }
        public uint OriginalHeight { get; set; }
        public uint OriginalBitsPerPixel { get; set; }
        public uint OriginalFrequency { get; set; }
        public uint TargetWidth { get; set; }
        public uint TargetHeight { get; set; }
        public uint TargetBitsPerPixel { get; set; }
        public uint TargetFrequency { get; set; }

        public Lease() { }

        [JsonIgnore]
        public WindowIdentity Identity => new(Handle, ProcessId, ProcessStarted, ProcessPath, "UnityWndClass");
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNumber, ref DevMode mode, uint flags);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode mode, nint hwnd, uint flags, nint parameter);

    public GameResolutionExperiment(string dataDirectory)
    {
        _journalPath = Path.Combine(dataDirectory, "DisplayModeLease.json");
    }

    public DisplayModeOption GetCurrentMode(nint hwnd)
    {
        string device = DeviceForWindow(hwnd);
        DevMode mode = ReadCurrent(device);
        return ToOption(device, mode);
    }

    public IReadOnlyList<DisplayModeOption> GetDisplayModes(nint hwnd)
    {
        string device = DeviceForWindow(hwnd);
        DevMode current = ReadCurrent(device);
        var modes = new Dictionary<(uint Width, uint Height, uint Bpp, uint Frequency), DisplayModeOption>();
        for (int index = 0; index < 512; index++)
        {
            DevMode mode = NewDevMode();
            if (!EnumDisplaySettingsEx(device, index, ref mode, 0)) break;
            if (mode.PelsWidth < 640 || mode.PelsHeight < 480 || mode.BitsPerPel < 24) continue;
            var option = ToOption(device, mode);
            modes[(option.Width, option.Height, option.BitsPerPixel, option.Frequency)] = option;
        }

        var currentOption = ToOption(device, current);
        modes[(currentOption.Width, currentOption.Height, currentOption.BitsPerPixel, currentOption.Frequency)] = currentOption;
        return modes.Values
            .OrderByDescending(x => (long)x.Width * x.Height)
            .ThenByDescending(x => x.Frequency)
            .ToArray();
    }

    public bool HasLease
    {
        get
        {
            lock (_gate) return _lease is not null || File.Exists(_journalPath);
        }
    }

    public string ApplyOutputMode(WindowIdentity identity, DisplayModeOption target)
    {
        lock (_gate)
        {
            if (!Native.Matches(identity)) throw new InvalidOperationException(UiText.Get("IdentityUnavailable"));
            string device = DeviceForWindow(identity.Hwnd);
            if (!string.Equals(device, target.DeviceName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(UiText.Get("GameResolutionWrongDisplay"));

            // Re-applying a different choice starts from the real desktop mode, not from
            // the temporary mode installed by the previous experiment.
            if (_lease is not null || File.Exists(_journalPath)) _ = RestoreNow();

            DevMode original = ReadCurrent(device);
            if (SameMode(original, target))
                return UiText.Get("GameResolutionAlreadyActive");

            var lease = new Lease
            {
                DeviceName = device,
                Handle = identity.Handle,
                ProcessId = identity.ProcessId,
                ProcessStarted = identity.ProcessStarted,
                ProcessPath = identity.ProcessPath,
                OriginalWidth = original.PelsWidth,
                OriginalHeight = original.PelsHeight,
                OriginalBitsPerPixel = original.BitsPerPel,
                OriginalFrequency = original.DisplayFrequency,
                TargetWidth = target.Width,
                TargetHeight = target.Height,
                TargetBitsPerPixel = target.BitsPerPixel,
                TargetFrequency = target.Frequency
            };

            WriteLease(lease); // Durable recovery intent must exist before the display is changed.
            _lease = lease;
            _appliedAtUtc = DateTime.UtcNow;
            _notFullscreenSinceUtc = default;

            try
            {
                DevMode requested = WithResolution(original, target.Width, target.Height, target.BitsPerPixel, target.Frequency);
                ChangeMode(device, requested);
                DevMode actual = ReadCurrent(device);
                if (!MatchesTarget(actual, lease))
                    throw new InvalidOperationException(UiText.Get("GameResolutionVerifyFailed"));

                return UiText.Format("GameResolutionApplied", target.ToString());
            }
            catch
            {
                // If the driver changed the mode before reporting failure, roll it back.
                // Keep the journal if rollback itself fails so the next launch can retry.
                try { _ = RestoreLease(lease); } catch { }
                throw;
            }
        }
    }

    public string? RestoreNow()
    {
        lock (_gate)
        {
            Lease? lease = _lease ?? ReadLease();
            if (lease is null) return null;
            return RestoreLease(lease);
        }
    }

    public string? RecoverStaleLease()
    {
        lock (_gate)
        {
            _lease = ReadLease();
            if (_lease is null) return null;
            _appliedAtUtc = DateTime.UtcNow;
            return RestoreLease(_lease);
        }
    }

    // Restore when the target process exits, or when its window leaves fullscreen.
    // Losing focus alone is not enough: a fullscreen game may simply be behind another app.
    public string? TryRestoreIfTargetLeavesFullscreen()
    {
        lock (_gate)
        {
            Lease? lease = _lease ?? ReadLease();
            if (lease is null) return null;
            _lease = lease;

            if (!IsSameProcessAlive(lease)) return RestoreLease(lease);
            if (!Native.Matches(lease.Identity)) return null;

            bool fullscreen = IsFullscreen(lease.Identity);
            if (fullscreen)
            {
                _notFullscreenSinceUtc = default;
                return null;
            }

            DateTime now = DateTime.UtcNow;
            if (now - _appliedAtUtc < TimeSpan.FromSeconds(3)) return null;
            if (_notFullscreenSinceUtc == default)
            {
                _notFullscreenSinceUtc = now;
                return null;
            }
            return now - _notFullscreenSinceUtc >= TimeSpan.FromSeconds(2)
                ? RestoreLease(lease)
                : null;
        }
    }

    public static string[] UnityBorderlessArguments(DisplayModeOption mode) =>
    [
        "-screen-fullscreen", "1",
        "-window-mode", "borderless",
        "-screen-width", mode.Width.ToString(CultureInfo.InvariantCulture),
        "-screen-height", mode.Height.ToString(CultureInfo.InvariantCulture)
    ];

    public static string FormatUnityBorderlessArguments(DisplayModeOption mode) =>
        string.Join(" ", UnityBorderlessArguments(mode));

    private string RestoreLease(Lease lease)
    {
        DevMode current = ReadCurrent(lease.DeviceName);
        if (!MatchesTarget(current, lease))
        {
            // Someone or something else has changed the display since our apply.
            // Never overwrite that newer choice with a stale rollback.
            DeleteJournal();
            _lease = null;
            _notFullscreenSinceUtc = default;
            return UiText.Get("GameResolutionExternalChange");
        }

        DevMode restore = WithResolution(
            current, lease.OriginalWidth, lease.OriginalHeight,
            lease.OriginalBitsPerPixel, lease.OriginalFrequency);
        ChangeMode(lease.DeviceName, restore);
        DevMode actual = ReadCurrent(lease.DeviceName);
        if (!MatchesOriginal(actual, lease))
            throw new InvalidOperationException(UiText.Get("GameResolutionRestoreVerifyFailed"));

        DeleteJournal();
        _lease = null;
        _notFullscreenSinceUtc = default;
        return UiText.Format("GameResolutionRestored", lease.OriginalWidth, lease.OriginalHeight);
    }

    private static bool IsSameProcessAlive(Lease lease)
    {
        try
        {
            using var process = Process.GetProcessById(lease.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == lease.ProcessStarted;
        }
        catch (ArgumentException) { return false; }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // An access or inspection failure is not proof that the game has exited.
            return true;
        }
    }

    private static bool IsFullscreen(WindowIdentity identity)
    {
        if (!Native.Matches(identity)) return true; // Unknown window state: wait for process exit.
        if (Native.IsIconic(identity.Hwnd) || !Native.IsWindowVisible(identity.Hwnd)) return false;
        if (!Native.GetWindowRect(identity.Hwnd, out Native.Rect rect)) return true;

        try
        {
            var bounds = Screen.FromHandle(identity.Hwnd).Bounds;
            const int tolerance = 2;
            // Unity may retain its pre-switch outer bounds briefly. A window that still
            // covers the active monitor is fullscreen for this lease, even if its far edge
            // temporarily extends beyond the new desktop bounds.
            return rect.Left <= bounds.Left + tolerance &&
                rect.Top <= bounds.Top + tolerance &&
                rect.Right >= bounds.Right - tolerance &&
                rect.Bottom >= bounds.Bottom - tolerance;
        }
        catch (InvalidOperationException) { return true; }
    }

    private static string DeviceForWindow(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd))
            throw new InvalidOperationException(UiText.Get("IdentityUnavailable"));
        return Screen.FromHandle(hwnd).DeviceName;
    }

    private static DevMode ReadCurrent(string device)
    {
        DevMode mode = NewDevMode();
        if (!EnumDisplaySettingsEx(device, EnumCurrentSettings, ref mode, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), UiText.Get("GameResolutionReadFailed"));
        return mode;
    }

    private static DevMode NewDevMode() => new()
    {
        DeviceName = string.Empty,
        FormName = string.Empty,
        Size = (ushort)Marshal.SizeOf<DevMode>()
    };

    private static DisplayModeOption ToOption(string device, DevMode mode) =>
        new(device, mode.PelsWidth, mode.PelsHeight, mode.BitsPerPel, mode.DisplayFrequency);

    private static bool SameMode(DevMode current, DisplayModeOption target) =>
        current.PelsWidth == target.Width && current.PelsHeight == target.Height &&
        current.BitsPerPel == target.BitsPerPixel &&
        (target.Frequency <= 1 || Math.Abs((long)current.DisplayFrequency - target.Frequency) <= 1);

    private static bool MatchesTarget(DevMode current, Lease lease) =>
        current.PelsWidth == lease.TargetWidth && current.PelsHeight == lease.TargetHeight &&
        current.BitsPerPel == lease.TargetBitsPerPixel &&
        (lease.TargetFrequency <= 1 || Math.Abs((long)current.DisplayFrequency - lease.TargetFrequency) <= 1);

    private static bool MatchesOriginal(DevMode current, Lease lease) =>
        current.PelsWidth == lease.OriginalWidth && current.PelsHeight == lease.OriginalHeight &&
        current.BitsPerPel == lease.OriginalBitsPerPixel &&
        (lease.OriginalFrequency <= 1 || Math.Abs((long)current.DisplayFrequency - lease.OriginalFrequency) <= 1);

    private static DevMode WithResolution(DevMode mode, uint width, uint height, uint bitsPerPixel, uint frequency)
    {
        mode.Fields |= DmPelsWidth | DmPelsHeight | DmBitsPerPel;
        mode.PelsWidth = width;
        mode.PelsHeight = height;
        mode.BitsPerPel = bitsPerPixel;
        if (frequency > 1)
        {
            mode.Fields |= DmDisplayFrequency;
            mode.DisplayFrequency = frequency;
        }
        else
        {
            mode.Fields &= ~DmDisplayFrequency;
        }
        return mode;
    }

    private static void ChangeMode(string device, DevMode mode)
    {
        int test = ChangeDisplaySettingsEx(device, ref mode, 0, CdsTest, 0);
        if (test != DispChangeSuccessful)
            throw new InvalidOperationException(UiText.Format("GameResolutionModeFailed", test));

        int result = ChangeDisplaySettingsEx(device, ref mode, 0, CdsFullscreen, 0);
        if (result != DispChangeSuccessful)
            throw new InvalidOperationException(UiText.Format("GameResolutionModeFailed", result));
    }

    private void WriteLease(Lease lease)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        string temporary = _journalPath + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lease, Json));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _journalPath, overwrite: true);
    }

    private Lease? ReadLease()
    {
        if (!File.Exists(_journalPath)) return null;
        return JsonSerializer.Deserialize<Lease>(File.ReadAllText(_journalPath), Json)
            ?? throw new InvalidDataException(UiText.Get("GameResolutionJournalInvalid"));
    }

    private void DeleteJournal()
    {
        if (File.Exists(_journalPath)) File.Delete(_journalPath);
    }
}
