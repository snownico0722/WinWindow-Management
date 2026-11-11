using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace WinWindow;

// Temporary isolated-test trace for the native close/hide regression.
public partial class MainWindow
{
    private string? _lifecycleTrace;
    private int _traceLines;
    private void TraceLifecycle(string text)
    {
        if (_lifecycleTrace is null || _traceLines++ >= 300) return;
        File.AppendAllText(_lifecycleTrace, $"{DateTime.UtcNow:O} {text}; visible={IsVisible}/{Native.IsWindowVisible(_hwnd)} active={IsActive} top={Native.IsTopmost(_hwnd)} fg={Native.GetForegroundWindow():X} own={_hwnd:X} tray={_store.Preferences.CloseToTray} desktop={_desktop is not null} quitting={_quitting}\n");
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_testMode || !Environment.GetCommandLineArgs().Contains("--data-dir")) return;
        _lifecycleTrace = Path.Combine(Path.GetDirectoryName(_store.DirectoryPath)!, $"lifecycle-{Environment.ProcessId}.log");
        _source!.AddHook((nint hwnd, int msg, nint wp, nint lp, ref bool handled) =>
        {
            if (msg is 0x10 or 0x18 or 0x6 || (uint)msg == DesktopIntegration.WakeMessage)
                TraceLifecycle($"message {msg:X} wp={wp:X} lp={lp:X} handled={handled}");
            return 0;
        });
        IsVisibleChanged += (_, _) => TraceLifecycle("visibility changed");
        Activated += (_, _) => TraceLifecycle("activated");
        Deactivated += (_, _) => TraceLifecycle("deactivated");
        Dispatcher.UnhandledException += (_, args) => TraceLifecycle("exception " + args.Exception);
        TraceLifecycle("source initialized");
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        TraceLifecycle("closing entered");
        base.OnClosing(e);
        TraceLifecycle("closing returned cancel=" + e.Cancel);
    }
}
