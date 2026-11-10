using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

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
            _instance = new Mutex(true, @"Local\WinWindow.Management." + (dev ? "dev" : "main"), out _ownsInstance);
            if (!_ownsInstance)
            {
                nint existing = Native.FindWindow(null, "WinWindow Management");
                if (existing != 0) { Native.GetWindowThreadProcessId(existing, out uint pid); Native.AllowSetForegroundWindow(pid); Native.PostMessage(existing, DesktopIntegration.WakeMessage, 0, 0); }
                Shutdown(); return;
            }
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dev ? "WinWindow-Management-dev" : "WinWindow-Management");
            var store = new StateStore(directory);
            var window = new MainWindow(store);
            MainWindow = window;
            SessionEnding += (_, _) => { window.SaveGeometry(); window.Manager.RestoreForSessionEnd(); };
            window.Show();
            if (e.Args.Contains("--minimized")) window.Hide();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "WinWindow 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private void OpenFixture(string[] args)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        bool reject = args.Contains("reject");
        var window = new Window { Title = reject ? "窗口测试 · 拒绝最小化" : "窗口测试 · 文档工作区", Width = 640, Height = 420, Content = new TextBlock { Text = "WinWindow 自动验证目标窗口", Margin = new Thickness(32), FontSize = 24 } };
        MainWindow = window;
        if (reject) window.StateChanged += (_, _) => { if (window.WindowState == WindowState.Minimized) window.Dispatcher.BeginInvoke(new Action(() => window.WindowState = WindowState.Normal), DispatcherPriority.Send); };
        window.Show();
        File.WriteAllText(args[1], new WindowInteropHelper(window).Handle.ToInt64().ToString());
    }
    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.Cleanup();
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
