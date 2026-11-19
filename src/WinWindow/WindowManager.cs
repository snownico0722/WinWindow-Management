using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinWindow;

internal sealed class WindowManager
{
    private readonly StateStore _store;
    private readonly Dictionary<WindowItem, Task> _operations = new();
    private readonly HashSet<int> _backgroundOperations = new();
    private bool _refreshing, _rulesActive;
    private volatile bool _stopping;
    private Task<int>? _shutdown;
    public ObservableCollection<WindowItem> Items { get; } = new();
    public event Action<string>? Notice;
    public event Action? Changed;
    public bool HasRules => _store.Preferences.Rules.Count != 0;
    public WindowManager(StateStore store) => _store = store;
    private string NativeDirectory => Path.GetFullPath(Path.Combine(_store.DirectoryPath, "Native"));
    private static bool Same(WindowIdentity? a, WindowIdentity? b) => a is not null && b is not null && a.Handle == b.Handle && a.ProcessId == b.ProcessId && a.ProcessStarted == b.ProcessStarted && a.ClassName == b.ClassName;
    // State and journal stay on the dispatcher; foreign HWND work does not.
    private async Task OperateAsync(WindowItem item, Func<Task> action)
    {
        if (_stopping) return;
        EnsureIdle(item);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Add(item, completion.Task);
        item.IsBusy = true;
        try { await action(); }
        finally
        {
            item.IsBusy = false; _operations.Remove(item); completion.TrySetResult();
            if (!_stopping) Changed?.Invoke();
        }
    }
    public async Task RecoverPreviousSessionAsync()
    {
        foreach (var snapshot in _store.ReadRecovery())
        {
            if (!Native.Matches(snapshot.Identity) &&
                !((snapshot.RestoreActivation || snapshot.OriginalAudioMutes is not null) && Native.MatchesProcess(snapshot.Identity))) continue;
            var item = new WindowItem { Identity = snapshot.Identity, Title = snapshot.Title, Original = snapshot, Listed = true, IsHidden = !Native.IsWindowVisible(snapshot.Identity.Hwnd), IsMinimized = Native.IsIconic(snapshot.Identity.Hwnd) };
            Items.Add(item);
        }
        Journal();
        foreach (var item in Items.ToArray()) _ = RecoverOneAsync(item);
        await Task.CompletedTask;
    }
    private async Task RecoverOneAsync(WindowItem item)
    {
        try
        {
            await ReleaseAsync(item, updateRule: false);
            // Recovery can outlast the initial enumeration. Retry rules for the now idle window.
            if (_rulesActive && !_stopping) ApplyRules();
        }
        catch (Exception e) { if (!_stopping) Notice?.Invoke(UiText.Format("RestoreError", item.Title, e.Message)); }
    }
    public async Task RefreshAsync()
    {
        if (_refreshing || _stopping) return;
        _refreshing = true;
        try
        {
            var found = await Task.Run(Native.Enumerate);
            if (_stopping) return;
            bool removed = false;
            foreach (var item in Items.Where(x => x.Identity is not null).ToArray())
            {
                if (item.IsBusy) continue;
                if (!Native.Matches(item.Identity!))
                {
                    if ((item.Original?.RestoreActivation == true || item.Original?.OriginalAudioMutes is not null) &&
                        Native.MatchesProcess(item.Identity!))
                    {
                        _ = RecoverOneAsync(item);
                        continue;
                    }
                    removed |= item.Original is not null; Items.Remove(item); continue;
                }
                var live = found.FirstOrDefault(x => Same(x.Identity, item.Identity));
                if (live is null && !item.IsManaged) { Items.Remove(item); continue; }
                item.Title = live?.Title ?? Native.Title(item.Hwnd);
                item.IsMinimized = Native.IsIconic(item.Hwnd); item.IsHidden = !Native.IsWindowVisible(item.Hwnd);
                item.Pinned = Native.IsTopmost(item.Hwnd);
                item.ClickThrough = (Native.Style(item.Hwnd) & Native.Transparent) != 0;
                if (Native.GetLayeredWindowAttributes(item.Hwnd, out _, out byte alpha, out uint flags) && (flags & 2) != 0) item.Opacity = Native.ToPercent(alpha);
                else if ((Native.Style(item.Hwnd) & Native.Layered) == 0) item.Opacity = 100;
            }
            foreach (var item in found) if (!Items.Any(x => Same(x.Identity, item.Identity))) Items.Add(item);
            await RefreshAudioMutesAsync();
            if (_stopping) return;
            if (removed) Journal();
            _rulesActive = true;
            ApplyRules(); Changed?.Invoke();
        }
        finally { _refreshing = false; }
    }
    private void ApplyRules()
    {
        foreach (var rule in _store.Preferences.Rules.ToArray())
        {
            // Keep a live binding through title changes. Each saved rule may claim one unbound HWND.
            if (Items.Any(x => x.Identity is not null && x.RuleId == rule.Id)) continue;
            var placeholder = Items.FirstOrDefault(x => x.Identity is null && x.RuleId == rule.Id);
            var item = Items.FirstOrDefault(x => x.IsAvailable && !x.IsBusy && x.AutoRemember &&
                x.RuleId is null && rule.Matches(x));
            if (item is not null)
            {
                if (placeholder is not null) Items.Remove(placeholder);
                item.RuleId = rule.Id;
                _ = ApplyRuleAsync(item, rule);
            }
            else if (placeholder is null)
                Items.Add(new() { Title = rule.Title, RuleId = rule.Id, IsAvailable = false, Pinned = rule.Pinned, Opacity = rule.Opacity, ClickThrough = rule.ClickThrough, Muted = rule.Muted, BackgroundEnabled = rule.KeepRunning });
        }
        foreach (var pending in Items.Where(x => x.Identity is null && !_store.Preferences.Rules.Any(r => r.Id == x.RuleId)).ToArray()) Items.Remove(pending);
    }
    public Task ReinforcePinnedAsync()
    {
        var missing = Items.Where(x => x.WantsPin && !x.IsBusy && !Native.IsTopmost(x.Hwnd)).ToArray();
        return Task.WhenAll(missing.Select(async item =>
        {
            try
            {
                await SetPinAsync(item, true, updateRule: false);
            }
            catch (Exception e)
            {
                item.WantsPin = false;
                if (!_stopping) Notice?.Invoke(UiText.Format("ReinforceError", item.Title, e.Message));
            }
        }));
    }

    private async Task ApplyRuleAsync(WindowItem item, WindowRule rule)
    {
        try
        {
            await OperateAsync(item, async () =>
            {
                bool pin = rule.Pinned, through = rule.ClickThrough;
                int opacity = Math.Clamp(rule.Opacity, 0, 100);
                Track(item, s =>
                {
                    s.EditedTopmost = true;
                    s.EditedOpacity |= opacity != item.Opacity;
                    s.EditedClickThrough |= through != item.ClickThrough;
                });
                var original = item.Original!;
                await Task.Run(() =>
                {
                    Native.EnsureIdentity(original.Identity);
                    if (Native.IsTopmost(item.Hwnd) != pin) Native.SetTopmost(item.Hwnd, pin);
                    if (!_stopping && opacity != item.Opacity) Native.SetOpacity(original, opacity);
                    if (!_stopping && through != item.ClickThrough) Native.SetClickThrough(original, through, opacity);
                });
                if (_stopping) return;
                item.WantsPin = pin;
                item.Pinned = Native.IsTopmost(item.Hwnd); item.Opacity = opacity; item.ClickThrough = through;
                item.NotifyState();
            });
            if (rule.Muted) await SetMuteAsync(item, true, updateRule: false);
            if (rule.KeepRunning) await SetBackgroundEnabledAsync(item, true, updateRule: false);
        }
        catch (Exception e) { if (!_stopping) Notice?.Invoke(UiText.Format("RuleError", item.Title, e.Message)); }
    }
    public WindowItem SelectTarget(nint hwnd)
    {
        hwnd = Native.GetAncestor(hwnd, 2);
        if (!Native.IsTarget(hwnd)) throw new InvalidOperationException(UiText.Get("ChooseOther"));
        var id = Native.Identity(hwnd) ?? throw new InvalidOperationException(UiText.Get("IdentityUnavailable"));
        var existing = Items.FirstOrDefault(x => Same(x.Identity, id));
        if (existing is not null) return existing;
        long style = Native.Style(hwnd);
        int opacity = (style & Native.Layered) != 0 && Native.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) && (flags & 2) != 0 ? Native.ToPercent(alpha) : 100;
        var item = new WindowItem { Identity = id, Title = Native.Title(hwnd), Pinned = Native.IsTopmost(hwnd), IsMinimized = Native.IsIconic(hwnd), ClickThrough = (style & Native.Transparent) != 0, Opacity = opacity };
        Items.Add(item); Changed?.Invoke(); return item;
    }
    private static void EnsureIdle(WindowItem item) { if (item.IsBusy) throw new InvalidOperationException(UiText.Get("WindowBusy")); }
    private WindowSnapshot Manage(WindowItem item)
    {
        if (item.Identity is null) throw new InvalidOperationException(UiText.Get("WindowPending"));
        Native.EnsureIdentity(item.Identity);
        item.Original ??= Native.Capture(item.Identity); item.Listed = true;
        item.NotifyState(); return item.Original;
    }
    private void Track(WindowItem item, Action<WindowSnapshot> edit)
    {
        var s = Manage(item);
        var previous = (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.OriginalAudioMutes, s.RestoreActivation, s.InjectedBackground, s.Parked, s.BeforeParking);
        edit(s);
        var next = (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.OriginalAudioMutes, s.RestoreActivation, s.InjectedBackground, s.Parked, s.BeforeParking);
        if (previous.Equals(next)) return;
        try { Journal(); }
        catch { (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.OriginalAudioMutes, s.RestoreActivation, s.InjectedBackground, s.Parked, s.BeforeParking) = previous; throw; }
    }
    private void Journal() => _store.WriteRecovery(Items.Where(x => x.Original is not null).Select(x => x.Original!));
    private void Finish(WindowItem item, bool updateRule)
    { item.NotifyState(); if (!_stopping && updateRule) UpdateRule(item); }
    public void AddToList(WindowItem item)
    {
        if (_stopping || item.Identity is null) return;
        Native.EnsureIdentity(item.Identity);
        item.Listed = true;
        item.NotifyState();
        Changed?.Invoke();
    }

    public async Task SetBackgroundEnabledAsync(WindowItem item, bool enabled, bool updateRule = true)
    {
        if (_stopping || item.BackgroundEnabled == enabled) return;
        EnsureIdle(item);
        var identity = item.Identity ?? throw new InvalidOperationException(UiText.Get("WindowPending"));
        if (!_backgroundOperations.Add(identity.ProcessId)) throw new InvalidOperationException(UiText.Get("WindowBusy"));
        try
        {
            if (enabled)
            {
                foreach (var other in Items.Where(x => x != item && x.Original?.RestoreActivation == true &&
                    x.Identity?.ProcessId == identity.ProcessId).ToArray())
                    await ChangeBackgroundAsync(other, false, updateRule);
            }
            await ChangeBackgroundAsync(item, enabled, updateRule);
        }
        finally { _backgroundOperations.Remove(identity.ProcessId); }
    }

    private Task ChangeBackgroundAsync(WindowItem item, bool enabled, bool updateRule) =>
        OperateAsync(item, async () =>
        {
            if (_stopping) return;
            bool enhanced = _store.Preferences.EnhancedBackgroundKeep;
            bool lockCursor = _store.Preferences.LockBackgroundCursorPos;
            bool wasInjected = item.Original is { RestoreActivation: true } previous &&
                previous.InjectedBackground != false;
            if (enabled)
                Track(item, s => { s.RestoreActivation = true; s.InjectedBackground = wasInjected || enhanced; });
            var identity = item.Identity!;
            bool antiCheatSkipped = false;
            bool injected = await Task.Run(() => Native.SetBackgroundActivity(
                identity, enabled, enhanced, lockCursor, wasInjected, NativeDirectory, out antiCheatSkipped));
            item.BackgroundEnabled = enabled;
            if (item.Original is { } snapshot)
            {
                snapshot.RestoreActivation = enabled;
                snapshot.InjectedBackground = enabled && injected;
                Journal();
            }
            if (antiCheatSkipped && !_stopping)
                Notice?.Invoke(UiText.Format("BackgroundAntiCheatFallback", item.Title));
            Finish(item, updateRule);
        });

    public async Task ApplyBackgroundPreferencesAsync()
    {
        if (_stopping) return;
        foreach (var item in Items.Where(x => x.Identity is not null && (x.BackgroundEnabled || x.IsBusy)).ToArray())
        {
            if (_operations.TryGetValue(item, out var pending)) await pending;
            if (_stopping || !item.BackgroundEnabled || item.Identity is not { } identity ||
                !_backgroundOperations.Add(identity.ProcessId)) continue;
            try { await ChangeBackgroundAsync(item, true, updateRule: false); }
            catch (Exception e) { if (!_stopping) Notice?.Invoke(UiText.Format("BackgroundStopped", item.Title, e.Message)); }
            finally { _backgroundOperations.Remove(identity.ProcessId); }
        }
    }

    private IEnumerable<WindowItem> AudioRows(WindowIdentity identity) => Items.Where(x =>
        x.Identity is { } id && id.ProcessId == identity.ProcessId && id.ProcessStarted == identity.ProcessStarted);

    private async Task RefreshAudioMutesAsync()
    {
        var targets = Items.Where(x => x.IsManaged && x.Identity is not null)
            .Select(x => x.Identity!).DistinctBy(id => (id.ProcessId, id.ProcessStarted)).ToArray();
        foreach (var identity in targets)
        {
            if (_stopping) return;
            try
            {
                var current = await Task.Run(() => Native.GetProcessMutes(identity));
                if (_stopping) return;
                var owner = AudioRows(identity).FirstOrDefault(x => x.Original?.OriginalAudioMutes is not null);
                if (owner is null)
                {
                    foreach (var row in AudioRows(identity))
                        row.Muted = current.Count != 0 && current.Values.All(x => x);
                }
                else if (owner.Muted && !owner.IsBusy && current.Values.Any(x => !x))
                    await OperateAsync(owner, () => ApplyMuteAsync(owner, current, true));
            }
            catch (Exception e) { if (!_stopping) Notice?.Invoke(e.Message); }
        }
    }

    public Task SetMuteAsync(WindowItem item, bool value, bool updateRule = true)
    {
        EnsureIdle(item);
        var identity = item.Identity ?? throw new InvalidOperationException(UiText.Get("WindowPending"));
        var owner = AudioRows(identity).FirstOrDefault(x => x.Original?.OriginalAudioMutes is not null) ?? item;
        return OperateAsync(owner, async () =>
        {
            Native.EnsureIdentity(identity);
            Track(owner, s => s.OriginalAudioMutes ??= new(StringComparer.Ordinal));
            var current = await Task.Run(() => Native.GetProcessMutes(identity));
            if (_stopping) return;
            await ApplyMuteAsync(owner, current, value);
            if (updateRule)
            {
                if (value) AutoRemember(owner);
                foreach (var row in AudioRows(identity)) Finish(row, true);
            }
        });
    }

    private async Task ApplyMuteAsync(WindowItem owner, Dictionary<string, bool> current, bool value)
    {
        var snapshot = owner.Original!;
        var baseline = new Dictionary<string, bool>(snapshot.OriginalAudioMutes!, StringComparer.Ordinal);
        foreach (var (id, muted) in current) baseline.TryAdd(id, muted);
        if (baseline.Count != snapshot.OriginalAudioMutes!.Count) Track(owner, s => s.OriginalAudioMutes = baseline);
        await Task.Run(() => Native.SetProcessMutes(snapshot.Identity, current, value));
        foreach (var row in AudioRows(snapshot.Identity)) row.Muted = value;
    }

    internal void OnApplicationDeactivated(uint processId)
    {
        if (_stopping) return;
        foreach (var item in Items.Where(i => i.BackgroundEnabled && i.Identity?.ProcessId == processId).ToArray())
        {
            if (Native.IsNoFocusLossInjected(item.Hwnd)) continue;
            try { Native.PostAppActivation(item.Identity!, true, throwOnError: true); }
            catch (Exception e)
            {
                item.BackgroundEnabled = false;
                Notice?.Invoke(UiText.Format("BackgroundStopped", item.Title, e.Message));
            }
        }
    }

    public Task SetPinAsync(WindowItem item, bool value, bool updateRule = true) => OperateAsync(item, async () =>
    {
        Track(item, s => s.EditedTopmost = true);
        await Task.Run(() => { Native.EnsureIdentity(item.Identity!); Native.SetTopmost(item.Hwnd, value); });
        item.Pinned = Native.IsTopmost(item.Hwnd);
        if (item.Pinned != value) throw new InvalidOperationException(UiText.Get("PinRejected"));
        item.WantsPin = value;
        if (updateRule && value) AutoRemember(item);
        Finish(item, updateRule);
    });
    public Task SetOpacityAsync(WindowItem item, int value, bool updateRule = true) => OperateAsync(item, async () =>
    {
        value = Math.Clamp(value, 0, 100); Track(item, s => s.EditedOpacity = true);
        var original = item.Original!;
        await Task.Run(() => { Native.EnsureIdentity(original.Identity); Native.SetOpacity(original, value); });
        item.Opacity = value;
        if (updateRule && value < 100) AutoRemember(item);
        Finish(item, updateRule);
    });
    public Task SetClickThroughAsync(WindowItem item, bool value, bool updateRule = true) => OperateAsync(item, async () =>
    {
        Track(item, s => s.EditedClickThrough = true);
        var original = item.Original!; int opacity = item.Opacity;
        await Task.Run(() => { Native.EnsureIdentity(original.Identity); Native.SetClickThrough(original, value, opacity); });
        item.ClickThrough = (Native.Style(item.Hwnd) & Native.Transparent) != 0;
        if (item.ClickThrough != value) throw new InvalidOperationException(UiText.Get("ThroughRejected"));
        if (updateRule && value) AutoRemember(item);
        Finish(item, updateRule);
    });
    public Task SetListedPinAsync(WindowItem item, bool value)
    {
        if (item.Identity is not null) return SetPinAsync(item, value);
        if (_stopping) return Task.CompletedTask;
        var rule = _store.Preferences.Rules.FirstOrDefault(r => r.Id == item.RuleId);
        if (rule is not null) { rule.Pinned = item.Pinned = value; _store.SaveSoon(); Changed?.Invoke(); }
        return Task.CompletedTask;
    }
    public async Task UnpinAllAsync()
    {
        var attempts = Items.Where(x => (x.IsManaged || x.Remembered) && (x.Pinned || x.WantsPin)).ToArray().Select(async item =>
        {
            try { await SetListedPinAsync(item, false); return 0; }
            catch (Exception) { return 1; }
        });
        int failures = (await Task.WhenAll(attempts)).Sum();
        if (failures != 0) Notice?.Invoke(UiText.Format("UnpinFailed", failures));
    }
    public Task ActivateAsync(WindowItem item)
    {
        if (item.Identity is null) return Task.CompletedTask;
        // Covered/maximized targets only move forward, not back to normal size.
        if (Native.IsIconic(item.Hwnd) || !Native.IsWindowVisible(item.Hwnd)) return RestoreParkingAsync(item);
        return OperateAsync(item, () => Task.Run(() =>
        { Native.EnsureIdentity(item.Identity); Native.SetForegroundWindow(item.Hwnd); }));
    }
    public Task RemoveAsync(WindowItem item) => OperateAsync(item, async () =>
    {
        if (item.Original is not null) await RestoreSnapshotAsync(item.Original, true);
        else if (item.Identity is not null && Native.Matches(item.Identity))
        {
            Track(item, s => s.EditedTopmost = true);
            await Task.Run(() => { Native.EnsureIdentity(item.Identity); Native.SetTopmost(item.Hwnd, false); });
        }
        item.BackgroundEnabled = false;
        // X unpins and removes. It never sends WM_CLOSE or restores TOPMOST=true.
        _store.Preferences.Rules.RemoveAll(r => r.Id == item.RuleId);
        item.RuleId = null; item.Original = null; item.Listed = false;
        Items.Remove(item); Journal(); _store.SaveSoon();
    });
    public void SetRemembered(WindowItem item, bool remember)
    {
        if (_stopping) return;
        item.AutoRemember = remember;
        if (!remember)
        {
            _store.Preferences.Rules.RemoveAll(x => x.Id == item.RuleId);
            item.RuleId = null;
            if (item.Identity is null) Items.Remove(item);
        }
        else
        {
            EnsureIdle(item); Manage(item);
            if (item.RuleId is not null) return;
            AddRule(item);
        }
        _store.SaveSoon(); Finish(item, false); Changed?.Invoke();
    }
    private void AutoRemember(WindowItem item)
    {
        if (item.AutoRemember && item.RuleId is null) AddRule(item);
    }

    private void AddRule(WindowItem item)
    {
        var rule = new WindowRule { ProcessPath = item.ProcessPath, ClassName = item.Identity!.ClassName,
            Title = item.Title, Pinned = item.Pinned, Opacity = item.Opacity, ClickThrough = item.ClickThrough,
            Muted = item.Muted, KeepRunning = item.BackgroundEnabled };
        _store.Preferences.Rules.Add(rule);
        item.RuleId = rule.Id;
    }

    private void UpdateRule(WindowItem item)
    {
        var rule = _store.Preferences.Rules.FirstOrDefault(x => x.Id == item.RuleId);
        if (rule is null) return;
        rule.Pinned = item.Pinned; rule.Opacity = item.Opacity; rule.ClickThrough = item.ClickThrough;
        rule.Muted = item.Muted; rule.KeepRunning = item.BackgroundEnabled;
        _store.SaveSoon();
    }
    private static async Task<bool> WaitForAsync(WindowIdentity id, Func<nint, bool> predicate)
    {
        int stable = 0;
        for (int i = 0; i < 8; i++)
        {
            if (!Native.Matches(id)) throw new InvalidOperationException(UiText.Get("ClosedDuringOperation"));
            stable = predicate(id.Hwnd) ? stable + 1 : 0;
            if (stable >= 2) return true;
            await Task.Delay(60);
        }
        return false;
    }
    public async Task<string> ParkAsync(WindowItem item)
    {
        string result = "";
        await OperateAsync(item, async () =>
        {
            var snapshot = Manage(item);
            if (Native.IsIconic(item.Hwnd) || !Native.IsWindowVisible(item.Hwnd)) { result = UiText.Get("AlreadyParked"); return; }
            var placement = Native.GetPlacement(item.Hwnd);
            Track(item, s => { s.BeforeParking = placement; s.Parked = true; });
            Native.ShowWindowAsync(item.Hwnd, Native.ForceMinimize);
            bool minimized = await WaitForAsync(snapshot.Identity, Native.IsIconic);
            if (!minimized && !_stopping)
            {
                var fallback = placement; fallback.ShowCmd = 7; Native.ApplyPlacement(item.Hwnd, fallback);
                minimized = await WaitForAsync(snapshot.Identity, Native.IsIconic);
            }
            if (!minimized && !_stopping)
            {
                Native.ShowWindowAsync(item.Hwnd, Native.Hide);
                if (!await WaitForAsync(snapshot.Identity, h => !Native.IsWindowVisible(h) || Native.IsIconic(h)))
                { result = UiText.Get("ParkRejected"); return; }
            }
            item.IsMinimized = Native.IsIconic(item.Hwnd); item.IsHidden = !Native.IsWindowVisible(item.Hwnd);
            result = item.IsHidden ? UiText.Get("HiddenRestoreList") : UiText.Get("MinimizedStatus"); Finish(item, false);
        });
        return result;
    }
    private async Task RestoreSnapshotAsync(WindowSnapshot s, bool unpin = false)
    {
        if (s.RestoreActivation)
            await Task.Run(() => Native.SetBackgroundActivity(
                s.Identity, false, false, false, s.InjectedBackground != false, NativeDirectory, out _));
        if (s.OriginalAudioMutes is { } baseline)
        {
            await Task.Run(() => Native.SetProcessMutes(s.Identity, baseline));
            foreach (var row in AudioRows(s.Identity))
                row.Muted = baseline.Count != 0 && baseline.Values.All(x => x);
        }
        if (!Native.Matches(s.Identity)) return;
        if (s.Parked)
        {
            Native.ApplyPlacement(s.Identity.Hwnd, s.BeforeParking);
            Native.ShowWindowAsync(s.Identity.Hwnd, (int)s.BeforeParking.ShowCmd);
            if (!await WaitForAsync(s.Identity, h => Native.IsWindowVisible(h) && !Native.IsIconic(h)))
                throw new InvalidOperationException(UiText.Get("RestorePending"));
        }
        await Task.Run(() => { if (Native.Matches(s.Identity)) Native.RestoreStyles(s, unpin); });
    }
    public Task RestoreParkingAsync(WindowItem item, bool activate = true) => OperateAsync(item, async () =>
    {
        var s = Manage(item);
        if (s.Parked) Native.ApplyPlacement(item.Hwnd, s.BeforeParking);
        Native.ShowWindowAsync(item.Hwnd, s.Parked ? (int)s.BeforeParking.ShowCmd : Native.Restore);
        if (!await WaitForAsync(s.Identity, h => Native.IsWindowVisible(h) && !Native.IsIconic(h))) throw new InvalidOperationException(UiText.Get("RestoreRejected"));
        s.Parked = false; item.IsHidden = false; item.IsMinimized = false; Journal();
        if (activate && !_stopping) Native.SetForegroundWindow(item.Hwnd);
        Finish(item, false);
    });
    public Task ReleaseAsync(WindowItem item, bool forgetRule = false, bool updateRule = true) => OperateAsync(item, async () =>
    {
        if (item.Original is { } s)
        {
            await RestoreSnapshotAsync(s);
            item.Pinned = s.Topmost;
            item.Opacity = s.LayeredReadable && (s.LayeredFlags & 2) != 0 ? Native.ToPercent(s.Alpha) : 100;
            item.ClickThrough = (s.ExtendedStyle & Native.Transparent) != 0;
            item.IsHidden = false; item.IsMinimized = Native.IsIconic(item.Hwnd);
            item.Original = null; Journal();
        }
        item.BackgroundEnabled = false;
        item.WantsPin = false;
        if (forgetRule) SetRemembered(item, false);
        else if (!_stopping && updateRule) UpdateRule(item);
        Finish(item, false);
    });
    public async Task<int> RestoreAllAsync(bool updateRules = true)
    {
        var attempts = Items.Where(x => x.IsManaged).ToArray().Select(async item =>
        {
            try { await ReleaseAsync(item, false, updateRules); return 0; }
            catch (Exception e) { if (!_stopping) Notice?.Invoke(UiText.Format("RestoreError", item.Title, e.Message)); return 1; }
        });
        return (await Task.WhenAll(attempts)).Sum();
    }
    public Task<int> ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();
    private async Task<int> ShutdownCoreAsync()
    {
        _stopping = true;
        var attempts = Items.Where(x => x.Original is not null).ToArray().Select(RestoreOnExitAsync).ToArray();
        // Timeout is not success: incomplete originals stay in recovery.json.
        await Task.WhenAny(Task.WhenAll(attempts), Task.Delay(900));
        return Items.Count(x => x.Original is not null);
    }
    private async Task RestoreOnExitAsync(WindowItem item)
    {
        try
        {
            if (_operations.TryGetValue(item, out var pending)) await pending;
            if (item.Original is not { } s) return;
            item.BackgroundEnabled = false;
            await RestoreSnapshotAsync(s);
            item.Original = null; item.NotifyState(); Journal();
        }
        catch (Exception e) { System.Diagnostics.Trace.TraceWarning("Exit recovery: {0}", e.Message); }
    }
    public void RestoreForSessionEnd()
    {
        foreach (var item in Items.Where(x => x.Original is not null).ToArray()) _ = RestoreOnExitAsync(item);
    }
}
