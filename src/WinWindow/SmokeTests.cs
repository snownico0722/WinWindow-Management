using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinWindow;

internal static class SmokeTests
{
    private static readonly List<string> Results = new();
    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Results.Add("PASS: " + name);
    }
    private static async Task WaitAsync(string name, Func<bool> condition, int milliseconds = 3000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > milliseconds) throw new TimeoutException(name);
            await Task.Delay(25);
        }
    }
    private static Process Start(params string[] args)
    {
        string executable = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动测试进程。");
    }
    private static async Task<(Process Process, nint Hwnd)> FixtureAsync(string directory, bool reject, string? stall = null)
    {
        string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".hwnd");
        var args = new List<string> { "--fixture", file };
        if (reject) args.Add("reject");
        if (stall is not null) { args.Add("stall"); args.Add(stall); }
        var process = Start(args.ToArray());
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(file) && long.TryParse(await File.ReadAllTextAsync(file), out long hwnd)) return (process, (nint)hwnd);
            if (process.HasExited) throw new InvalidOperationException("测试窗口提前退出。");
            await Task.Delay(100);
        }
        process.Kill(true); process.Dispose(); throw new TimeoutException("测试窗口没有就绪。");
    }
    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
    }
    public static async Task<int> RunAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        string data = Path.Combine(output, "isolated-state-" + Guid.NewGuid().ToString("N"));
        var processes = new List<Process>();
        WindowManager? manager = null; MainWindow? shell = null;
        int exit = 0;
        try
        {
            Assert(Native.ToAlpha(0) == 0 && Native.ToAlpha(100) == 255 && Enumerable.Range(0, 101).All(x => Native.ToPercent(Native.ToAlpha(x)) == x), "opacity accepts zero and round-trips 0–100");
            Assert(!OwnWindowPolicy.Topmost(false, false, true, false) && OwnWindowPolicy.Topmost(false, false, true, true) && OwnWindowPolicy.Topmost(false, true, false, false), "manual, focus and compact topmost are independent");
            Assert(!OwnWindowPolicy.Enforce(false, false, true, false, true) && !OwnWindowPolicy.Enforce(true, true, true, false, true) && !OwnWindowPolicy.Enforce(true, false, false, false, true), "hidden, minimized and focus-only windows never enforce absolute topmost");
            Assert(DesktopIntegration.ParseHotkey("Win+Ctrl+Shift+R") == (14u, 82u) && DesktopIntegration.ParseHotkey("") == (0u, 0u), "hotkey parsing and disabled bindings");
            var store = new StateStore(data);
            store.Preferences.Rules.Add(new() { Title = "roundtrip", Opacity = 0, ClickThrough = true }); store.SavePreferences();
            Assert(new StateStore(data).Preferences.Rules.Single().Opacity == 0, "settings preserve zero opacity");
            store.Preferences.Rules.Clear(); store.SavePreferences();
            var normal = await FixtureAsync(output, false); processes.Add(normal.Process);
            manager = new WindowManager(store);
            var item = manager.SelectTarget(normal.Hwnd);
            long originalStyle = Native.Style(item.Hwnd); bool originalPin = Native.IsTopmost(item.Hwnd);
            Assert(item.Identity is not null && !Native.Matches(item.Identity with { ProcessStarted = item.Identity.ProcessStarted + 1 }), "recovery rejects a different process lifetime");
            await manager.SetPinAsync(item, true);
            Assert(Native.IsTopmost(item.Hwnd), "native topmost is actually applied");
            await manager.SetOpacityAsync(item, 0);
            Assert(Native.GetLayeredWindowAttributes(item.Hwnd, out _, out byte alpha, out _) && alpha == 0, "native opacity really reaches zero");
            await manager.SetClickThroughAsync(item, true);
            Assert((Native.Style(item.Hwnd) & Native.Transparent) != 0, "native click-through is applied");
            Assert(store.ReadRecovery().Count == 1, "restorable snapshot is durable before mutations");
            await manager.ReleaseAsync(item);
            Assert(Native.IsTopmost(item.Hwnd) == originalPin && (Native.Style(item.Hwnd) & (Native.Layered | Native.Transparent)) == (originalStyle & (Native.Layered | Native.Transparent)), "release restores original style bits and pin state");
            Native.ShowWindowAsync(item.Hwnd, Native.Maximized); await Task.Delay(250);
            var before = Native.GetPlacement(item.Hwnd);
            await manager.ParkAsync(item);
            Assert(Native.IsIconic(item.Hwnd) && item.IsParked, "force minimize produces a real iconic window");
            await manager.RestoreParkingAsync(item, false);
            Assert(Native.IsWindowVisible(item.Hwnd) && !Native.IsIconic(item.Hwnd) && Native.GetPlacement(item.Hwnd).ShowCmd == before.ShowCmd, "restore preserves pre-minimize maximized state");
            await manager.ReleaseAsync(item);
            var rejected = await FixtureAsync(output, true); processes.Add(rejected.Process);
            var stubborn = manager.SelectTarget(rejected.Hwnd);
            bool rejectedOpacity = false;
            try { await manager.SetOpacityAsync(stubborn, 0); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { rejectedOpacity = true; }
            Assert(rejectedOpacity && stubborn.Opacity == 100 && (Native.Style(stubborn.Hwnd) & Native.Layered) == 0, "WPF opacity veto is reported without claiming success");
            await manager.ReleaseAsync(stubborn);
            Assert(stubborn.Original is null, "an unchanged unsupported window does not block release");
            await manager.ParkAsync(stubborn);
            Assert(!Native.IsWindowVisible(stubborn.Hwnd) && stubborn.IsHidden && stubborn.IsParked, "refused minimization falls back to hidden");
            await manager.RestoreParkingAsync(stubborn, false);
            Assert(Native.IsWindowVisible(stubborn.Hwnd), "hidden fallback can be restored");
            await manager.ReleaseAsync(stubborn);
            await manager.SetOpacityAsync(item, 0);
            var recovered = new WindowManager(store);
            await recovered.RecoverPreviousSessionAsync();
            await WaitAsync("startup recovery", () => store.ReadRecovery().Count == 0);
            Assert((Native.Style(item.Hwnd) & Native.Layered) == (originalStyle & Native.Layered), "startup recovery restores a surviving invisible window");
            await manager.ReleaseAsync(item);
            Native.ShowWindowAsync(item.Hwnd, Native.Normal);
            shell = new MainWindow(store, true); Application.Current.MainWindow = shell;
            shell.Show(); await shell.InitializeAsync(); await Task.Delay(150);
            await shell.Manager.RefreshAsync();
            var chosen = shell.Manager.Items.First(x => x.Hwnd == item.Hwnd);
            await shell.Manager.SetPinAsync(chosen, true); await shell.Manager.SetOpacityAsync(chosen, 68);
            shell.SelectForTest(chosen);
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            shell.UpdateLayout(); Capture(shell, Path.Combine(output, "workbench-light.png"));
            Palette.Apply(true); shell.UpdateLayout(); await Task.Delay(100);
            Capture(shell, Path.Combine(output, "workbench-dark.png"));
            ((Button)shell.FindName("BrandArea")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
            nint own = new WindowInteropHelper(shell).Handle;
            Assert(shell.Width == 180 && Native.IsTopmost(own), "compact mode pins the native HWND, independently of WPF Topmost");
            Capture(shell, Path.Combine(output, "compact-dark.png"));
            ((Button)shell.FindName("BrandArea")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(shell.Width >= 880 && shell.MinWidth == 880 && double.IsPositiveInfinity(shell.MaxWidth), "workbench remains spacious and has no maximum width");
            Palette.Apply(false); shell.Width = 880; shell.Height = 560; shell.UpdateLayout();
            Capture(shell, Path.Combine(output, "workbench-minimum.png"));
            ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100); shell.UpdateLayout();
            Assert(((TextBox)shell.FindName("ShowKeyBox")).Text == "Win+Ctrl+Space", "settings retain the actual hotkey text");
            Capture(shell, Path.Combine(output, "settings-minimum.png"));
            ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var row = await FindRowAsync(shell, chosen);
            var slider = (Slider)row.FindName("OpacitySlider");
            slider.Focus(); slider.Value = 25; slider.Value = 73;
            await WaitAsync("coalesced slider edit", () => Native.GetLayeredWindowAttributes(item.Hwnd, out _, out byte a, out _) && a == Native.ToAlpha(73));
            Assert(slider.IsEnabled && slider.Value == 73, "slider keeps accepting input and applies the latest value");
            await WaitAsync("slider completion", () => !chosen.IsBusy);
            await shell.AddTargetAsync(chosen.Hwnd); await shell.AddTargetAsync(chosen.Hwnd);
            Assert(chosen.Pinned && shell.Manager.Items.Count(x => x.Hwnd == chosen.Hwnd) == 1, "adding an existing row never unpins or duplicates it");
            row = await FindRowAsync(shell, chosen);
            ((ToggleButton)row.FindName("PinButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitAsync("inline unpin", () => !chosen.Pinned && !chosen.IsBusy);
            Assert(chosen.IsManaged && ((ListBox)shell.FindName("WindowList")).Items.Contains(chosen) && chosen.Opacity == 73, "row pin toggles only topmost, retaining membership and opacity");
            ((Button)row.FindName("ParkButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitAsync("inline minimize", () => Native.IsIconic(chosen.Hwnd) && !chosen.IsBusy);
            Assert((string)((Button)row.FindName("ParkButton")).ToolTip == "恢复窗口", "same row action changes from minimize to restore");
            ((Button)row.FindName("ParkButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitAsync("inline restore", () => !Native.IsIconic(chosen.Hwnd) && !chosen.IsBusy);
            Native.ShowWindowAsync(chosen.Hwnd, Native.Maximized); await Task.Delay(200);
            shell.ShowShell();
            ((Button)row.FindName("TitleButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitAsync("single-click target activation", () => Native.GetForegroundWindow() == chosen.Hwnd && !chosen.IsBusy);
            Assert(Native.GetPlacement(chosen.Hwnd).ShowCmd == Native.Maximized, "title brings a covered maximized target forward without resizing it");
            await shell.Manager.ReleaseAsync(chosen);
            Assert(chosen.IsManaged && chosen.Original is null, "restoring original settings preserves explicit list membership");
            await shell.AddTargetAsync(chosen.Hwnd); await shell.Manager.SetOpacityAsync(chosen, 41);
            await shell.Manager.UnpinAllAsync();
            Assert(!chosen.Pinned && chosen.IsManaged && chosen.Opacity == 41, "unpin all is not restore all or remove all");
            await Task.Run(() => Native.SetTopmost(stubborn.Hwnd, true));
            await shell.AddTargetAsync(stubborn.Hwnd);
            var extra = shell.Manager.Items.Single(x => x.Hwnd == stubborn.Hwnd);
            var extraRow = await FindRowAsync(shell, extra);
            ((Button)extraRow.FindName("RemoveButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitAsync("inline remove", () => !shell.Manager.Items.Contains(extra));
            Assert(Native.IsWindow(stubborn.Hwnd) && !Native.IsTopmost(stubborn.Hwnd), "row X unpins and removes, never closes or re-pins the target");
            await shell.Manager.RestoreAllAsync();
            Assert(store.ReadRecovery().Count == 0, "normal recovery clears the journal");
            shell.ShowShell(); await WaitAsync("own activation", () => Native.GetForegroundWindow() == own);
            Assert(Native.IsTopmost(own), "actual focus temporarily pins the workbench");
            Native.SetForegroundWindow(normal.Hwnd);
            await WaitAsync("focus-only pin must yield", () => Native.GetForegroundWindow() == normal.Hwnd && !Native.IsTopmost(own) && Native.IsAbove(normal.Hwnd, own));
            await Task.Delay(250);
            Assert(Native.GetForegroundWindow() == normal.Hwnd && Native.IsAbove(normal.Hwnd, own), "losing focus neither reactivates nor covers the new foreground app");
            store.Preferences.ManualTopmost = true; shell.ApplyOwnPolicy();
            Assert(Native.IsTopmost(own) && Native.GetForegroundWindow() == normal.Hwnd, "manual topmost does not steal keyboard focus");
            store.Preferences.ManualTopmost = false; shell.ApplyOwnPolicy();
            shell.WindowState = WindowState.Minimized; await Task.Delay(150);
            Assert(Native.IsIconic(own) && !Native.IsTopmost(own), "minimized workbench is not raised by policy");
            shell.ShowShell(); await WaitAsync("minimized wake", () => !Native.IsIconic(own) && Native.GetForegroundWindow() == own);
            shell.Hide(); shell.ShowShell();
            Assert(Native.IsWindowVisible(own), "hidden workbench can be explicitly shown");
            store.Preferences.PinWhenFocused = false; shell.ShowShell();
            Assert(!Native.IsTopmost(own), "wake raise is cleaned up immediately with focus policy off");
            shell.Cleanup(); shell.Close(); shell = null;
            await manager.RestoreAllAsync(); manager = null;
            foreach (var p in processes) { Stop(p); await p.WaitForExitAsync(); }
            await DesktopRegressionAsync(output, processes);
        }
        catch (Exception e) { Results.Add(e.ToString()); exit = 1; }
        finally
        {
            if (shell is not null) { await shell.Manager.ShutdownAsync(); shell.Cleanup(); shell.Close(); }
            if (manager is not null) await manager.ShutdownAsync();
            foreach (var process in processes) { Stop(process); process.Dispose(); }
            await File.WriteAllLinesAsync(Path.Combine(output, "smoke-tests.txt"), Results);
        }
        return exit;
    }
    private static StateStore ChildStore(string output, bool tray)
    {
        var store = new StateStore(Path.Combine(output, "desktop-" + Guid.NewGuid().ToString("N")));
        store.Preferences.CloseToTray = tray;
        store.Preferences.Hotkeys = new()
        {
            ["show"] = "Ctrl+Alt+Shift+F5", ["pin"] = "Ctrl+Alt+Shift+F6", ["park"] = "Ctrl+Alt+Shift+F7",
            ["recover"] = "Ctrl+Alt+Shift+F8", ["opacityUp"] = "Ctrl+Alt+Shift+F9", ["opacityDown"] = "Ctrl+Alt+Shift+F10"
        };
        store.SavePreferences(); return store;
    }
    private static async Task<(Process Process, nint Hwnd)> StartMainAsync(StateStore store, List<Process> processes)
    {
        Assert(Native.FindWindow(null, "WinWindow Management · Dev") == 0, "test will not use an existing development instance");
        var process = Start("--dev", "--data-dir", store.DirectoryPath, "--minimized"); processes.Add(process);
        nint hwnd = 0;
        await WaitAsync("real desktop integration startup", () =>
        {
            hwnd = Native.FindWindow(null, "WinWindow Management · Dev");
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            return hwnd != 0 && pid == process.Id;
        }, 6000);
        if (!await Task.Run(() => process.WaitForInputIdle(6000))) throw new TimeoutException("Desktop process did not enter its message loop.");
        await WaitAsync("startup finished hiding to tray", () => !Native.IsWindowVisible(hwnd), 6000);
        return (process, hwnd);
    }
    private static async Task WakeAsync(StateStore store, nint hwnd)
    {
        using var second = Start("--dev", "--data-dir", store.DirectoryPath);
        try
        {
            await WaitAsync("second launch exits", () => second.HasExited);
            Assert(second.ExitCode == 0, "single-instance handoff reports success");
            await WaitAsync("second launch wakes existing instance", () => Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd) && Native.GetForegroundWindow() == hwnd);
        }
        finally { Stop(second); }
    }
    private static WindowRule Rule(nint hwnd)
    {
        var id = Native.Identity(hwnd)!;
        return new() { ProcessPath = id.ProcessPath, ClassName = id.ClassName, Title = Native.Title(hwnd), Pinned = true };
    }
    private static async Task DesktopRegressionAsync(string output, List<Process> processes)
    {
        var trayStore = ChildStore(output, true);
        var tray = await StartMainAsync(trayStore, processes);
        await WakeAsync(trayStore, tray.Hwnd);
        Native.PostMessage(tray.Hwnd, 0x10, 0, 0);
        await WaitAsync("close to tray", () => !Native.IsWindowVisible(tray.Hwnd));
        await WakeAsync(trayStore, tray.Hwnd);
        Assert(Native.IsWindowVisible(tray.Hwnd), "real close-to-tray and second-launch wake");
        Native.PostMessage(tray.Hwnd, 0x112, 0xF020, 0);
        await WaitAsync("native minimize", () => Native.IsIconic(tray.Hwnd));
        await WakeAsync(trayStore, tray.Hwnd);
        Assert(!Native.IsIconic(tray.Hwnd), "real desktop entry wakes a minimized workbench");
        Stop(tray.Process); await tray.Process.WaitForExitAsync();
        string name = @"Local\WinWindow.Stall." + Guid.NewGuid().ToString("N");
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".entered");
        using var release = new EventWaitHandle(false, EventResetMode.ManReset, name + ".release");
        try
        {
            var stuck = await FixtureAsync(output, false, name); processes.Add(stuck.Process);
            var healthy = await FixtureAsync(output, true); processes.Add(healthy.Process);
            var store = ChildStore(output, false);
            store.Preferences.Rules.Add(Rule(stuck.Hwnd)); store.Preferences.Rules.Add(Rule(healthy.Hwnd)); store.SavePreferences();
            Native.PostMessage(stuck.Hwnd, 0x8014, 0, 0); await Task.Delay(100);
            var app = await StartMainAsync(store, processes);
            await WaitAsync("worker entered blocked foreign WndProc", () => entered.WaitOne(0));
            await WaitAsync("independent healthy target still operates", () => Native.IsTopmost(healthy.Hwnd));
            Assert(!release.WaitOne(0), "foreign WndProc remains blocked during responsiveness checks");
            await WakeAsync(store, app.Hwnd);
            Native.PostMessage(app.Hwnd, 0x112, 0xF020, 0);
            await WaitAsync("minimize while target blocked", () => Native.IsIconic(app.Hwnd));
            await WakeAsync(store, app.Hwnd);
            var clock = Stopwatch.StartNew(); Native.PostMessage(app.Hwnd, 0x10, 0, 0);
            await WaitAsync("quit cannot wait indefinitely for another process", () => app.Process.HasExited);
            Assert(app.Process.ExitCode == 0 && clock.ElapsedMilliseconds < 3000, "actual application exits with a blocked foreign operation");
            Assert(!Native.IsTopmost(healthy.Hwnd), "exit restores healthy windows despite a blocked target");
            Assert(store.ReadRecovery().Any(s => s.Identity.Hwnd == stuck.Hwnd), "unfinished operation remains durably recoverable after exit");
            Assert(store.ReadRecovery().All(s => s.Identity.Hwnd != healthy.Hwnd), "successful exit recovery is removed from journal");
            Assert(!release.WaitOne(0), "quit did not require releasing the hung target");
            release.Set(); await Task.Delay(150);
            var recovered = await StartMainAsync(store, processes);
            await WaitAsync("unfinished journal recovered on next launch", () => store.ReadRecovery().All(s => s.Identity.Hwnd != stuck.Hwnd) && !Native.IsTopmost(stuck.Hwnd));
            Assert(!Native.IsTopmost(stuck.Hwnd), "next launch restores the formerly blocked target");
            Native.PostMessage(recovered.Hwnd, 0x10, 0, 0);
            await WaitAsync("recovered app exits", () => recovered.Process.HasExited);
        }
        finally { release.Set(); }
    }
    private static async Task<WindowRow> FindRowAsync(MainWindow shell, WindowItem item)
    {
        shell.SelectForTest(item); shell.UpdateLayout();
        // UpdateLayout realizes the template before WPF dispatches Loaded.
        // Let the row attach its model before sending synthetic button input.
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        static WindowRow? Walk(DependencyObject node, WindowItem target)
        {
            if (node is WindowRow row && ReferenceEquals(row.DataContext, target)) return row;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (Walk(VisualTreeHelper.GetChild(node, i), target) is { } found) return found;
            return null;
        }
        var row = Walk(shell, item) ?? throw new InvalidOperationException("Managed row not realized.");
        if (!row.IsLoaded) throw new InvalidOperationException("Managed row has not completed Loaded.");
        return row;
    }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
