using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace WinWindow;

public partial class App : Application
{
    private const int ElevationCancelled = 1223;
    private const string ElevationHandoff = "--elevation-parent";
    private Mutex? _instance;
    private bool _ownsInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            // Chromium launches the registered host with the extension origin. Dispatch it
            // before single-instance and elevation so it stays a headless, read-only relay.
            if (e.Args.FirstOrDefault() is { } origin && origin.StartsWith("chrome-extension://", StringComparison.Ordinal))
            {
                Shutdown(await Task.Run(() => NeatWin.App.BrowserBridge.RunNativeHostAsync(origin,
                    NeatWin.App.BrowserBridge.DefaultDirectory, Console.OpenStandardInput(), Console.OpenStandardOutput())));
                return;
            }
            if (e.Args.FirstOrDefault() == "--fixture") { OpenFixture(e.Args); return; }
            if (e.Args.FirstOrDefault() == "--self-test")
            {
                int result = await SmokeTests.RunAsync(e.Args.Length > 1 ? e.Args[1] : "TestResults");
                Shutdown(result); return;
            }
            bool dev = e.Args.Contains("--dev");
            string title = dev ? "WinWindow Management · Dev" : "WinWindow Management";
            bool isAdministrator = IsAdministrator();
            string? handoffDirectory = null;
            if (isAdministrator && e.Args.Length >= 3 && e.Args[^3] == ElevationHandoff &&
                int.TryParse(e.Args[^2], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId) &&
                parentId > 0 && parentId != Environment.ProcessId)
            {
                await WaitForStartupParentAsync(parentId);
                handoffDirectory = e.Args[^1];
            }
            try { _instance = new Mutex(true, @"Local\WinWindow.Management." + (dev ? "dev" : "main"), out _ownsInstance); }
            catch (UnauthorizedAccessException) { _ownsInstance = false; }
            if (!_ownsInstance)
            {
                // An elevated owner's mutex can deny access. Its window still
                // accepts the dedicated Show message; never start a duplicate.
                nint existing = 0;
                for (int i = 0; i < 10 && existing == 0; i++)
                {
                    existing = Native.FindWindow(null, title);
                    if (existing == 0) await Task.Delay(80);
                }
                if (existing == 0) throw new InvalidOperationException(UiText.Get("RunningNotFound"));
                Native.GetWindowThreadProcessId(existing, out uint pid);
                Native.AllowSetForegroundWindow(pid);
                if (!Native.PostMessage(existing, DesktopIntegration.WakeMessage, 0, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), UiText.Get("WakeFailed"));
                Shutdown(); return;
            }
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dev ? "WinWindow-Management-dev" : "WinWindow-Management");
            int dataIndex = Array.IndexOf(e.Args, "--data-dir");
            if (dev && dataIndex >= 0 && dataIndex + 1 < e.Args.Length) directory = Path.GetFullPath(e.Args[dataIndex + 1]);
            directory = handoffDirectory ?? directory;
            string? elevationError = null;
            if (!isAdministrator && StateStore.RequestAdministratorOnLaunch(directory))
            {
                try
                {
                    await Task.Run(() => RelaunchAsAdministrator(e.Args, directory));
                    Shutdown(); return;
                }
                // 用户取消权限提示后仍可管理普通窗口。这里继续当前实例的启动流程，让本次选择生效。
                catch (Win32Exception ex) when (ex.NativeErrorCode == ElevationCancelled) { }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { elevationError = ex.Message; }
            }
            var store = new StateStore(directory);
            var window = new MainWindow(store) { Title = title };
            MainWindow = window;
            SessionEnding += (_, _) => { window.SaveGeometry(); window.Manager.RestoreForSessionEnd(); };
            // A tray launch creates the native message endpoint without showing
            // and later hiding it. Otherwise initialization can hide a window
            // that the user has already woken, or briefly expose a false wake.
            if (e.Args.Contains("--minimized")) new WindowInteropHelper(window).EnsureHandle();
            else window.Show();
            await window.InitializeAsync();
            if (elevationError is not null) window.ShowStatus(UiText.Format("AdministratorLaunchFailed", elevationError));
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "WinWindow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task WaitForStartupParentAsync(int parentId)
    {
        // The original instance keeps the lock while Windows waits for confirmation.
        Process parent;
        try { parent = Process.GetProcessById(parentId); }
        catch (ArgumentException) { return; } // The parent already exited.
        using (parent) await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void RelaunchAsAdministrator(string[] args, string dataDirectory)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException(UiText.Get("MissingExePath"));
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(ElevationHandoff);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(dataDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(UiText.Get("AdministratorProcessMissing"));
    }

    private static void PublishFixtureHandle(string path, nint hwnd)
    {
        // Readers treat the marker's existence as readiness. Publish it only after its writer closes.
        var pending = path + ".tmp";
        File.WriteAllText(pending, hwnd.ToInt64().ToString());
        File.Move(pending, path);
    }

    private void OpenFixture(string[] args)
    {
        bool reject = args.Contains("reject");
        if (!reject)
        {
            Forms.Form form = args.Contains("stall") ? new StalledTarget(args[3])
                : args.Contains("activity") ? new ActivityTarget() : new Forms.Form();
            if (form is ActivityTarget && uint.TryParse(args.ElementAtOrDefault(3), out var controllerPid) && controllerPid != 0)
            {
                // The fixture can become foreground when shown. Give its test controller a
                // foreground handoff before the controller verifies the transition to background.
                form.Activated += (_, _) => Native.AllowSetForegroundWindow(controllerPid);
            }
            form.Text = "窗口测试 · 文档工作区"; form.Width = 640; form.Height = 420;
            form.Controls.Add(new Forms.Label { Text = "WinWindow 原生窗口验证", Dock = Forms.DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });
            form.FormClosed += (_, _) => Shutdown();
            if (args.Contains("twins"))
            {
                form.Text = "WinWindow duplicate fixture";
                form.Icon = System.Drawing.SystemIcons.Information;
                new Forms.Form { Text = form.Text, Icon = System.Drawing.SystemIcons.Warning }.Show();
            }
            form.Show(); PublishFixtureHandle(args[1], form.Handle);
            return;
        }
        var window = new Window
        {
            Title = "窗口测试 · 拒绝最小化", Width = 640, Height = 420,
            Content = new TextBlock { Text = "WinWindow WPF 兼容性验证", Margin = new Thickness(32), FontSize = 24 }
        };
        MainWindow = window; window.Closed += (_, _) => Shutdown();
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized) window.Dispatcher.BeginInvoke(new Action(() => window.WindowState = WindowState.Normal), DispatcherPriority.Send);
        };
        window.Show(); PublishFixtureHandle(args[1], new WindowInteropHelper(window).Handle);
    }
    // Standalone fixture with an application-controlled pause and a measurable heartbeat.
    private sealed class ActivityTarget : Forms.Form
    {
        private readonly Forms.Timer _clock = new() { Interval = 50 };
        private bool _running;
        private int _ticks;

        public ActivityTarget()
        {
            _clock.Tick += (_, _) =>
            {
                if (_running) _ticks++;
                bool focused = Native.GetForegroundWindow() == Handle &&
                    Native.GetActiveWindow() == Handle &&
                    Native.GetFocus() == Handle;
                Text = $"activity:{(_running ? "running" : "paused")}:{(focused ? "focused" : "unfocused")}:{_ticks}";
            };
            _clock.Start();
        }

        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == Native.AppActivationMessage) _running = message.WParam != 0;
            else if (message.Msg == 0x0006 && ((int)message.WParam & 0xFFFF) == 0) _running = false;
            else if (message.Msg == 0x0008) _running = false;
            base.WndProc(ref message);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _clock.Dispose();
            base.Dispose(disposing);
        }
    }

    internal const uint StallFixtureMessage = 0x8015;

    // Block a real cross-thread native operation for the regression test.
    private sealed class StalledTarget : Forms.Form
    {
        private readonly EventWaitHandle _entered, _release;
        private bool _armed;
        public StalledTarget(string name)
        {
            _entered = EventWaitHandle.OpenExisting(name + ".entered");
            _release = EventWaitHandle.OpenExisting(name + ".release");
        }
        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == 0x8014) { _armed = true; m.Result = 0; return; }
            if (m.Msg == StallFixtureMessage)
            {
                _entered.Set(); _release.WaitOne(); m.Result = 0; return;
            }
            if (_armed && m.Msg == 0x0046 && (Native.InSendMessageEx(0) & 9) == 1)
            {
                _armed = false;
                _entered.Set(); _release.WaitOne();
            }
            base.WndProc(ref m);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _entered.Dispose(); _release.Dispose(); }
            base.Dispose(disposing);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.Cleanup();
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose(); base.OnExit(e);
    }
}
