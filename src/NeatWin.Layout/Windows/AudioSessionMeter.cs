using System.Runtime.InteropServices;
using static NeatWin.Windows.NativeMethods;

namespace NeatWin.Windows;

// Enumerate on a worker so a disconnected audio endpoint cannot hold up a layout operation.
internal sealed class AudioSessionMeter
{
    internal sealed record Reading(long Tick, Dictionary<uint, float> Peaks, Dictionary<uint, float> SharedPeaks);
    private Task<Reading?>? _pending;

    internal Reading? Poll(long now)
    {
        if (_pending is { IsCompleted: false }) return null;
        var latest = _pending?.GetAwaiter().GetResult();
        _pending = Task.Run(Measure);
        return latest != null && now - latest.Tick is >= 0 and <= 8000 ? latest : null;
    }

    private static Reading? Measure()
    {
        object? deviceEnumerator = null, manager = null;
        IMMDevice? endpoint = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            deviceEnumerator = type == null ? null : Activator.CreateInstance(type);
            var iid = typeof(IAudioSessionManager2).GUID;
            if (deviceEnumerator is not IMMDeviceEnumerator devices ||
                devices.GetDefaultAudioEndpoint(0, 1, out endpoint) != 0 || endpoint == null ||
                endpoint.Activate(in iid, 23, 0, out manager) != 0 || manager is not IAudioSessionManager2 audio ||
                audio.GetSessionEnumerator(out sessions) != 0 || sessions == null || sessions.GetCount(out var count) != 0)
                return null;
            var peaks = new Dictionary<uint, float>();
            var sharedPeaks = new Dictionary<uint, float>();
            for (int i = 0; i < Math.Min(count, 64); i++)
            {
                if (sessions.GetSession(i, out var session) != 0 || session == null) continue;
                try
                {
                    if (session is IAudioSessionControl2 control && session is IAudioMeterInformation meter &&
                        control.IsSystemSoundsSession() == 1)
                    {
                        int pidStatus = control.GetProcessId(out uint pid);
                        // S_OK (0) or AUDCLNT_S_NO_SINGLE_PROCESS (0x0889000D) both populate the creating PID.
                        if ((pidStatus == 0 || pidStatus == AudclntSNoSingleProcess) && pid != 0 &&
                            meter.GetPeakValue(out float peak) == 0 && peak is >= 0 and <= 1)
                        {
                            var readings = pidStatus == 0 ? peaks : sharedPeaks;
                            readings[pid] = Math.Max(readings.GetValueOrDefault(pid), peak);
                        }
                    }
                }
                finally { Release(session); }
            }
            return new(Environment.TickCount64, peaks, sharedPeaks);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or
            System.Reflection.TargetInvocationException or TypeLoadException) { return null; }
        finally { Release(sessions); Release(manager); Release(endpoint); Release(deviceEnumerator); }
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
