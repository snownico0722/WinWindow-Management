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
    public bool HasManagedWindows => Items.Any(x => x.IsManaged);
    private readonly Dictionary<string, PendingLaunch> _pendingLaunches = new();
    private readonly HashSet<string> _launching = new(StringComparer.OrdinalIgnoreCase);
    private long _nextLaunchCheck;
    private sealed record PendingLaunch(string Path, string ClassName, WindowIdentity[] Existing, long Expires);
    private readonly Func<WindowIdentity, Dictionary<string, bool>> _readMutes;
    private readonly Action<WindowIdentity, IReadOnlyDictionary<string, bool>, bool?> _writeMutes;
    private readonly Func<string, Task<bool>> _launchProcess;

    public WindowManager(StateStore store) : this(store, Native.GetProcessMutes, Native.SetProcessMutes, Native.LaunchApplicationAsync) { }

    internal WindowManager(StateStore store, Func<WindowIdentity, Dictionary<string, bool>> readMutes,
        Action<WindowIdentity, IReadOnlyDictionary<string, bool>, bool?> writeMutes,
        Func<string, Task<bool>>? launchProcess = null)
    {
        _store = store;
        _readMutes = readMutes;
        _writeMutes = writeMutes;
        _launchProcess = launchProcess ?? Native.LaunchApplicationAsync;
    }
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
                snapshot.OriginalAudioMutes is not { Count: > 0 } &&
                !(snapshot.RestoreActivation && Native.MatchesProcess(snapshot.Identity))) continue;
            var item = new WindowItem { Identity = snapshot.Identity, Title = snapshot.Title, Original = snapshot, Listed = !snapshot.RemovedPendingBackground, IsHidden = !Native.IsWindowVisible(snapshot.Identity.Hwnd), IsMinimized = Native.IsIconic(snapshot.Identity.Hwnd) };
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
            if (!Native.Matches(item.Identity!)) Items.Remove(item);
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
                    if (item.Original?.OriginalAudioMutes is { Count: > 0 } ||
                        (item.Original?.RestoreActivation == true && Native.MatchesProcess(item.Identity!)))
                    {
                        if (item.Original?.RemovedPendingBackground != true) _ = RecoverOneAsync(item);
                        continue;
                    }
                    removed |= item.Original is not null; Items.Remove(item); continue;
                }
                var live = found.FirstOrDefault(x => Same(x.Identity, item.Identity));
                if (live is null && !item.IsManaged && item.Original is null) { Items.Remove(item); continue; }
                item.Title = live?.Title ?? Native.Title(item.Hwnd);
                bool observedParked = item.IsIconCollapsed || item.IsHidden || item.IsMinimized;
                bool minimized = Native.IsIconic(item.Hwnd), hidden = !Native.IsWindowVisible(item.Hwnd);
                item.IsMinimized = minimized; item.IsHidden = hidden;
                if (item.Original is { Parked: true } parking)
                {
                    if (parking.ParkingPending && hidden)
                        Track(item, snapshot => snapshot.ParkingPending = false);
                    else if (!parking.ParkingPending && observedParked && !minimized && !hidden &&
                        (!item.IsIconCollapsed || Native.HasReachableFrame(item.Hwnd)))
                    {
                        // 外部唤回后让用户保留当前坐标。桌面图标须等窗口回到屏幕再撤下。
                        Track(item, snapshot => snapshot.Parked = false);
                        item.IsIconCollapsed = false;
                    }
                }
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
            ApplyRules();
            await RefreshLaunchAvailabilityAsync();
            if (!_stopping) Changed?.Invoke();
        }
        finally { _refreshing = false; }
    }
    private void ApplyRules()
    {
        long now = Environment.TickCount64;
        foreach (var expired in _pendingLaunches.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray())
            _pendingLaunches.Remove(expired);

        var rules = _store.Preferences.Rules.ToArray();
        // 优先保留精确匹配。只有保存规则和新窗口双方都唯一时，才按 WPF 稳定类名恢复。
        foreach (var rule in rules)
        {
            if (Items.Any(x => x.Identity is not null && x.RuleId == rule.Id))
            {
                _pendingLaunches.Remove(rule.Id);
                continue;
            }
            var placeholder = Items.FirstOrDefault(x => x.Identity is null && x.RuleId == rule.Id);
            var candidates = Items.Where(x => x.IsAvailable && !x.IsBusy && x.AutoRemember &&
                x.Original?.RemovedPendingBackground != true && x.RuleId is null && rule.Matches(x));
            var item = candidates.FirstOrDefault(x => x.Identity!.ClassName == rule.ClassName);
            if (item is null && candidates.Take(2).ToArray() is [var candidate] &&
                rules.Count(other => other.Matches(candidate)) == 1)
                item = candidate;
            if (item is not null)
            {
                _pendingLaunches.Remove(rule.Id);
                if (placeholder is not null) Items.Remove(placeholder);
                item.RuleId = rule.Id;
                _ = ApplyRuleAsync(item, rule);
            }
            else if (placeholder is null)
            {
                Items.Add(new()
                {
                    SavedProcessPath = rule.ProcessPath ?? "", Title = rule.Title, RuleId = rule.Id, IsAvailable = false,
                    Pinned = rule.Pinned, Opacity = rule.Opacity, ClickThrough = rule.ClickThrough,
                    Muted = rule.Muted, BackgroundEnabled = rule.KeepRunning
                });
            }
        }

        // 主动启动时允许标题改变，但只认领启动前未出现、类名吻合且唯一的候选。
        // 保留保存规则原有标题和类名，后续的普通启动仍走精确匹配。
        foreach (var rule in rules)
        {
            if (!_pendingLaunches.TryGetValue(rule.Id, out var launch)) continue;
            var candidates = Items.Where(x => x.IsAvailable && !x.IsBusy && x.AutoRemember &&
                x.Original?.RemovedPendingBackground != true && x.RuleId is null && x.Identity is { } id &&
                string.Equals(id.ProcessPath, launch.Path, StringComparison.OrdinalIgnoreCase) &&
                (launch.ClassName.Length == 0 || WindowRule.MatchesClass(launch.ClassName, id.ClassName)) &&
                !launch.Existing.Any(previous => Same(previous, id))).Take(2).ToArray();
            if (candidates is not [var matched]) continue;
            _pendingLaunches.Remove(rule.Id);
            var placeholder = Items.FirstOrDefault(x => x.Identity is null && x.RuleId == rule.Id);
            if (placeholder is not null) Items.Remove(placeholder);
            matched.RuleId = rule.Id;
            _ = ApplyRuleAsync(matched, rule);
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
        if (item.Original is { RemovedPendingBackground: true } detached)
        {
            // A new management session needs a fresh baseline for styles and placement.
            var fresh = Native.Capture(item.Identity);
            fresh.RestoreActivation = detached.RestoreActivation;
            fresh.InjectedBackground = detached.InjectedBackground;
            item.Original = fresh;
            Journal();
        }
        item.Original ??= Native.Capture(item.Identity); item.Listed = true;
        item.NotifyState(); return item.Original;
    }
    private void Track(WindowItem item, Action<WindowSnapshot> edit)
    {
        var s = Manage(item);
        var previous = (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.OriginalAudioMutes, s.RestoreActivation, s.InjectedBackground, s.Parked, s.ParkingPending, s.BeforeParking);
        edit(s);
        var next = (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.OriginalAudioMutes, s.RestoreActivation, s.InjectedBackground, s.Parked, s.ParkingPending, s.BeforeParking);
        if (previous.Equals(next)) return;
        try { Journal(); }
        catch { (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.OriginalAudioMutes, s.RestoreActivation, s.InjectedBackground, s.Parked, s.ParkingPending, s.BeforeParking) = previous; throw; }
    }
    // Published on the dispatcher after the durable intent changes. The layout worker
    // can check one exact window lifetime without touching dispatcher-owned rows.
    private volatile WindowIdentity[] _keptWindows = [];
    internal bool KeepsRunning(nint hwnd, uint pid, long started, string windowClass) =>
        _keptWindows.Any(id => id.Handle == hwnd && id.ProcessId == pid &&
            id.ProcessStarted == started && id.ClassName == windowClass);

    private void Journal()
    {
        var recorded = Items.Where(x => x.Original != null).Select(x => x.Original!).ToArray();
        _store.WriteRecovery(recorded);
        _keptWindows = recorded.Where(s => s.RestoreActivation).Select(s => s.Identity).ToArray();
    }
    private void Finish(WindowItem item, bool updateRule)
    { item.NotifyState(); if (!_stopping && updateRule) UpdateRule(item); }
    public void AddToList(WindowItem item)
    {
        if (_stopping || item.Identity is null) return;
        Native.EnsureIdentity(item.Identity);
        item.Listed = true;
        // 主动加入管理的窗口默认记住其程序路径，目标退出后保留在列表中并展示播放启动入口；用户仍可通过右键取消记忆或点击 × 移出。
        AutoRemember(item);
        _store.SaveSoon();
        item.NotifyState();
        Changed?.Invoke();
    }

    internal async Task RefreshLaunchAvailabilityAsync()
    {
        if (_stopping || Environment.TickCount64 < _nextLaunchCheck) return;
        var pending = Items.Where(item => item.Identity is null).ToArray();
        if (pending.Length == 0) return;
        _nextLaunchCheck = Environment.TickCount64 + 800;
        var paths = pending.Select(item => item.SavedProcessPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var available = await Task.Run(() => paths.Where(Native.CanLaunchApplication).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (_stopping) return;
        long now = Environment.TickCount64;
        // ShellExecute 返回时进程可能还没出现。保留同一路径的启动占用，避免播放按钮短暂变回可用。
        var awaitingWindow = _pendingLaunches.Values.Where(launch => launch.Expires > now)
            .Select(launch => launch.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in pending)
            item.CanLaunch = available.Contains(item.SavedProcessPath) && !_launching.Contains(item.SavedProcessPath)
                && !awaitingWindow.Contains(item.SavedProcessPath);
    }

    public async Task LaunchAsync(WindowItem item)
    {
        if (_stopping || !item.CanLaunch || !Items.Contains(item) || item.RuleId is not { } id ||
            _store.Preferences.Rules.FirstOrDefault(rule => rule.Id == id) is not { } saved) return;
        string path = item.ProcessPath;
        if (_pendingLaunches.Values.Any(launch => launch.Expires > Environment.TickCount64 &&
            string.Equals(path, launch.Path, StringComparison.OrdinalIgnoreCase)) || !_launching.Add(path)) return;
        try
        {
            foreach (var row in Items.Where(row => row.Identity is null &&
                string.Equals(row.SavedProcessPath, path, StringComparison.OrdinalIgnoreCase)))
                row.CanLaunch = false;
            await OperateAsync(item, async () =>
            {
                string className = saved.ClassName ?? "";
                var existing = await Task.Run(() => Native.Enumerate().Where(row =>
                    string.Equals(row.ProcessPath, path, StringComparison.OrdinalIgnoreCase))
                    .Select(row => row.Identity!).ToArray());
                if (_stopping || !await _launchProcess(path) || _stopping) return;
                _pendingLaunches[id] = new PendingLaunch(path, className, existing, Environment.TickCount64 + 15_000);
                _ = FollowUpLaunchRefreshAsync(id);
            });
        }
        finally
        {
            _launching.Remove(path);
            _nextLaunchCheck = 0;
            await RefreshAsync();
        }
    }

    private async Task FollowUpLaunchRefreshAsync(string id)
    {
        foreach (int delay in new[] { 400, 800, 1600, 2400, 4000, 6000 })
        {
            if (_stopping || !_pendingLaunches.ContainsKey(id)) return;
            await Task.Delay(delay);
            if (_stopping || !_pendingLaunches.ContainsKey(id)) return;
            try { await RefreshAsync(); }
            catch (Exception e) { if (!_stopping) Notice?.Invoke(e.Message); }
        }
    }

    public Task CollapseToIconAsync(WindowItem item) => OperateAsync(item, async () =>
    {
        var snapshot = Manage(item);
        if (Native.IsIconic(item.Hwnd) || !Native.IsWindowVisible(item.Hwnd)) return;
        var placement = Native.GetPlacement(item.Hwnd);
        item.IconWindowGeometry = Native.TryGetIconWindowGeometry(item.Hwnd, out var geometry) ? geometry : null;
        bool alreadyPending = snapshot.ParkingPending;
        Track(item, s => { s.BeforeParking = placement; s.Parked = s.ParkingPending = true; });
        // 图标是恢复入口。隐藏目标可同时收起任务栏项，原窗口的层级和透明度仍归原来的开关管理。
        if (!Native.ShowWindowAsync(item.Hwnd, Native.Hide))
        {
            if (!alreadyPending) Track(item, s => s.Parked = s.ParkingPending = false);
            throw new InvalidOperationException(UiText.Get("CollapseRejected"));
        }
        if (!await WaitForAsync(snapshot.Identity, h => !Native.IsWindowVisible(h)))
            throw new InvalidOperationException(UiText.Get("CollapseRejected"));
        Track(item, s => s.ParkingPending = false);
        item.IsHidden = true;
        item.IsIconCollapsed = true;
        Finish(item, false);
    });

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
            bool restoreBefore = item.Original?.RestoreActivation == true;
            bool? injectedBefore = item.Original?.InjectedBackground;
            bool wasInjected = item.Original is { RestoreActivation: true } previous &&
                previous.InjectedBackground != false;
            if (enabled)
                Track(item, s => { s.RestoreActivation = true; s.InjectedBackground = wasInjected || enhanced; });
            var identity = item.Identity!;
            bool targetMayHaveChanged = false;
            bool injected;
            try
            {
                injected = await Task.Run(() => Native.SetBackgroundActivity(
                    identity, enabled, enhanced, lockCursor, wasInjected, NativeDirectory, out targetMayHaveChanged));
            }
            catch when (enabled && !targetMayHaveChanged)
            {
                var original = item.Original!;
                original.RestoreActivation = restoreBefore;
                original.InjectedBackground = injectedBefore;
                Journal();
                throw;
            }
            item.BackgroundEnabled = enabled;
            if (item.Original is { } snapshot)
            {
                snapshot.RestoreActivation = enabled;
                snapshot.InjectedBackground = enabled && injected;
                Journal();
            }
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
            catch (Exception e) { if (!_stopping) Notice?.Invoke(UiText.Format("BackgroundModeChangeFailed", item.Title, e.Message)); }
            finally { _backgroundOperations.Remove(identity.ProcessId); }
        }
    }

    private IEnumerable<WindowItem> AudioRows(WindowIdentity identity) => Items.Where(x =>
        x.Identity is { } id && id.ProcessId == identity.ProcessId && id.ProcessStarted == identity.ProcessStarted);

    private WindowItem? AudioOwner(WindowIdentity identity) =>
        AudioRows(identity).FirstOrDefault(row => row.Original?.OriginalAudioMutes is not null);

    private async Task RefreshAudioMutesAsync()
    {
        var targets = Items.Where(x => x.IsManaged && x.Identity is not null)
            .Select(x => x.Identity!).DistinctBy(id => (id.ProcessId, id.ProcessStarted)).ToArray();
        foreach (var identity in targets)
        {
            if (_stopping) return;
            try
            {
                var rows = AudioRows(identity).ToArray();
                if (rows.Any(row => row.IsBusy)) continue;
                var owner = AudioOwner(identity);
                var baseline = owner?.Original?.OriginalAudioMutes;
                var seen = owner?.MutedSessionIds;
                var current = await Task.Run(() => _readMutes(identity));
                if (_stopping) return;
                // A click may have completed while the mixer was being read.
                if (rows.Any(row => row.IsBusy) || owner != AudioOwner(identity) ||
                    baseline != owner?.Original?.OriginalAudioMutes || seen != owner?.MutedSessionIds) continue;

                if (owner is not null && baseline is { Count: > 0 })
                {
                    // A session returned to its original state outside WinWindow.
                    // Release our claim so a later exit won't undo the player's next edit.
                    var released = baseline.Keys.Where(id => current.TryGetValue(id, out bool muted) && muted == baseline[id]).ToArray();
                    if (released.Length != 0)
                        Track(owner, s => s.OriginalAudioMutes = baseline.ExceptBy(released, pair => pair.Key).ToDictionary());
                }
                if (owner is not null && seen is not null)
                {
                    if (!current.Any(pair => seen.Contains(pair.Key) && !pair.Value))
                    {
                        var added = current.Where(pair => !seen.Contains(pair.Key)).ToDictionary();
                        if (added.Count != 0)
                            await OperateAsync(owner, () => ApplyMuteAsync(owner, added, true));
                        continue;
                    }
                    owner.MutedSessionIds = null;
                    foreach (var row in rows)
                        if (_store.Preferences.Rules.FirstOrDefault(r => r.Id == row.RuleId) is { } rule)
                            rule.Muted = false;
                    _store.SaveSoon();
                }
                foreach (var row in rows) row.Muted = current.Count != 0 && current.Values.All(muted => muted);
            }
            catch (Exception e) { if (!_stopping) Notice?.Invoke(e.Message); }
        }
    }

    public Task SetMuteAsync(WindowItem item, bool value, bool updateRule = true)
    {
        EnsureIdle(item);
        var identity = item.Identity ?? throw new InvalidOperationException(UiText.Get("WindowPending"));
        var owner = AudioOwner(identity) ?? item;
        return OperateAsync(owner, async () =>
        {
            Native.EnsureIdentity(identity);
            if (!value && owner.MutedSessionIds is not null && owner.Original is { OriginalAudioMutes: not null } snapshot)
                await RestoreAudioAsync(snapshot);
            else
            {
                Track(owner, s => s.OriginalAudioMutes ??= new(StringComparer.Ordinal));
                var current = await Task.Run(() => _readMutes(identity));
                if (_stopping) return;
                await ApplyMuteAsync(owner, current, value);
            }
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
        var changes = current.Where(pair => pair.Value != value).ToDictionary();
        // 同一进程会陆续出现新的音频会话。首次改动时记下各自的静音状态，多次切换后仍能还原到接管前。
        foreach (var (id, muted) in changes) baseline.TryAdd(id, muted);
        if (baseline.Count != snapshot.OriginalAudioMutes!.Count) Track(owner, s => s.OriginalAudioMutes = baseline);
        await Task.Run(() => _writeMutes(snapshot.Identity, changes, value));
        if (value)
        {
            var seen = new HashSet<string>(owner.MutedSessionIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            seen.UnionWith(current.Keys);
            owner.MutedSessionIds = seen;
        }
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
    public Task SetOpacityAsync(WindowItem item, int value, bool updateRule = true, int? quickBaseline = null) =>
        OperateAsync(item, async () =>
        {
            value = Math.Clamp(value, 0, 100); Track(item, s => s.EditedOpacity = true);
            var original = item.Original!;
            await Task.Run(() => { Native.EnsureIdentity(original.Identity); Native.SetOpacity(original, value); });
            item.OpacityBeforeQuickToggle = quickBaseline;
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
        bool pendingBackground = false;
        if (item.Original is { } snapshot)
        {
            item.BackgroundEnabled = false;
            try { await RestoreSnapshotAsync(item, snapshot, unpin: true); }
            catch (Exception e) when (snapshot.RestoreActivation && !snapshot.Parked &&
                snapshot.OriginalAudioMutes is null && !snapshot.EditedTopmost &&
                !snapshot.EditedOpacity && !snapshot.EditedClickThrough)
            {
                snapshot.RemovedPendingBackground = true;
                pendingBackground = true;
                Notice?.Invoke(UiText.Format("BackgroundCleanupPending", item.Title, e.Message));
            }
        }
        else if (item.Identity is not null && Native.Matches(item.Identity))
        {
            Track(item, s => s.EditedTopmost = true);
            await Task.Run(() => { Native.EnsureIdentity(item.Identity); Native.SetTopmost(item.Hwnd, false); });
        }
        item.BackgroundEnabled = false;
        item.WantsPin = false;
        item.IsIconCollapsed = false;
        item.OpacityBeforeQuickToggle = null;
        if (item.RuleId is { } rid) _pendingLaunches.Remove(rid);
        _store.Preferences.Rules.RemoveAll(r => r.Id == item.RuleId);
        item.RuleId = null; item.Listed = false;
        if (!pendingBackground)
        {
            item.Original = null;
            Items.Remove(item);
        }
        item.NotifyState();
        Journal(); _store.SaveSoon();
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
            Title = item.Title, Pinned = item.Pinned, Opacity = item.OpacityBeforeQuickToggle ?? item.Opacity,
            ClickThrough = item.ClickThrough,
            Muted = item.Muted, KeepRunning = item.BackgroundEnabled };
        _store.Preferences.Rules.Add(rule);
        item.RuleId = rule.Id;
    }

    private void UpdateRule(WindowItem item)
    {
        var rule = _store.Preferences.Rules.FirstOrDefault(x => x.Id == item.RuleId);
        if (rule is null) return;
        rule.Pinned = item.Pinned;
        // 快捷开关只影响当前窗。保存其他字段时也沿用规则里的不透明度。
        if (item.OpacityBeforeQuickToggle is null) rule.Opacity = item.Opacity;
        rule.ClickThrough = item.ClickThrough;
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
    public Task<string> ParkAsync(WindowItem item) => ParkAsync(item,
        hwnd => Native.ShowWindowAsync(hwnd, Native.ForceMinimize),
        (hwnd, placement) =>
        {
            placement.ShowCmd = 7;
            Native.ApplyPlacement(hwnd, placement);
        });

    internal async Task<string> ParkAsync(WindowItem item, Action<nint> forceMinimize,
        Action<nint, Native.Placement> minimizeWithoutActivation)
    {
        string result = "";
        await OperateAsync(item, async () =>
        {
            var snapshot = Manage(item);
            if (Native.IsIconic(item.Hwnd) || !Native.IsWindowVisible(item.Hwnd)) { result = UiText.Get("AlreadyParked"); return; }
            var placement = Native.GetPlacement(item.Hwnd);
            Track(item, s => { s.BeforeParking = placement; s.Parked = s.ParkingPending = true; });
            forceMinimize(item.Hwnd);
            bool hideRequested = false;
            bool minimized = await WaitForAsync(snapshot.Identity, Native.IsIconic);
            if (!minimized && !_stopping)
            {
                minimizeWithoutActivation(item.Hwnd, placement);
                minimized = await WaitForAsync(snapshot.Identity, Native.IsIconic);
            }
            if (!minimized && !_stopping)
            {
                hideRequested = Native.ShowWindowAsync(item.Hwnd, Native.Hide);
                if (!await WaitForAsync(snapshot.Identity, h => !Native.IsWindowVisible(h) || Native.IsIconic(h)))
                { result = UiText.Get("ParkRejected"); return; }
            }
            item.IsMinimized = Native.IsIconic(item.Hwnd); item.IsHidden = !Native.IsWindowVisible(item.Hwnd);
            if (item.IsHidden || (item.IsMinimized && !hideRequested))
                Track(item, s => s.ParkingPending = false);
            result = item.IsHidden ? UiText.Get("HiddenRestoreList") : UiText.Get("MinimizedStatus"); Finish(item, false);
        });
        return result;
    }
    private async Task RestoreAudioAsync(WindowSnapshot snapshot)
    {
        if (snapshot.OriginalAudioMutes is not { } baseline) return;
        await Task.Run(() => _writeMutes(snapshot.Identity, baseline, null));
        snapshot.OriginalAudioMutes = null;
        var rows = AudioRows(snapshot.Identity).ToArray();
        foreach (var row in rows) row.MutedSessionIds = null;
        Journal();
        if (_stopping || !Native.Matches(snapshot.Identity)) return;
        var current = await Task.Run(() => _readMutes(snapshot.Identity));
        foreach (var row in rows)
            row.Muted = current.Count != 0 && current.Values.All(muted => muted);
    }

    private async Task RestoreSnapshotAsync(WindowItem item, WindowSnapshot s, bool unpin = false)
    {
        // Activation cleanup may outlast the entire exit budget. Start independent
        // restoration now and checkpoint each completed part on the dispatcher.
        async Task RestoreActivationAsync()
        {
            if (!s.RestoreActivation) return;
            await Task.Run(() => Native.SetBackgroundActivity(
                s.Identity, false, false, false, s.InjectedBackground != false, NativeDirectory, out _));
            s.RestoreActivation = false;
            s.InjectedBackground = false;
            Journal();
        }

        async Task RestoreWindowAsync()
        {
            if (!Native.Matches(s.Identity)) return;
            nint hwnd = s.Identity.Hwnd;
            bool Restored(nint window) => item.IsIconCollapsed
                ? Native.HasReachableFrame(window) : Native.IsShown(window);
            // 可见窗口可能还没处理已投递的隐藏请求。保留恢复记录，同时继续恢复其他属性。
            bool waitingForParking = s.Parked && s.ParkingPending && Native.IsShown(hwnd);
            if (s.Parked && !waitingForParking)
            {
                if (!Restored(hwnd))
                {
                    Native.ApplyPlacement(hwnd, s.BeforeParking);
                    Native.ShowWindowAsync(hwnd, (int)s.BeforeParking.ShowCmd);
                    if (!await WaitForAsync(s.Identity, Restored))
                        throw new InvalidOperationException(UiText.Get("RestorePending"));
                }
                s.Parked = s.ParkingPending = false;
                Journal();
            }
            if (unpin || s.EditedTopmost || s.EditedOpacity || s.EditedClickThrough)
            {
                if (unpin) s.EditedTopmost = true;
                await Task.Run(() => { if (Native.Matches(s.Identity)) Native.RestoreStyles(s, unpin); });
                s.EditedTopmost = s.EditedOpacity = s.EditedClickThrough = false;
                Journal();
            }
            if (waitingForParking) throw new InvalidOperationException(UiText.Get("RestorePending"));
        }

        await Task.WhenAll(RestoreActivationAsync(), RestoreAudioAsync(s), RestoreWindowAsync());
    }
    public Task RestoreParkingAsync(WindowItem item, bool activate = true, Native.Rect? movedBadge = null) => OperateAsync(item, async () =>
    {
        var s = Manage(item);
        if (s.Parked && s.ParkingPending && Native.IsShown(item.Hwnd))
            throw new InvalidOperationException(UiText.Get("RestorePending"));
        bool maximized = s.Parked && s.BeforeParking.ShowCmd == Native.Maximized;
        bool restoreFromIcon = item.IsIconCollapsed && s.Parked;
        var planned = restoreFromIcon && item.IconWindowGeometry is { } geometry
            ? Native.PlanIconRestore(item.Hwnd, s.BeforeParking, geometry, movedBadge)
            : null;
        if (planned is { } plan)
        {
            if (!await RestoreToPlannedPositionAsync(s.Identity, plan, maximized, movedBadge))
            {
                // 应用拒绝目标屏时仍按原有布局恢复，确认窗口可用后再移除图标。
                if (!await ApplyRestorePlacementAsync(s.Identity, s.BeforeParking) ||
                    !await WaitForAsync(s.Identity, h => Native.IsRestoredWindow(h, maximized)))
                    throw new InvalidOperationException(UiText.Get("RestoreRejected"));
                Notice?.Invoke(UiText.Get("RestoreBadgeFallback"));
            }
        }
        else if (restoreFromIcon)
        {
            if (!await ApplyRestorePlacementAsync(s.Identity, s.BeforeParking) ||
                !await WaitForAsync(s.Identity, h => Native.IsRestoredWindow(h, maximized)))
                throw new InvalidOperationException(UiText.Get("RestoreRejected"));
        }
        else
        {
            if (s.Parked) Native.ApplyPlacement(item.Hwnd, s.BeforeParking);
            Native.ShowWindowAsync(item.Hwnd, s.Parked ? (int)s.BeforeParking.ShowCmd : Native.Restore);
            if (!await WaitForAsync(s.Identity, Native.IsShown))
                throw new InvalidOperationException(UiText.Get("RestoreRejected"));
        }
        Track(item, snapshot => snapshot.Parked = snapshot.ParkingPending = false);
        item.IsHidden = false;
        item.IsMinimized = false;
        item.IsIconCollapsed = false;
        if (activate && !_stopping) Native.SetForegroundWindow(item.Hwnd);
        Finish(item, false);
    });

    private static Task<bool> ApplyRestorePlacementAsync(WindowIdentity identity, Native.Placement placement) => Task.Run(() =>
    {
        Native.EnsureIdentity(identity);
        return Native.ApplyPlacement(identity.Hwnd, placement);
    });

    private static async Task<bool> RestoreToPlannedPositionAsync(WindowIdentity identity, Native.IconRestorePlacement plan,
        bool maximized, Native.Rect? movedBadge)
    {
        bool OnTarget(nint hwnd) => Native.IsRestoredWindow(hwnd, maximized, plan.Monitor);
        // SetWindowPlacement 同时提交最终位置和显示状态，隐藏窗第一次显形就在目标屏。
        if (!await ApplyRestorePlacementAsync(identity, plan.Placement) || !await WaitForAsync(identity, OnTarget))
            return false;
        if (maximized) return true;
        bool AtPosition(nint hwnd) => OnTarget(hwnd) && (movedBadge is { } badge
            ? Native.IsAtBadge(hwnd, badge, plan.Work)
            : Native.TryGetFrameBounds(hwnd, out var frame) && Native.FrameInside(frame, plan.Work));
        if (AtPosition(identity.Hwnd)) return true;
        // 跨 DPI 后实际大小可能变化，只在窗口已经出现在目标屏后补一次边缘校正。
        return await Task.Run(() => Native.PostFitRestoredWindow(identity, movedBadge, plan.Work)) &&
            await WaitForAsync(identity, AtPosition);
    }
    public Task ReleaseAsync(WindowItem item, bool forgetRule = false, bool updateRule = true) => OperateAsync(item, async () =>
    {
        if (item.Original is { } s)
        {
            item.BackgroundEnabled = false;
            await RestoreSnapshotAsync(item, s);
            item.IsIconCollapsed = false;
            item.OpacityBeforeQuickToggle = null;
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
            await RestoreSnapshotAsync(item, s);
            item.Original = null; item.NotifyState(); Journal();
        }
        catch (Exception e) { System.Diagnostics.Trace.TraceWarning("Exit recovery: {0}", e.Message); }
    }
    public void RestoreForSessionEnd()
    {
        foreach (var item in Items.Where(x => x.Original is not null).ToArray()) _ = RestoreOnExitAsync(item);
    }
}
