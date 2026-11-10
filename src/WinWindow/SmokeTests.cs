using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
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
    private static async Task<(Process Process, nint Hwnd)> FixtureAsync(string directory, bool reject)
    {
        string file = Path.Combine(directory, (reject ? "reject-" : "normal-") + Guid.NewGuid().ToString("N") + ".hwnd");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(file);
        if (reject) start.ArgumentList.Add("reject");
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动测试窗口。");
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(file) && long.TryParse(await File.ReadAllTextAsync(file), out long hwnd)) return (process, (nint)hwnd);
            if (process.HasExited) throw new InvalidOperationException("测试窗口提前退出。");
            await Task.Delay(100);
        }
        process.Kill(true); process.Dispose();
        throw new TimeoutException("测试窗口没有就绪。");
    }
    public static async Task<int> RunAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        string data = Path.Combine(output, "isolated-state-" + Guid.NewGuid().ToString("N"));
        var processes = new List<Process>();
        WindowManager? manager = null;
        MainWindow? shell = null;
        int exit = 0;
        try
        {
            Assert(Native.ToAlpha(0) == 0 && Native.ToAlpha(100) == 255 && Enumerable.Range(0, 101).All(x => Native.ToPercent(Native.ToAlpha(x)) == x), "opacity accepts zero and round-trips 0–100");
            Assert(!OwnWindowPolicy.Topmost(false, false, true, false) && OwnWindowPolicy.Topmost(false, false, true, true) && OwnWindowPolicy.Topmost(false, true, false, false), "manual, focus and compact topmost are independent");
            Assert(!OwnWindowPolicy.Enforce(false, false, true, false, true) && !OwnWindowPolicy.Enforce(true, true, true, false, true) && !OwnWindowPolicy.Enforce(true, false, false, false, true), "hidden, minimized and focus-only windows never enforce absolute topmost");
            Assert(DesktopIntegration.ParseHotkey("Win+Ctrl+Shift+R") == (14u, 82u) && DesktopIntegration.ParseHotkey("") == (0u, 0u), "hotkey parsing and disabled bindings");
            var store = new StateStore(data);
            store.Preferences.Rules.Add(new() { Title = "roundtrip", Opacity = 0, ClickThrough = true });
            store.SavePreferences();
            var reload = new StateStore(data);
            Assert(reload.Preferences.Rules.Single().Opacity == 0, "settings preserve zero opacity");
            store.Preferences.Rules.Clear(); store.SavePreferences();
            var normal = await FixtureAsync(output, false); processes.Add(normal.Process);
            manager = new WindowManager(store);
            var item = manager.SelectTarget(normal.Hwnd);
            long originalStyle = Native.Style(item.Hwnd);
            bool originalPin = Native.IsTopmost(item.Hwnd);
            Assert(item.Identity is not null && !Native.Matches(item.Identity with { ProcessStarted = item.Identity.ProcessStarted + 1 }), "recovery rejects a different process lifetime");
            manager.SetPin(item, true);
            Assert(Native.IsTopmost(item.Hwnd), "native topmost is actually applied");
            manager.SetOpacity(item, 0);
            Assert(Native.GetLayeredWindowAttributes(item.Hwnd, out _, out byte alpha, out _) && alpha == 0, "native opacity really reaches zero");
            manager.SetClickThrough(item, true);
            Assert((Native.Style(item.Hwnd) & Native.Transparent) != 0, "native click-through is applied");
            Assert(store.ReadRecovery().Count == 1, "restorable snapshot is durable before mutations");
            await manager.ReleaseAsync(item);
            Assert(Native.IsTopmost(item.Hwnd) == originalPin && (Native.Style(item.Hwnd) & (Native.Layered | Native.Transparent)) == (originalStyle & (Native.Layered | Native.Transparent)), "release restores original style bits and pin state");
            Native.ShowWindowAsync(item.Hwnd, Native.Maximized); await Task.Delay(250);
            var before = Native.GetPlacement(item.Hwnd);
            string parked = await manager.ParkAsync(item);
            Assert(Native.IsIconic(item.Hwnd) && item.IsParked, "force minimize produces a real iconic window: " + parked);
            await manager.RestoreParkingAsync(item, false);
            Assert(Native.IsWindowVisible(item.Hwnd) && !Native.IsIconic(item.Hwnd) && Native.GetPlacement(item.Hwnd).ShowCmd == before.ShowCmd, "restore preserves pre-minimize maximized state");
            await manager.ReleaseAsync(item);
            var rejected = await FixtureAsync(output, true); processes.Add(rejected.Process);
            var stubborn = manager.SelectTarget(rejected.Hwnd);
            bool rejectedOpacity = false;
            try { manager.SetOpacity(stubborn, 0); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { rejectedOpacity = true; }
            Assert(rejectedOpacity && stubborn.Opacity == 100 && (Native.Style(stubborn.Hwnd) & Native.Layered) == 0, "WPF opacity veto is reported without claiming success");
            await manager.ReleaseAsync(stubborn);
            Assert(stubborn.Original is null, "an unchanged unsupported window does not block release");
            string hidden = await manager.ParkAsync(stubborn);
            Assert(!Native.IsWindowVisible(stubborn.Hwnd) && stubborn.IsHidden && stubborn.IsParked, "refused minimization falls back to hidden, not fake success: " + hidden);
            await manager.RestoreParkingAsync(stubborn, false);
            Assert(Native.IsWindowVisible(stubborn.Hwnd), "hidden fallback can be restored");
            await manager.ReleaseAsync(stubborn);
            manager.SetOpacity(item, 0);
            var recovered = new WindowManager(store);
            await recovered.RecoverPreviousSessionAsync();
            Assert((Native.Style(item.Hwnd) & Native.Layered) == (originalStyle & Native.Layered) && store.ReadRecovery().Count == 0, "startup recovery restores a surviving invisible window");
            await manager.ReleaseAsync(item);
            Native.ShowWindowAsync(item.Hwnd, Native.Normal);
            shell = new MainWindow(store, true);
            Application.Current.MainWindow = shell;
            shell.Show(); await shell.InitializeAsync(); await Task.Delay(150);
            shell.Manager.Refresh();
            var chosen = shell.Manager.Items.First(x => x.Hwnd == item.Hwnd);
            shell.Manager.SetPin(chosen, true);
            shell.Manager.SetOpacity(chosen, 68);
            shell.SelectForTest(chosen);
            shell.ShowStatus("自动验证窗口 · 0–100% 不透明度 / 强制最小化 / 一键恢复");
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            shell.UpdateLayout();
            Capture(shell, Path.Combine(output, "workbench-light.png"));
            Palette.Apply(true); shell.UpdateLayout(); await Task.Delay(100);
            Capture(shell, Path.Combine(output, "workbench-dark.png"));
            shell.ToggleCompact(); await Task.Delay(100);
            Assert(shell.Width == 360 && shell.Topmost, "compact mode is separate from the full workbench");
            Capture(shell, Path.Combine(output, "compact-dark.png"));
            shell.ToggleCompact();
            Assert(shell.Width >= 880 && shell.MinWidth == 880 && double.IsPositiveInfinity(shell.MaxWidth), "full workbench is spacious, resizable and has no maximum width");
            await shell.Manager.RestoreAllAsync();
            Assert(store.ReadRecovery().Count == 0, "normal recovery clears the journal");
        }
        catch (Exception e) { Results.Add(e.ToString()); exit = 1; }
        finally
        {
            if (shell is not null) { await shell.Manager.RestoreAllAsync(); shell.Cleanup(); shell.Close(); }
            if (manager is not null) await manager.RestoreAllAsync();
            foreach (var process in processes)
            {
                try { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(1500)) process.Kill(true); } } catch (InvalidOperationException) { }
                process.Dispose();
            }
            await File.WriteAllLinesAsync(Path.Combine(output, "smoke-tests.txt"), Results);
        }
        return exit;
    }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
