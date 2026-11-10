using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private Mutex? _instance;
    private bool _ownsInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.FirstOrDefault() == "--fixture") { OpenFixture(e.Args); return; }
            if (e.Args.FirstOrDefault() == "--self-test")
            {
                int result = await SmokeTests.RunAsync(e.Args.Length > 1 ? e.Args[1] : "TestResults");
                Shutdown(result); return;
            }
            bool dev = e.Args.Contains("--dev");
            string title = dev ? "WinWindow Management · Dev" : "WinWindow Management";
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
                if (existing == 0) throw new InvalidOperationException("程序已在运行，但尚未找到窗口。");
                Native.GetWindowThreadProcessId(existing, out uint pid);
                Native.AllowSetForegroundWindow(pid);
                if (!Native.PostMessage(existing, DesktopIntegration.WakeMessage, 0, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "未能唤醒正在运行的 WinWindow。");
                Shutdown(); return;
            }
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dev ? "WinWindow-Management-dev" : "WinWindow-Management");
            // Isolated state is a development/test option, not a production override.
            int dataIndex = Array.IndexOf(e.Args, "--data-dir");
            if (dev && dataIndex >= 0 && dataIndex + 1 < e.Args.Length) directory = Path.GetFullPath(e.Args[dataIndex + 1]);
            var store = new StateStore(directory);
            var window = new MainWindow(store) { Title = title };
            MainWindow = window;
            SessionEnding += (_, _) => { window.SaveGeometry(); window.Manager.RestoreForSessionEnd(); };
            window.Show();
            await window.InitializeAsync();
            if (e.Args.Contains("--minimized")) window.Hide();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "WinWindow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private void OpenFixture(string[] args)
    {
        bool reject = args.Contains("reject");
        if (!reject)
        {
            Forms.Form form = args.Contains("stall") ? new StalledTarget(args[3]) : new Forms.Form();
            form.Text = "窗口测试 · 文档工作区"; form.Width = 640; form.Height = 420;
            form.Controls.Add(new Forms.Label { Text = "WinWindow 原生窗口验证", Dock = Forms.DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });
            form.FormClosed += (_, _) => Shutdown();
            form.Show(); File.WriteAllText(args[1], form.Handle.ToInt64().ToString());
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
        window.Show(); File.WriteAllText(args[1], new WindowInteropHelper(window).Handle.ToInt64().ToString());
    }
    // The regression target blocks in a real cross-thread window operation,
    // not in a mocked manager. Named events keep the test deterministic.
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
