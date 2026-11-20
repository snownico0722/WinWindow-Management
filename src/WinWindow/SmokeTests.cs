using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using NeatWin.Core;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinWindow;

internal static class SmokeTests
{
    private static StreamWriter? _results;
    private static void Report(string message) => _results!.WriteLine(message);
    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Report("PASS: " + name);
    }
    private static async Task WaitAsync(string name, Func<bool> condition, int milliseconds = 3000)
    {
        Report("CHECK: " + name);
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > milliseconds) throw new TimeoutException(name);
            await Task.Delay(25);
        }
    }
    private static Process Start(params string[] args)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, typeof(App).Assembly.GetName().Name + ".exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动测试进程。");
    }
    private static async Task<(Process Process, nint Hwnd)> FixtureAsync(string directory, bool reject, string? stall = null, bool activity = false, bool twins = false)
    {
        string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".hwnd");
        var args = new List<string> { "--fixture", file };
        if (reject) args.Add("reject");
        if (activity) args.Add("activity");
        if (twins) args.Add("twins");
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
        using var report = new StreamWriter(Path.Combine(output, "smoke-tests.txt")) { AutoFlush = true };
        _results = report;
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
            if (string.IsNullOrEmpty(typeof(App).Assembly.Location))
            {
                CheckNativeResource("x86", Machine.I386);
                CheckNativeResource("x64", Machine.Amd64);
            }
            CheckSmartArrangeScenarios();
            var store = new StateStore(data);
            store.Preferences.Language = "zh-CN";
            store.Preferences.Rules.Add(new() { Title = "roundtrip", Opacity = 0, ClickThrough = true }); store.SavePreferences();
            Assert(new StateStore(data).Preferences.Rules.Single().Opacity == 0, "settings preserve zero opacity");
            store.Preferences.Rules.Clear(); store.SavePreferences();
            var twins = await FixtureAsync(output, false, twins: true);
            processes.Add(twins.Process);
            await CheckDuplicateRulesAsync(twins.Hwnd, data);
            Stop(twins.Process);
            await twins.Process.WaitForExitAsync();
            var normal = await FixtureAsync(output, false); processes.Add(normal.Process);
            manager = new WindowManager(store);
            var item = manager.SelectTarget(normal.Hwnd);
            long originalStyle = Native.Style(item.Hwnd); bool originalPin = Native.IsTopmost(item.Hwnd);
            Assert(item.Identity is not null && !Native.Matches(item.Identity with { ProcessStarted = item.Identity.ProcessStarted + 1 }), "recovery rejects a different process lifetime");
            var layoutWindows = new NeatWin.Windows.WindowManager();
            var source = layoutWindows.Capture().Single(w => w.Handle == item.Hwnd);
            Assert(source.IsManageable, "layout sees the normal fixture");
            CheckActivityRecording(source, data);
            var target = source.VisualRect with { X = source.VisualRect.X + (source.VisualRect.X > source.WorkArea.X + 40 ? -32 : 32) };
            var applied = layoutWindows.ApplyLayout(new([new(source, target)], [], []));
            Assert(applied.Plan.Moves.Count == 1, "ordinary fixture is accepted for layout");
            bool Reached(NeatWin.Core.RectI rect) => layoutWindows.Capture().Any(w => w.Handle == item.Hwnd &&
                NeatWin.Core.WindowStateRules.Near(w.VisualRect, rect));
            await WaitAsync("fixture reaches layout target", () => Reached(target));
            var undo = NeatWin.Core.LayoutUndoPlanner.Create(applied.Plan, layoutWindows.Capture());
            Assert(layoutWindows.ApplyLayout(undo).Plan.Moves.Count == 1, "verified layout can be undone");
            await WaitAsync("layout undo restores fixture geometry", () => Reached(source.VisualRect));
            await manager.SetPinAsync(item, true);
            Assert(store.Preferences.Rules.Single().Pinned, "pinning automatically persists a rule");
            Native.SetTopmost(item.Hwnd, false);
            await manager.ReinforcePinnedAsync();
            Assert(Native.IsTopmost(item.Hwnd), "restore a managed pin after TOPMOST is lost");
            Assert(!layoutWindows.Capture().Single(w => w.Handle == item.Hwnd).IsManageable, "layout protects a WinWindow-pinned target");
            Assert(Native.IsTopmost(item.Hwnd), "native topmost is actually applied");
            await manager.SetOpacityAsync(item, 0);
            Assert(Native.GetLayeredWindowAttributes(item.Hwnd, out _, out byte alpha, out _) && alpha == 0, "native opacity really reaches zero");
            Assert(layoutWindows.Capture().All(w => w.Handle != item.Hwnd), "a fully transparent window does not obstruct layout");
            await manager.SetClickThroughAsync(item, true);
            Assert((Native.Style(item.Hwnd) & Native.Transparent) != 0, "native click-through is applied");
            Assert(store.Preferences.Rules.Single().ClickThrough && store.Preferences.Rules.Single().Opacity == 0,
                "remembered styles include zero opacity and click-through");
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
            Assert(store.Preferences.Rules.Single().Opacity == 0, "crash recovery preserves the saved opacity rule");
            await recovered.RefreshAsync();
            await WaitAsync("reapply saved opacity after crash recovery", () =>
                Native.GetLayeredWindowAttributes(item.Hwnd, out _, out byte opacity, out _) && opacity == 0 &&
                !recovered.Items.Single(x => x.Hwnd == item.Hwnd).IsBusy);
            await recovered.ShutdownAsync();
            await manager.ReleaseAsync(item);
            manager.SetRemembered(item, false);
            Native.ShowWindowAsync(item.Hwnd, Native.Normal);
            shell = new MainWindow(store, true); Application.Current.MainWindow = shell;
            shell.Show(); await shell.InitializeAsync(); await Task.Delay(150);
            shell.ShowShell();
            await WaitAsync("foreground before toggling the workbench", () => Native.GetForegroundWindow() == new WindowInteropHelper(shell).Handle);
            await shell.OnDesktopActionAsync("togglePanel");
            Assert(!shell.IsVisible, "the workbench shortcut hides the foreground workbench");
            await shell.OnDesktopActionAsync("togglePanel");
            Assert(shell.IsVisible, "the same shortcut shows the hidden workbench");
            var language = (ComboBox)shell.FindName("LanguageBox");
            var arrange = (Button)shell.FindName("ArrangeButton");
            language.SelectedValue = "en";
            await WaitAsync("English applies to the visible workbench", () => Equals(arrange.Content, "Arrange"));
            Assert(NeatWin.App.LayoutStrings.LayoutText.Get("NothingToUndo") == "No arrangement to undo.",
                "the layout worker uses the workbench language");
            store.SavePreferences();
            Assert(new StateStore(data).Preferences.Language == "en", "the display language survives a restart");
            language.SelectedValue = "zh-CN";
            await WaitAsync("Chinese applies without restarting", () => Equals(arrange.Content, "整理桌面"));
            await CheckArrangeButtonAsync(shell, layoutWindows, item.Hwnd, stubborn.Hwnd);
            await shell.Manager.RefreshAsync();
            var chosen = shell.Manager.Items.First(x => x.Hwnd == item.Hwnd);
            var game = await FixtureAsync(output, false, activity: true);
            processes.Add(game.Process);
            using (var desktop = new DesktopIntegration(shell, shell.Manager))
            {
                await shell.AddTargetAsync(game.Hwnd, pin: false);
                var gameItem = shell.Manager.Items.Single(w => w.Hwnd == game.Hwnd);
                Assert(gameItem.IsManaged && !Native.IsTopmost(game.Hwnd), "adding a background target does not pin it");
                shell.ShowShell();
                await WaitAsync("fixture pauses when another window is active", () => Native.Title(game.Hwnd).StartsWith("activity:paused:"));
                var gameRow = await FindRowAsync(shell, gameItem);
                Assert(!store.Preferences.EnhancedBackgroundKeep && !store.Preferences.LockBackgroundCursorPos,
                    "enhanced injection and cursor coordinate lock are off by default");
                bool nativeKeep = Native.HasNoFocusLossSupport();
                var keepRunning = (ToggleButton)gameRow.FindName("BackgroundToggle");
                keepRunning.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitAsync("default gentle toggle resumes a background fixture without injection", () =>
                    gameItem.BackgroundEnabled && !gameItem.IsBusy &&
                    Native.Title(game.Hwnd).StartsWith("activity:running:unfocused:"), 5000);
                Assert(!Native.IsNoFocusLossInjected(game.Hwnd) && !Native.HasLoadedNoFocusLossModule(game.Process.Id),
                    "default background keep does not inject native hooks");

                var peer = await FixtureAsync(output, false);
                processes.Add(peer.Process);
                await shell.AddTargetAsync(peer.Hwnd, pin: false);
                var peerItem = shell.Manager.Items.Single(w => w.Hwnd == peer.Hwnd);
                await shell.Manager.SetBackgroundEnabledAsync(peerItem, true, updateRule: false);
                var enhancedBox = (CheckBox)shell.FindName("EnhancedBackgroundBox");
                var lockCursorBox = (CheckBox)shell.FindName("LockCursorPosBox");
                Assert(!lockCursorBox.IsEnabled, "cursor coordinate lock requires enhanced injection");
                enhancedBox.IsChecked = true;
                enhancedBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(lockCursorBox.IsEnabled, "enabling enhanced injection unlocks the cursor lock option");
                await WaitAsync("enabling enhanced injection upgrades the active background target", () =>
                    gameItem.BackgroundEnabled && !gameItem.IsBusy &&
                    Native.Title(game.Hwnd).StartsWith(nativeKeep ? "activity:running:focused:" : "activity:running:"), 5000);
                if (nativeKeep)
                    Assert(Native.IsNoFocusLossInjected(game.Hwnd) && Native.HasLoadedNoFocusLossModule(game.Process.Id),
                        "native NoFocusLoss library is loaded when enhanced injection is enabled");
                if (nativeKeep)
                    await WaitAsync("the global switch also updates the second opted-in game", () =>
                        Native.IsNoFocusLossInjected(peer.Hwnd) && !peerItem.IsBusy, 5000);
                Assert(!Native.IsNoFocusLossInjected(normal.Hwnd), "a managed window with keep-active off is unaffected");
                lockCursorBox.IsChecked = true;
                lockCursorBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitAsync("cursor coordinate lock applies live", () => !gameItem.IsBusy, 5000);

                Native.SetForegroundWindow(game.Hwnd);
                await WaitAsync("fixture becomes foreground", () => Native.GetForegroundWindow() == game.Hwnd);
                shell.ShowShell();
                await WaitAsync("foreground returns to workbench", () => Native.GetForegroundWindow() == new WindowInteropHelper(shell).Handle);
                await Task.Delay(150);
                var title = Native.Title(game.Hwnd);
                await WaitAsync("background fixture heartbeat advances after switching away",
                    () => Native.Title(game.Hwnd).StartsWith(nativeKeep ? "activity:running:focused:" : "activity:running:") &&
                        Native.Title(game.Hwnd) != title);
                Assert(Native.GetForegroundWindow() != game.Hwnd && !Native.IsTopmost(game.Hwnd), "background keep leaves keyboard focus and z-order alone");

                enhancedBox.IsChecked = false;
                enhancedBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitAsync("disabling enhanced mode unloads hooks and keeps the default activation notice", () =>
                    gameItem.BackgroundEnabled && !gameItem.IsBusy &&
                    Native.Title(game.Hwnd).StartsWith("activity:running:unfocused:"), 5000);
                Assert(!Native.HasLoadedNoFocusLossModule(game.Process.Id), "switching to default mode unloads the library");
                await WaitAsync("turning global enhancement off returns both games to gentle mode", () =>
                    peerItem.BackgroundEnabled && !peerItem.IsBusy && !Native.HasLoadedNoFocusLossModule(peer.Process.Id), 5000);
                await shell.Manager.RemoveAsync(peerItem);
                Stop(peer.Process); await peer.Process.WaitForExitAsync();
                enhancedBox.IsChecked = true;
                enhancedBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitAsync("enhanced mode can be reapplied", () => !gameItem.IsBusy &&
                    (!nativeKeep || Native.Title(game.Hwnd).StartsWith("activity:running:focused:")), 5000);

                // Adding and removing the peer can recycle the original WPF row.
                gameRow = await FindRowAsync(shell, gameItem);
                keepRunning = (ToggleButton)gameRow.FindName("BackgroundToggle");
                var muteToggle = (ToggleButton)gameRow.FindName("MuteButton");
                muteToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitAsync("row mute button toggles mute state and remembers rule", () =>
                    gameItem.Muted && !gameItem.IsBusy && store.Preferences.Rules.Any(r => r.Id == gameItem.RuleId && r.Muted));
                muteToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitAsync("row mute button unmutes cleanly", () =>
                    !gameItem.Muted && !gameItem.IsBusy && store.Preferences.Rules.Any(r => r.Id == gameItem.RuleId && !r.Muted));
                muteToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitAsync("row mute button re-mutes before removal", () => gameItem.Muted && !gameItem.IsBusy);

                keepRunning.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitAsync("turning off the row toggle restores the background pause", () =>
                    !gameItem.BackgroundEnabled && !gameItem.IsBusy && Native.Title(game.Hwnd).StartsWith("activity:paused:unfocused:"), 5000);
                if (nativeKeep)
                    Assert(!Native.IsNoFocusLossInjected(game.Hwnd) && !Native.HasLoadedNoFocusLossModule(game.Process.Id),
                        "turning off the row toggle unloads the native NoFocusLoss library");
                keepRunning.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitAsync("enabling the row toggle again resumes the same window", () =>
                    gameItem.BackgroundEnabled && !gameItem.IsBusy &&
                    Native.Title(game.Hwnd).StartsWith(nativeKeep ? "activity:running:focused:" : "activity:running:"), 5000);
                await shell.Manager.RemoveAsync(gameItem);
                await WaitAsync("removal restores the fixture's background pause", () => Native.Title(game.Hwnd).StartsWith("activity:paused:unfocused:"));
                Assert(!gameItem.Muted, "removal restores mute state");
                if (nativeKeep)
                    Assert(!Native.IsNoFocusLossInjected(game.Hwnd) && !Native.HasLoadedNoFocusLossModule(game.Process.Id),
                        "removing a managed window unloads the native NoFocusLoss library");

                lockCursorBox.IsChecked = false;
                enhancedBox.IsChecked = false;
                enhancedBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                store.SavePreferences();
            }
            Stop(game.Process);
            await game.Process.WaitForExitAsync();
            await shell.Manager.SetPinAsync(chosen, true); await shell.Manager.SetOpacityAsync(chosen, 68);
            string layoutData = Path.Combine(data, "layout-check");
            var notice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var layout = new NeatWin.App.DesktopLayout(layoutData, message => notice.TrySetResult(message)))
            {
                var settings = await layout.ReadSettingsAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert(settings.Preferences.Mode == NeatWin.Core.AutomaticLayoutMode.Off && settings.Recorder.IsPaused, "unified settings read the engine defaults without starting recording");
                await layout.UndoAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert(await notice.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "没有可撤销的整理。", "embedded layout accepts commands on its own message loop");
                Assert(!Directory.Exists(Path.Combine(layoutData, "Recorder")), "opening layout settings leaves recording off");
            }
            shell.SelectForTest(chosen);
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            shell.UpdateLayout(); Capture(shell, Path.Combine(output, "workbench-light.png"));
            Palette.Apply(true); shell.UpdateLayout(); await Task.Delay(100);
            Capture(shell, Path.Combine(output, "workbench-dark.png"));
            ((Button)shell.FindName("BrandArea")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
            nint own = new WindowInteropHelper(shell).Handle;
            Assert(shell.Width == 240 && Native.IsTopmost(own), "compact mode pins the native HWND, independently of WPF Topmost");
            shell.UpdateLayout();
            var compactPick = (Button)shell.FindName("CompactPickerButton");
            var compactArrange = (Button)shell.FindName("CompactArrangeButton");
            Rect InShell(FrameworkElement control) => control.TransformToAncestor(shell).TransformBounds(new Rect(control.RenderSize));
            var pickBounds = InShell(compactPick);
            var arrangeBounds = InShell(compactArrange);
            Assert(compactPick.IsVisible && compactArrange.IsVisible && pickBounds.Width >= 24 && arrangeBounds.Width >= 24 &&
                InShell((Button)shell.FindName("BrandArea")).Right <= pickBounds.Left && pickBounds.Right <= arrangeBounds.Left &&
                arrangeBounds.Right <= shell.ActualWidth, "both capsule actions fit beside the managed-window count");
            Capture(shell, Path.Combine(output, "compact-dark.png"));
            ((Button)shell.FindName("BrandArea")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(shell.Width >= 500 && shell.MinWidth == 500 && double.IsPositiveInfinity(shell.MaxWidth), "workbench respects compact minimum and has no maximum width");
            Palette.Apply(false); shell.Width = 500; shell.Height = 380; shell.UpdateLayout();
            Capture(shell, Path.Combine(output, "workbench-minimum.png"));
            await CheckFooterModesAsync(shell, store, output);
            ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100); shell.UpdateLayout();
            Assert(((TextBox)shell.FindName("ShowKeyBox")).Text == "Win+Ctrl+Space", "settings retain the actual hotkey text");
            Capture(shell, Path.Combine(output, "settings-minimum.png"));
            var nav = (ListBox)shell.FindName("SettingsNav");
            nav.SelectedIndex = 1;
            var layoutPage = (ScrollViewer)shell.FindName("LayoutSection");
            await WaitAsync("integrated layout settings loaded", () => layoutPage.IsVisible && layoutPage.IsEnabled && ((CheckBox)shell.FindName("VerticalFill")).IsChecked.HasValue);
            var strength = ((Panel)shell.FindName("StrengthChoices")).Children.OfType<RadioButton>().Single(x => (string)x.Tag == "Assertive");
            strength.IsChecked = true;
            strength.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var layoutSettings = new NeatWin.App.SettingsStore(Path.Combine(store.DirectoryPath, "Layout", "settings.json"));
            await WaitAsync("layout edit persists through unified UI", () => layoutSettings.LoadTidyOptions().SmartStrength == NeatWin.Core.SmartTidyStrength.Assertive);
            shell.UpdateLayout(); Capture(shell, Path.Combine(output, "settings-layout.png"));
            nav.SelectedIndex = 3;
            await WaitAsync("recorder shares the same settings window", () => ((CheckBox)shell.FindName("RecordSwitch")).IsVisible && !layoutPage.IsVisible);
            nav.SelectedIndex = 2;
            await WaitAsync("hotkeys are reachable from the same sidebar", () => ((TextBox)shell.FindName("ShowKeyBox")).IsVisible);
            await CheckHotkeyDraftAsync(shell, store);
            ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var row = await FindRowAsync(shell, chosen);
            var slider = (Slider)row.FindName("OpacitySlider");
            var label = (TextBlock)row.FindName("BackgroundLabel");
            Assert(!label.IsVisible, "narrow cards keep the background switch compact");
            shell.Width = 820;
            await WaitAsync("wide cards label the background switch", () => label.IsVisible);
            shell.SelectForTest(chosen);
            var focusButton = (ToggleButton)shell.FindName("SearchToggleButton");
            focusButton.Focus();
            var list = (ListBox)shell.FindName("WindowList");
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(chosen);
            var frame = (Border)container.Template.FindName("Outer", container);
            Assert(container.IsSelected && !container.IsKeyboardFocusWithin && Equals(frame.BorderBrush, shell.FindResource("Accent")), "the target stays visibly selected when the toolbar has focus");
            shell.UpdateLayout();
            Capture(shell, Path.Combine(output, "workbench-wide.png"));
            var passiveWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            int beforeWheel = shell.DesiredOpacity(chosen);
            slider.RaiseEvent(passiveWheel);
            Assert(!passiveWheel.Handled && shell.DesiredOpacity(chosen) == beforeWheel, "an unfocused slider leaves the wheel to the list");
            slider.Focus();
            var activeWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            slider.RaiseEvent(activeWheel);
            Assert(activeWheel.Handled && shell.DesiredOpacity(chosen) == beforeWheel - 5, "a focused slider accepts deliberate wheel edits");
            slider.Value = 25; slider.Value = 73;
            await WaitAsync("coalesced slider edit", () => Native.GetLayeredWindowAttributes(item.Hwnd, out _, out byte a, out _) && a == Native.ToAlpha(73));
            Assert(slider.IsEnabled && slider.Value == 73, "slider keeps accepting input and applies the latest value");
            await WaitAsync("slider completion", () => !chosen.IsBusy);
            await CheckPickerOutlineAsync(shell, chosen.Hwnd);
            await CheckHoverPreviewAsync(shell, chosen);
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
            await CheckGameRecoveryAsync(output, processes);
        }
        catch (Exception e) { Report(e.ToString()); exit = 1; }
        finally
        {
            if (shell is not null) { await shell.Manager.ShutdownAsync(); shell.Cleanup(); shell.Close(); }
            if (manager is not null) await manager.ShutdownAsync();
            foreach (var process in processes) { Stop(process); process.Dispose(); }
            _results = null;
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
    private static async Task CheckDuplicateRulesAsync(nint hwnd, string data)
    {
        var store = new StateStore(Path.Combine(data, "duplicates"));
        var manager = new WindowManager(store);
        var identity = manager.SelectTarget(hwnd).Identity!;
        WindowRule Rule(int opacity) => new() { ProcessPath = identity.ProcessPath, ClassName = identity.ClassName,
            Title = Native.Title(hwnd), Opacity = opacity };
        var first = Rule(62);
        store.Preferences.Rules.Add(first);
        await manager.RefreshAsync();
        await WaitAsync("restore first duplicate rule", () => manager.Items.Any(x => x.RuleId == first.Id && !x.IsBusy && x.Opacity == 62));
        var pair = manager.Items.Where(x => x.Identity?.ProcessId == identity.ProcessId && first.Matches(x)).ToArray();
        Assert(pair.Length == 2 && pair.Count(x => x.RuleId == first.Id) == 1 && pair.Count(x => x.Opacity == 100) == 1,
            "one saved rule claims exactly one of two identically titled windows");
        await WaitAsync("duplicate windows load their own icons", () => pair.All(x => x.Icon is not null));
        byte[] Pixels(WindowItem window)
        {
            var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                (System.Windows.Media.Imaging.BitmapSource)window.Icon!, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        Assert(!Pixels(pair[0]).SequenceEqual(Pixels(pair[1])), "window-specific icons distinguish windows from one executable");
        var second = Rule(83);
        store.Preferences.Rules.Add(second);
        await manager.RefreshAsync();
        await WaitAsync("restore the other duplicate", () => pair.Any(x => x.RuleId == second.Id && !x.IsBusy && x.Opacity == 83));
        await manager.RefreshAsync();
        Assert(pair.Any(x => x.RuleId == first.Id && x.Opacity == 62), "later refreshes preserve both assignments");
        var forgotten = pair.Single(x => x.RuleId == first.Id);
        manager.SetRemembered(forgotten, false);
        await manager.SetOpacityAsync(forgotten, 54);
        Assert(forgotten.RuleId is null && store.Preferences.Rules.Count == 1, "explicitly forgotten windows stay forgotten after an edit");
        await manager.ShutdownAsync();
    }

    private static void CheckNativeResource(string architecture, Machine expected)
    {
        string name = $"WinWindow.Native.NoFocusLoss.{architecture}.dll";
        using var stream = typeof(App).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Packaged native resource missing: {name}");
        using var image = new PEReader(stream);
        var header = image.PEHeaders.CoffHeader;
        Assert(header.Machine == expected && header.Characteristics.HasFlag(Characteristics.Dll),
            $"published EXE embeds the {architecture} NoFocusLoss DLL");
    }

    private static void CheckActivityRecording(NeatWin.Core.WindowSnapshot fixture, string root)
    {
        var recorder = new NeatWin.Reference.IntentReferenceStore(Path.Combine(root, "ActivityFixture"));
        var activity = new NeatWin.Recording.DesktopActivity(recorder.Placement);
        var tracker = new ObservationIdentityTracker();
        var frame = tracker.Capture([fixture], NeatWin.Recording.DesktopActivity.Displays(), w => activity.AppKey(w, true));
        var tick = Environment.TickCount64;
        var first = activity.Sample("fixture", tick, frame, [fixture], tracker, false);
        var next = activity.Sample("fixture", tick + 5000, frame, [fixture], tracker, false);
        Assert(frame.Windows.Single().AppKey?.Length == 24 && next.Processes.Single().WorkingSetBytes > 0 &&
            next.Windows.Single().ComparedCells is null && first.ObservedMilliseconds == 0 && next.ObservedMilliseconds == 5000,
            "native recorder correlates a window and process and marks disabled visual sampling unknown");
        var gap = activity.Sample("fixture", tick + 60000, frame, [fixture], tracker, false);
        Assert(gap.ObservedMilliseconds == 0 && gap.Windows.Single().ForegroundMs is null, "recorder excludes interruptions from usage time");
        recorder.AppendActivity(next, frame, next.Tick);
        var path = Path.Combine(root, "activity-fixture.zip");
        recorder.ExportRecords(path);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(path))
            Assert(archive.Entries.Any(e => e.Name.StartsWith("activity-")) && archive.GetEntry("identity.key") == null &&
                archive.GetEntry("compiled-rules.json") != null && archive.GetEntry("layout-guide.md") != null,
                "export includes activity but excludes the installation identity secret");
        recorder.Clear();
    }

    private static void CheckSmartArrangeScenarios()
    {
        var area = new RectI(0, 0, 1920, 1040);
        var rect = new RectI(250, 150, 1300, 720);
        NeatWin.Core.WindowSnapshot Window(int rank, RectI bounds) =>
            new(rank + 1, bounds, bounds, area, 1, default, true, false, true, rank);
        var stack = new[] { Window(0, rect), Window(1, rect with { X = 270, Y = 170 }),
            Window(2, rect with { X = 290, Y = 190 }) };
        var analyzer = new VisibilityAnalyzer();
        IntentLayoutPlan Manual(NeatWin.Core.WindowSnapshot[] windows, bool allowResize = true) => LayoutDispatch.Create(
            LayoutRoute.ManualAlgorithm, analyzer.SelectForManualArrange(windows, true), new(), null,
            windows, new(Profile: new(AllowUsefulResize: allowResize), ExplicitArrange: true));

        var spread = Manual(stack);
        var targets = stack.Select(w => spread.Moves.FirstOrDefault(m => m.Window.Handle == w.Handle)?.TargetVisualRect ?? w.VisualRect).ToArray();
        Assert(targets.All(r => r.Intersect(area) == r) &&
            targets.SelectMany((r, i) => targets.Skip(i + 1).Select(b => r.Intersect(b))).All(r => r.IsEmpty),
            "explicit Smart fits a three-window cascade into readable nonoverlapping rows");
        Assert(Manual(stack, allowResize: false).Moves.All(m => m.TargetVisualRect.Width == m.Window.VisualRect.Width &&
            m.TargetVisualRect.Height == m.Window.VisualRect.Height), "manual arrange respects disabled resizing");
        Assert(analyzer.SelectVisibleWorkingSet(stack, includeStackAccess: true).Count == 1,
            "automatic Smart leaves the covered background out of its working set");
        var session = new TaskLayoutSession();
        var now = DateTimeOffset.UtcNow;
        var placed = stack.Select((w, i) => w with { VisualRect = targets[i] }).ToArray();
        session.Requested(stack, spread, now);
        session.Verify(placed);
        Assert(session.Capture(placed, new(), [], null, now).VerifiedRepeat &&
            !session.Capture(placed, new(), [], null, now, explicitArrange: true).VerifiedRepeat,
            "a verified automatic pass does not suppress the next explicit arrange");
        var fixedCover = Window(0, area) with { IsManageable = false, IsTopmost = true };
        Assert(analyzer.SelectForManualArrange([fixedCover, stack[1]], true).Count == 0,
            "a fixed covering surface shields windows behind it from manual arrange");
        Assert(Manual([Window(0, new(0, 0, 956, 1040)), Window(1, new(964, 0, 956, 1040))]).Moves.Count == 0,
            "already aligned windows stay in place after a manual arrange");
        var offscreen = Window(0, rect with { X = area.Right + 50 });
        Assert(Manual([offscreen]).Moves.Single().TargetVisualRect.Intersect(area).Area == rect.Area &&
            analyzer.SelectForManualArrange([offscreen], false).Count == 0, "off-screen rescue honors the preference");

        // Smaller workspace with two off-screen background windows, as observed on Windows.
        var smallArea = new RectI(0, 0, 1024, 720);
        RectI[] mixedRects = [new(87, 90, 482, 413), new(87, 90, 482, 413), new(36, 104, 1074, 628),
            new(2, 52, 1074, 628), new(318, 319, 406, 185)];
        var mixed = mixedRects.Select((r, i) => Window(i, r) with { WorkArea = smallArea }).ToArray();
        var moves = Manual(mixed).Moves.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        var first = moves.GetValueOrDefault(mixed[0].Handle, mixedRects[0]);
        var second = moves.GetValueOrDefault(mixed[1].Handle, mixedRects[1]);
        Assert(first.Intersect(second).IsEmpty && mixed.Skip(2).Take(2).All(w =>
            moves.TryGetValue(w.Handle, out var dest) && dest.Intersect(smallArea) == dest),
            "one arrange uncovers the front pair and rescues the larger background windows");

        // Removing one of the larger background windows used to leave 78 px of overlap.
        var four = new[] { mixed[0], mixed[1], mixed[3], mixed[4] };
        var fourMoves = Manual(four).Moves.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        var left = fourMoves.GetValueOrDefault(four[0].Handle, four[0].VisualRect);
        var right = fourMoves.GetValueOrDefault(four[1].Handle, four[1].VisualRect);
        Assert(left.Intersect(right).IsEmpty && left.Intersect(smallArea) == left && right.Intersect(smallArea) == right,
            "a fully stacked pair separates on the four-window desktop without pushing either window off screen");
        var smallTopmost = Window(0, new(1600, 820, 260, 200)) with { IsManageable = false, IsTopmost = true };
        var underPair = new[] { smallTopmost, Window(1, new(300, 120, 1200, 760)) with { IsForeground = true },
            Window(2, new(380, 120, 1200, 760)) };
        var fullAssistPlan = LayoutDispatch.Create(LayoutRoute.Smart,
            analyzer.SelectVisibleWorkingSet(underPair, includeStackAccess: true), new(), null, underPair,
            new(UseWorkingPairs: true, Focus: new(2, 3, 3), WindowHints: [new(3, PassiveVisual: true, MediaCompanion: true)]));
        Assert(fullAssistPlan.Moves.Count >= 2 && fullAssistPlan.Groups.Any(g => g.Selected == "working-overlap"),
            "FullAssist applies working-overlap even with a small pinned Topmost widget on screen");
    }

    private static async Task CheckArrangeButtonAsync(MainWindow shell, NeatWin.Windows.WindowManager layout, nint first, nint second)
    {
        var desktop = layout.Capture();
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" &&
            desktop.Any(w => w.IsManageable && w.Handle != first && w.Handle != second))
        {
            Report("SKIP: desktop-wide button test found other applications on the local desktop");
            return;
        }
        var area = desktop.Single(w => w.Handle == first).WorkArea;
        await Task.Run(() =>
        {
            foreach (var hwnd in new[] { first, second })
                if (!Native.SetWindowPos(hwnd, 0, area.X + 80, area.Y + 90, Math.Min(600, area.Width / 2 - 16),
                    Math.Min(420, area.Height - 100), Native.NoActivate))
                    throw new InvalidOperationException("Could not position the overlapping test windows.");
        });
        NeatWin.Core.WindowSnapshot[] Pair() => layout.Capture().Where(w => w.Handle == first || w.Handle == second).ToArray();
        var before = Pair();
        Assert(before.Length == 2 && before.All(w => w.IsManageable) &&
            before[0].VisualRect.Intersect(before[1].VisualRect).Area > .90 * before[1].VisualRect.Area,
            "two native windows start almost completely covered");
        var arrange = (Button)shell.FindName("ArrangeButton");
        var undo = (Button)shell.FindName("UndoArrangeButton");
        var status = (TextBlock)shell.FindName("StatusText");
        var messages = new HashSet<string>();
        arrange.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        try
        {
            await WaitAsync("manual Smart separates real windows through the footer", () =>
            {
                if (status.Text.Length != 0) messages.Add(status.Text);
                var pair = Pair();
                return undo.IsEnabled && pair.Length == 2 && pair[0].VisualRect.Intersect(pair[1].VisualRect).IsEmpty;
            }, 8000);
        }
        catch (TimeoutException ex)
        {
            var observations = layout.Capture().Select(w => $"{w.Handle}: {w.VisualRect} movable={w.IsManageable} z={w.ZOrder}");
            throw new TimeoutException($"{ex.Message}. Work area {area}, targets {first}/{second}, " +
                $"undo={undo.IsEnabled}. {string.Join(" | ", messages)}. {string.Join(" | ", observations)}", ex);
        }
        undo.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await WaitAsync("footer undo restores both overlapping windows", () => !undo.IsEnabled && before.All(w =>
            Pair().Any(a => a.Handle == w.Handle && WindowStateRules.Near(a.VisualRect, w.VisualRect))), 8000);
        Report("PASS: the arrange and undo buttons separate overlapping native windows and restore their positions");
    }

    private static async Task CheckFooterModesAsync(MainWindow shell, StateStore store, string output)
    {
        var group = (Panel)shell.FindName("FooterModes");
        var buttons = group.Children.OfType<RadioButton>().ToArray();
        var footer = (Border)shell.FindName("DesktopFooter");
        var undo = (Button)shell.FindName("UndoArrangeButton");
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(footer).TransformBounds(new Rect(element.RenderSize));
        Assert(buttons.Length == 4 && buttons.All(b => b.IsVisible) && Bounds(group).Right <= Bounds(undo).Left,
            "all four mode choices fit to the left of the desktop commands at minimum width");
        Assert(buttons[0].IsChecked == true && shell.Icon is not null, "the workbench starts in manual mode with its application icon");
        var settingsPath = Path.Combine(store.DirectoryPath, "Layout", "settings.json");
        foreach (var mode in new[] { AutomaticLayoutMode.LightAssist, AutomaticLayoutMode.FullAssist, AutomaticLayoutMode.AutoFullAssist, AutomaticLayoutMode.Off })
        {
            var option = buttons.Single(b => (string)b.Tag == mode.ToString());
            var selection = (ISelectionItemProvider)new RadioButtonAutomationPeer(option).GetPattern(PatternInterface.SelectionItem)!;
            selection.Select();
            await WaitAsync("footer applies and saves " + mode, () => option.IsChecked == true &&
                new NeatWin.App.SettingsStore(settingsPath).LoadAutomaticLayoutMode() == mode);
            Assert(buttons.Count(b => b.IsChecked == true) == 1, "only " + mode + " is selected in the footer");
            if (mode == AutomaticLayoutMode.LightAssist)
            {
                shell.UpdateLayout();
                Capture(shell, Path.Combine(output, "footer-light-assist.png"));
                var reopened = new MainWindow(new StateStore(store.DirectoryPath), testMode: true);
                try
                {
                    await reopened.InitializeAsync();
                    var savedGroup = (Panel)reopened.FindName("FooterModes");
                    Assert(savedGroup.Children.OfType<RadioButton>().Single(b => b.IsChecked == true).Tag.Equals(mode.ToString()),
                        "a fresh workbench restores the saved mode without visiting settings");
                }
                finally { reopened.Cleanup(); reopened.Close(); }
            }
        }
    }

    private static async Task CheckHotkeyDraftAsync(MainWindow shell, StateStore store)
    {
        var pin = (TextBox)shell.FindName("PinKeyBox");
        var show = (TextBox)shell.FindName("ShowKeyBox");
        var apply = (Button)shell.FindName("ApplyKeysButton");
        var originalPin = pin.Text;
        var originalShow = show.Text;
        using var desktop = new DesktopIntegration(shell, shell.Manager);
        // Use an isolated chord to exercise the same WM_HOTKEY route as a binding already owned by WinWindow.
        const string probe = "Ctrl+Shift+F23";
        desktop.ApplyHotkeys(new() { ["pin"] = probe });
        pin.Focus();
        Assert(Native.PostMessage(new WindowInteropHelper(shell).Handle, 0x0312, 1, (nint)((0x86 << 16) | 6)), "registered chord reaches the window");
        await WaitAsync("an already registered chord can be recorded in a focused editor", () => pin.Text == probe);
        pin.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin)!, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Assert(pin.Text == originalPin, "Escape restores the applied shortcut");
        show.Focus(); shell.CaptureRegisteredHotkey(originalPin);
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(store.Preferences.Hotkeys["show"] == originalShow, "a duplicate does not replace the working shortcut map");
        pin.Focus(); shell.CaptureRegisteredHotkey(originalShow);
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var saved = new StateStore(store.DirectoryPath).Preferences.Hotkeys;
        Assert(saved["show"] == originalPin && saved["pin"] == originalShow, "two shortcuts can be swapped and saved together");
        show.Text = originalShow; pin.Text = originalPin;
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static async Task CheckHoverPreviewAsync(MainWindow shell, WindowItem item)
    {
        shell.ShowShell();
        nint own = new WindowInteropHelper(shell).Handle;
        await WaitAsync("workbench has focus before hover preview", () => Native.GetForegroundWindow() == own);
        var row = await FindRowAsync(shell, item);
        long originalStyle = Native.Style(item.Hwnd);
        bool originalTopmost = Native.IsTopmost(item.Hwnd);

        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseEnterEvent });
        await Task.Delay(120);
        Assert(!shell.IsHoverPreviewVisible, "hover preview waits for the 350 ms dwell");
        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseLeaveEvent });
        await Task.Delay(400);
        Assert(!shell.IsHoverPreviewVisible, "leaving before the dwell cancels the preview");

        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseEnterEvent });
        await WaitAsync("hover preview appears after the dwell", () => shell.IsHoverPreviewVisible, 2000);
        nint outlineHwnd = shell.HoverPreviewHandle;
        bool Aligned() => Native.TryGetFrameBounds(item.Hwnd, out var targetRect) &&
            Native.GetWindowRect(outlineHwnd, out var outline) &&
            outline.Left == targetRect.Left - 5 && outline.Top == targetRect.Top - 5 &&
            outline.Right == targetRect.Right + 5 && outline.Bottom == targetRect.Bottom + 5;
        await WaitAsync("hover outline matches the target's screen pixels", Aligned);
        Assert(Native.GetForegroundWindow() == own &&
            (Native.Style(outlineHwnd) & (Native.Transparent | Native.NoActivateStyle)) ==
                (Native.Transparent | Native.NoActivateStyle),
            "hover outline does not take focus or intercept mouse input");
        Assert(Native.Style(item.Hwnd) == originalStyle && Native.IsTopmost(item.Hwnd) == originalTopmost,
            "hover preview does not modify the target window");

        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseLeaveEvent });
        await WaitAsync("leaving the row hides its hover outline", () => !shell.IsHoverPreviewVisible);

        Assert(Native.SetForegroundWindow(item.Hwnd), "fixture can take foreground before inactive-workbench hover");
        await WaitAsync("fixture is foreground while workbench remains visible", () => Native.GetForegroundWindow() == item.Hwnd);
        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseEnterEvent });
        await WaitAsync("hover preview also works over an inactive workbench", () => shell.IsHoverPreviewVisible, 2000);
        Assert(Native.GetForegroundWindow() == item.Hwnd,
            "hover preview never activates the workbench or the outlined window");
        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseLeaveEvent });
        await WaitAsync("inactive-workbench hover outline is dismissed on leave", () => !shell.IsHoverPreviewVisible);
        shell.ShowShell();

        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseEnterEvent });
        await WaitAsync("hover preview is visible before minimizing", () => shell.IsHoverPreviewVisible, 2000);
        shell.WindowState = WindowState.Minimized;
        await WaitAsync("minimizing the workbench immediately hides hover preview", () => !shell.IsHoverPreviewVisible);
        await Task.Delay(400);
        Assert(!shell.IsHoverPreviewVisible, "a pending hover callback cannot reopen the preview while minimized");
        shell.ShowShell();
        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseLeaveEvent });
        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseEnterEvent });
        await WaitAsync("hover preview works again after restoring the workbench", () => shell.IsHoverPreviewVisible, 2000);
        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseLeaveEvent });
        await WaitAsync("restored hover preview closes on leave", () => !shell.IsHoverPreviewVisible);
    }

    private static async Task CheckPickerOutlineAsync(MainWindow shell, nint target)
    {
        shell.ShowShell();
        nint own = new WindowInteropHelper(shell).Handle;
        await WaitAsync("workbench has focus before preview", () => Native.GetForegroundWindow() == own);
        shell.PreviewPickerTarget(target);
        var overlay = Application.Current.Windows.Cast<Window>().Single(w => w != shell && w.IsVisible && w.AllowsTransparency);
        nint hwnd = new WindowInteropHelper(overlay).Handle;
        bool Aligned() => Native.TryGetFrameBounds(target, out var targetRect) && Native.GetWindowRect(hwnd, out var outline)
            && outline.Left == targetRect.Left - 5 && outline.Top == targetRect.Top - 5
            && outline.Right == targetRect.Right + 5 && outline.Bottom == targetRect.Bottom + 5;
        await WaitAsync("outline matches the target's screen pixels", Aligned);
        Assert(Native.GetForegroundWindow() == own && (Native.Style(hwnd) & (Native.Transparent | Native.NoActivateStyle)) == (Native.Transparent | Native.NoActivateStyle),
            "the outline leaves keyboard focus and mouse input with the application");
        if (!Native.GetWindowRect(target, out var original)) throw new InvalidOperationException("Fixture has no window bounds.");
        try
        {
            await Task.Run(() => Native.SetWindowPos(target, 0, original.Left + 34, original.Top + 18, 0, 0, Native.NoSize | Native.NoActivate | 4));
            await WaitAsync("target moves away from the old outline", () => !Aligned());
            shell.PreviewPickerTarget(target);
            await WaitAsync("preview follows the same window at its new position", Aligned);
            shell.PreviewPickerTarget(0);
            Assert(!Native.IsWindowVisible(hwnd), "leaving a target hides the outline");
        }
        finally
        {
            shell.PreviewPickerTarget(0);
            await Task.Run(() => Native.SetWindowPos(target, 0, original.Left, original.Top, 0, 0, Native.NoSize | Native.NoActivate | 4));
        }
    }

    private static async Task CheckMixedSessionRestoreAsync(nint hwnd, string output)
    {
        // Hosted Windows runners may have no render endpoint. Drive the same
        // manager operations with observable per-session values in that case too.
        var sessions = new Dictionary<string, bool> { ["music"] = true, ["voice"] = false };
        var store = ChildStore(output, false);
        WindowManager NewManager() => new WindowManager(store,
            identity => Native.MatchesProcess(identity) ? new Dictionary<string, bool>(sessions) : new(),
            (_, saved, mute) =>
            {
                foreach (var (id, original) in saved)
                    if (sessions.ContainsKey(id)) sessions[id] = mute ?? original;
            });
        var manager = NewManager();
        var item = manager.SelectTarget(hwnd);
        await manager.SetMuteAsync(item, true, updateRule: false);
        Assert(sessions.Values.All(muted => muted), "modeled sessions both muted through the manager");
        sessions["late"] = false;
        await manager.RefreshAsync();
        Assert(sessions["late"], "a new audio session joins the existing mute");
        await manager.SetMuteAsync(item, false, updateRule: false);
        Assert(sessions["music"] && !sessions["voice"] && !sessions["late"],
            "mute off restores the independent baseline of each modeled session");
        sessions["music"] = false;
        sessions["voice"] = true;
        await manager.SetMuteAsync(item, true, updateRule: false);
        sessions["voice"] = false; // Already muted before the workbench took control.
        sessions["music"] = false; // Changed by the workbench, then by the player.
        await manager.RefreshAsync();
        Assert(!item.Muted && !sessions["music"] && !sessions["voice"] && sessions["late"],
            "refresh respects mixer changes and leaves other sessions alone");
        sessions["music"] = true;
        await manager.ShutdownAsync();
        Assert(sessions["music"] && !sessions["voice"] && !sessions["late"],
            "exit preserves subsequent mixer choices and restores only the remaining owned session");

        var launcher = await FixtureAsync(output, false);
        using var launcherProcess = launcher.Process;
        try
        {
            manager = NewManager();
            var target = manager.SelectTarget(launcher.Hwnd);
            await manager.SetMuteAsync(target, true, updateRule: false);
            var journal = store.ReadRecovery();
            Stop(launcherProcess);
            await launcherProcess.WaitForExitAsync();
            await manager.RefreshAsync();
            await WaitAsync("launcher exit restores surviving modeled sessions", () => store.ReadRecovery().Count == 0);
            Assert(sessions["music"] && !sessions["voice"] && !sessions["late"],
                "closing a launcher restores only its captured session instances");

            sessions["voice"] = sessions["late"] = true;
            sessions["unrelated"] = true;
            store.WriteRecovery(journal);
            var restarted = NewManager();
            await restarted.RecoverPreviousSessionAsync();
            await WaitAsync("restart restores sessions after their launcher has exited", () => store.ReadRecovery().Count == 0);
            Assert(!sessions["voice"] && !sessions["late"] && sessions["unrelated"], "unrelated audio remains untouched");
        }
        finally { Stop(launcherProcess); }
    }

    private static async Task CheckGameRecoveryAsync(string output, List<Process> processes)
    {
        string? fixturePath = Environment.GetEnvironmentVariable("WINWINDOW_RECOVERY_FIXTURE");
        if (fixturePath is null)
        {
            fixturePath = Path.GetFullPath("artifacts/native-x64/Release/AudioFixture.exe");
            if (!File.Exists(fixturePath)) { Report("SKIP: build the native recovery fixture for the exit and audio checks"); return; }
        }
        var game = Process.Start(new ProcessStartInfo(fixturePath) { UseShellExecute = false })!;
        processes.Add(game);
        nint hwnd = 0;
        await WaitAsync("recovery fixture publishes its real window", () =>
        {
            if (game.HasExited) throw new InvalidOperationException($"Recovery fixture exited with {game.ExitCode}");
            game.Refresh();
            hwnd = game.MainWindowHandle;
            return hwnd != 0 && (Native.GetProp(hwnd, "WinWindow.Test.Mix") == 2 ||
                Native.GetProp(hwnd, "WinWindow.Test.NoEndpoint") != 0);
        }, 6000);
        await CheckMixedSessionRestoreAsync(hwnd, output);
        bool audio = Native.GetProp(hwnd, "WinWindow.Test.Mix") == 2;
        if (!audio) Report("SKIP: no Windows render endpoint for the two real WASAPI sessions");

        var store = ChildStore(output, false);
        var manager = new WindowManager(store);
        var item = manager.SelectTarget(hwnd);
        manager.AddToList(item);
        await manager.SetMuteAsync(item, true, updateRule: false);
        if (audio) await WaitAsync("both audio sessions muted", () => Native.GetProp(hwnd, "WinWindow.Test.Mix") == 4);
        await manager.SetMuteAsync(item, false, updateRule: false);
        if (audio) await WaitAsync("unmute restores silent music and audible voice", () => Native.GetProp(hwnd, "WinWindow.Test.Mix") == 2);
        Assert(item.Original?.OriginalAudioMutes is null, "unmute releases the saved audio baseline");
        if (audio)
        {
            var identity = item.Identity!;
            var voice = Native.GetProcessMutes(identity).Where(pair => !pair.Value).ToDictionary();
            Native.SetProcessMutes(identity, voice, true);
            await WaitAsync("recorded voice session muted", () => Native.GetProp(hwnd, "WinWindow.Test.Mix") == 4);
            // The original identity can no longer be used to enumerate a process tree.
            Native.SetProcessMutes(identity with { ProcessStarted = 0 }, voice);
            await WaitAsync("WASAPI undo by session instance no longer needs the launcher", () =>
                Native.GetProp(hwnd, "WinWindow.Test.Mix") == 2);
        }

        bool nativeKeep = Native.HasNoFocusLossSupport();
        store.Preferences.EnhancedBackgroundKeep = true;
        if (nativeKeep)
        {
            string nativeDirectory = Path.Combine(store.DirectoryPath, "Native");
            File.WriteAllText(nativeDirectory, "fixture blocks component extraction");
            try
            {
                await manager.SetBackgroundEnabledAsync(item, true, updateRule: false);
                throw new InvalidOperationException("Expected component preparation to fail");
            }
            catch (IOException) { }
            File.Delete(nativeDirectory);
            Assert(!item.BackgroundEnabled && item.Original?.RestoreActivation == false &&
                !Native.HasLoadedNoFocusLossModule(game.Id), "preparation failure leaves no new cleanup obligation");
        }
        await manager.RemoveAsync(item);
        Assert(!manager.Items.Contains(item) && store.ReadRecovery().Count == 0, "failed target can be removed normally");

        var rule = Rule(hwnd);
        rule.KeepRunning = true;
        rule.Muted = true;
        store.Preferences.Rules.Add(rule);
        store.SavePreferences();
        var app = await StartMainAsync(store, processes);
        await WaitAsync("running workbench enabled background keep, pin and mute", () =>
            store.ReadRecovery().Any(s => s.Identity.Hwnd == hwnd && s.OriginalAudioMutes is not null && s.RestoreActivation) &&
            Native.IsTopmost(hwnd) && (!nativeKeep || Native.IsNoFocusLossInjected(hwnd)) &&
            (!audio || Native.GetProp(hwnd, "WinWindow.Test.Mix") == 4), 6000);
        // A real Quit, without first disabling the options or removing the game.
        Native.PostMessage(app.Hwnd, 0x10, 0, 0);
        await WaitAsync("workbench exited with the game still managed", () => app.Process.HasExited);
        Assert(app.Process.ExitCode == 0 && !game.HasExited && !Native.IsTopmost(hwnd) &&
            !Native.HasLoadedNoFocusLossModule(game.Id) && store.ReadRecovery().Count == 0,
            "clean exit releases the live game and finishes its recovery journal");
        if (audio) await WaitAsync("game independently observes original mix after exit", () => Native.GetProp(hwnd, "WinWindow.Test.Mix") == 2);

        if (!nativeKeep) return;
        item = manager.SelectTarget(hwnd);
        await manager.SetBackgroundEnabledAsync(item, true, updateRule: false);
        await manager.SetMuteAsync(item, true, updateRule: false);
        Native.PostMessage(hwnd, 0x8000 + 91, 0, 0);
        await WaitAsync("game is temporarily unresponsive", () => Native.GetProp(hwnd, "WinWindow.Test.Stalled") != 0);
        await manager.RemoveAsync(item);
        var pending = store.ReadRecovery().Single(s => s.Identity.Hwnd == hwnd);
        Assert(!item.IsManaged && pending.RemovedPendingBackground && pending.RestoreActivation &&
            pending.OriginalAudioMutes is null && !pending.EditedTopmost, "removal keeps only unresolved background cleanup");
        if (audio) await WaitAsync("other settings recover despite background timeout", () => Native.GetProp(hwnd, "WinWindow.Test.Mix") == 2, 6000);
        Stop(game);
        await game.WaitForExitAsync();
        await manager.RefreshAsync();
        Assert(!store.ReadRecovery().Any(s => s.Identity.Hwnd == hwnd), "pending cleanup expires when the original game exits");
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
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".release");
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
            // The previous session must restore the original, then the saved pin applies again.
            // An unpinned window between these operations is transient and may never reach the test thread.
            await WaitAsync("saved pins reapplied after recovery", () =>
                Native.IsTopmost(stuck.Hwnd) && Native.IsTopmost(healthy.Hwnd) &&
                store.ReadRecovery().Any(s => s.Identity.Hwnd == stuck.Hwnd && !s.Topmost && s.EditedTopmost));
            Native.PostMessage(recovered.Hwnd, 0x10, 0, 0);
            await WaitAsync("recovered app exits", () => recovered.Process.HasExited);
            Assert(!Native.IsTopmost(stuck.Hwnd) && !Native.IsTopmost(healthy.Hwnd) && store.ReadRecovery().Count == 0,
                "recovering a blocked target preserves its original state through the next pin and clean exit");
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
