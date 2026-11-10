using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinWindow;

internal sealed class WindowManager
{
    private readonly StateStore _store;
    private readonly Dictionary<WindowItem, Task> _operations = new();
    private bool _refreshing;
    private volatile bool _stopping;
    private Task<int>? _shutdown;
    public ObservableCollection<WindowItem> Items { get; } = new();
    public event Action<string>? Notice;
    public event Action? Changed;
    public bool HasRules => _store.Preferences.Rules.Count != 0;
    public WindowManager(StateStore store) => _store = store;
    private static bool Same(WindowIdentity? a, WindowIdentity? b) => a is not null && b is not null && a.Handle == b.Handle && a.ProcessId == b.ProcessId && a.ProcessStarted == b.ProcessStarted && a.ClassName == b.ClassName;

    // State and the journal stay on the dispatcher. Only native work leaves it.
    // Each target has at most one in-flight operation; a hung target does not
    // hold a global lock or prevent operations on another target.
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
            item.IsBusy = false;
            _operations.Remove(item);
            completion.TrySetResult();
            if (!_stopping) Changed?.Invoke();
        }
    }

    public async Task RecoverPreviousSessionAsync()
    {
        foreach (var snapshot in _store.ReadRecovery())
        {
            if (!Native.Matches(snapshot.Identity)) continue;
            var item = new WindowItem { Identity = snapshot.Identity, Title = snapshot.Title, Original = snapshot, IsHidden = !Native.IsWindowVisible(snapshot.Identity.Hwnd), IsMinimized = Native.IsIconic(snapshot.Identity.Hwnd) };
            item.RuleId = _store.Preferences.Rules.FirstOrDefault(r => r.Matches(item))?.Id;
            Items.Add(item);
        }
        Journal();
        // Do not make startup wait indefinitely for one old hung window.
        // Busy recovery entries stay usable for identity/status display, and
        // retain their journal until their actual native restore completes.
        foreach (var item in Items.ToArray()) _ = RecoverOneAsync(item);
        await Task.CompletedTask;
    }
    private async Task RecoverOneAsync(WindowItem item)
    {
        try { await ReleaseAsync(item); }
        catch (Exception e) { if (!_stopping) Notice?.Invoke($"“{item.Title}”未恢复：{e.Message}"); }
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
                    removed |= item.Original is not null;
                    Items.Remove(item);
                    continue;
                }
                var live = found.FirstOrDefault(x => Same(x.Identity, item.Identity));
                if (live is null && !item.IsManaged) { Items.Remove(item); continue; }
                item.Title = live?.Title ?? Native.Title(item.Hwnd);
                item.IsMinimized = Native.IsIconic(item.Hwnd);
                item.IsHidden = !Native.IsWindowVisible(item.Hwnd);
                item.Pinned = Native.IsTopmost(item.Hwnd);
                item.ClickThrough = (Native.Style(item.Hwnd) & Native.Transparent) != 0;
                if (Native.GetLayeredWindowAttributes(item.Hwnd, out _, out byte alpha, out uint flags) && (flags & 2) != 0) item.Opacity = Native.ToPercent(alpha);
                else if ((Native.Style(item.Hwnd) & Native.Layered) == 0) item.Opacity = 100;
            }
            foreach (var item in found)
                if (!Items.Any(x => Same(x.Identity, item.Identity))) Items.Add(item);
            if (removed) Journal();
            ApplyRules();
            Changed?.Invoke();
        }
        finally { _refreshing = false; }
    }
    private void ApplyRules()
    {
        foreach (var rule in _store.Preferences.Rules.ToArray())
        {
            var matches = Items.Where(x => x.IsAvailable && rule.Matches(x)).ToArray();
            var placeholder = Items.FirstOrDefault(x => x.Identity is null && x.RuleId == rule.Id);
            if (matches.Length == 1)
            {
                if (placeholder is not null) Items.Remove(placeholder);
                var item = matches[0];
                if (item.RuleId == rule.Id || item.IsBusy) continue;
                item.RuleId = rule.Id;
                _ = ApplyRuleAsync(item, rule);
            }
            else if (placeholder is null && !Items.Any(x => x.RuleId == rule.Id))
                Items.Add(new() { Title = rule.Title, RuleId = rule.Id, IsAvailable = false, Pinned = rule.Pinned, Opacity = rule.Opacity, ClickThrough = rule.ClickThrough });
        }
        foreach (var pending in Items.Where(x => x.Identity is null && !_store.Preferences.Rules.Any(r => r.Id == x.RuleId)).ToArray()) Items.Remove(pending);
    }
    private async Task ApplyRuleAsync(WindowItem item, WindowRule rule)
    {
        try
        {
            await OperateAsync(item, async () =>
            {
                bool pin = rule.Pinned, through = rule.ClickThrough;
                int opacity = Math.Clamp(rule.Opacity, 0, 100);
                Track(item, s => { s.EditedTopmost = true; s.EditedOpacity |= opacity != item.Opacity; s.EditedClickThrough |= through != item.ClickThrough; });
                var original = item.Original!;
                await Task.Run(() =>
                {
                    Native.EnsureIdentity(original.Identity);
                    if (Native.IsTopmost(item.Hwnd) != pin) Native.SetTopmost(item.Hwnd, pin);
                    if (!_stopping && opacity != item.Opacity) Native.SetOpacity(original, opacity);
                    if (!_stopping && through != item.ClickThrough) Native.SetClickThrough(original, through, opacity);
                });
                if (_stopping) return;
                item.Pinned = Native.IsTopmost(item.Hwnd);
                item.Opacity = opacity; item.ClickThrough = through;
                item.NotifyState();
            });
        }
        catch (Exception e) { if (!_stopping) Notice?.Invoke($"“{item.Title}”的规则未完全应用：{e.Message}"); }
    }

    public WindowItem SelectTarget(nint hwnd)
    {
        hwnd = Native.GetAncestor(hwnd, 2);
        if (!Native.IsTarget(hwnd)) throw new InvalidOperationException("请选择其他应用的窗口。");
        var id = Native.Identity(hwnd) ?? throw new InvalidOperationException("无法读取窗口身份，请检查权限。");
        var existing = Items.FirstOrDefault(x => Same(x.Identity, id));
        if (existing is not null) return existing;
        long style = Native.Style(hwnd);
        int opacity = (style & Native.Layered) != 0 && Native.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) && (flags & 2) != 0 ? Native.ToPercent(alpha) : 100;
        var item = new WindowItem { Identity = id, Title = Native.Title(hwnd), Pinned = Native.IsTopmost(hwnd), IsMinimized = Native.IsIconic(hwnd), ClickThrough = (style & Native.Transparent) != 0, Opacity = opacity };
        Items.Add(item); Changed?.Invoke();
        return item;
    }
    private static void EnsureIdle(WindowItem item) { if (item.IsBusy) throw new InvalidOperationException("该窗口的操作尚未完成。"); }
    private WindowSnapshot Manage(WindowItem item)
    {
        if (item.Identity is null) throw new InvalidOperationException("该窗口尚未出现。");
        Native.EnsureIdentity(item.Identity);
        item.Original ??= Native.Capture(item.Identity);
        item.NotifyState();
        return item.Original;
    }
    private void Track(WindowItem item, Action<WindowSnapshot> edit)
    {
        var s = Manage(item);
        var previous = (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.Parked, s.BeforeParking);
        edit(s);
        var next = (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.Parked, s.BeforeParking);
        if (previous.Equals(next)) return;
        try { Journal(); }
        catch { (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.Parked, s.BeforeParking) = previous; throw; }
    }
    private void Journal() => _store.WriteRecovery(Items.Where(x => x.Original is not null).Select(x => x.Original!));
    private void Finish(WindowItem item, bool updateRule)
    {
        item.NotifyState();
        if (!_stopping && updateRule) UpdateRule(item);
    }
    public Task SetPinAsync(WindowItem item, bool value, bool updateRule = true) => OperateAsync(item, async () =>
    {
        Track(item, s => s.EditedTopmost = true);
        await Task.Run(() => { Native.EnsureIdentity(item.Identity!); Native.SetTopmost(item.Hwnd, value); });
        item.Pinned = Native.IsTopmost(item.Hwnd);
        if (item.Pinned != value) throw new InvalidOperationException("窗口未接受置顶设置。");
        Finish(item, updateRule);
    });
    public Task SetOpacityAsync(WindowItem item, int value, bool updateRule = true) => OperateAsync(item, async () =>
    {
        value = Math.Clamp(value, 0, 100);
        Track(item, s => s.EditedOpacity = true);
        var original = item.Original!;
        await Task.Run(() => { Native.EnsureIdentity(original.Identity); Native.SetOpacity(original, value); });
        item.Opacity = value; Finish(item, updateRule);
    });
    public Task SetClickThroughAsync(WindowItem item, bool value, bool updateRule = true) => OperateAsync(item, async () =>
    {
        Track(item, s => s.EditedClickThrough = true);
        var original = item.Original!;
        int opacity = item.Opacity;
        await Task.Run(() => { Native.EnsureIdentity(original.Identity); Native.SetClickThrough(original, value, opacity); });
        item.ClickThrough = (Native.Style(item.Hwnd) & Native.Transparent) != 0;
        if (item.ClickThrough != value) throw new InvalidOperationException("窗口未接受鼠标穿透设置。");
        Finish(item, updateRule);
    });
    public void SetRemembered(WindowItem item, bool remember)
    {
        if (_stopping) return;
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
            var rule = new WindowRule { ProcessPath = item.ProcessPath, ClassName = item.Identity!.ClassName, Title = item.Title, Pinned = item.Pinned, Opacity = item.Opacity, ClickThrough = item.ClickThrough };
            _store.Preferences.Rules.Add(rule); item.RuleId = rule.Id;
        }
        _store.SaveSoon(); Finish(item, false); Changed?.Invoke();
    }
    private void UpdateRule(WindowItem item)
    {
        var rule = _store.Preferences.Rules.FirstOrDefault(x => x.Id == item.RuleId);
        if (rule is null) return;
        rule.Pinned = item.Pinned; rule.Opacity = item.Opacity; rule.ClickThrough = item.ClickThrough;
        _store.SaveSoon();
    }
    private static async Task<bool> WaitForAsync(WindowIdentity id, Func<nint, bool> predicate)
    {
        int stable = 0;
        for (int i = 0; i < 8; i++)
        {
            if (!Native.Matches(id)) throw new InvalidOperationException("操作期间目标窗口已关闭。");
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
            if (Native.IsIconic(item.Hwnd) || !Native.IsWindowVisible(item.Hwnd)) { result = "窗口已收起。"; return; }
            var placement = Native.GetPlacement(item.Hwnd);
            Track(item, s => { s.BeforeParking = placement; s.Parked = true; });
            Native.ShowWindowAsync(item.Hwnd, Native.ForceMinimize);
            bool minimized = await WaitForAsync(snapshot.Identity, Native.IsIconic);
            if (!minimized && !_stopping)
            {
                var fallback = placement; fallback.ShowCmd = 7;
                Native.ApplyPlacement(item.Hwnd, fallback);
                minimized = await WaitForAsync(snapshot.Identity, Native.IsIconic);
            }
            if (!minimized && !_stopping)
            {
                Native.ShowWindowAsync(item.Hwnd, Native.Hide);
                if (!await WaitForAsync(snapshot.Identity, h => !Native.IsWindowVisible(h) || Native.IsIconic(h)))
                { result = "窗口未接受收起操作，恢复记录已保留。"; return; }
            }
            item.IsMinimized = Native.IsIconic(item.Hwnd); item.IsHidden = !Native.IsWindowVisible(item.Hwnd);
            result = item.IsHidden ? "已隐藏，可从列表恢复。" : "已最小化。";
            Finish(item, false);
        });
        return result;
    }
    private static async Task RestoreSnapshotAsync(WindowSnapshot s)
    {
        if (!Native.Matches(s.Identity)) return;
        if (s.Parked)
        {
            Native.ApplyPlacement(s.Identity.Hwnd, s.BeforeParking);
            Native.ShowWindowAsync(s.Identity.Hwnd, (int)s.BeforeParking.ShowCmd);
            if (!await WaitForAsync(s.Identity, h => Native.IsWindowVisible(h) && !Native.IsIconic(h)))
                throw new InvalidOperationException("窗口尚未恢复，记录已保留。");
        }
        await Task.Run(() => { if (Native.Matches(s.Identity)) Native.RestoreStyles(s); });
    }
    public Task RestoreParkingAsync(WindowItem item, bool activate = true) => OperateAsync(item, async () =>
    {
        var s = Manage(item);
        if (s.Parked) Native.ApplyPlacement(item.Hwnd, s.BeforeParking);
        Native.ShowWindowAsync(item.Hwnd, s.Parked ? (int)s.BeforeParking.ShowCmd : Native.Restore);
        if (!await WaitForAsync(s.Identity, h => Native.IsWindowVisible(h) && !Native.IsIconic(h)))
            throw new InvalidOperationException("窗口未接受恢复操作。");
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
        if (forgetRule) SetRemembered(item, false);
        else if (!_stopping && updateRule) UpdateRule(item);
        Finish(item, false);
    });
    public async Task<int> RestoreAllAsync(bool updateRules = true)
    {
        var attempts = Items.Where(x => x.IsManaged).ToArray().Select(async item =>
        {
            try { await ReleaseAsync(item, false, updateRules); return 0; }
            catch (Exception e) { if (!_stopping) Notice?.Invoke($"“{item.Title}”未恢复：{e.Message}"); return 1; }
        });
        return (await Task.WhenAll(attempts)).Sum();
    }
    public Task<int> ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();
    private async Task<int> ShutdownCoreAsync()
    {
        _stopping = true;
        var attempts = Items.Where(x => x.Original is not null).ToArray().Select(RestoreOnExitAsync).ToArray();
        // Native calls cannot be cancelled. Never claim that a timeout restored
        // a target; leave unfinished originals in recovery.json for next launch.
        await Task.WhenAny(Task.WhenAll(attempts), Task.Delay(900));
        return Items.Count(x => x.Original is not null);
    }
    private async Task RestoreOnExitAsync(WindowItem item)
    {
        try
        {
            if (_operations.TryGetValue(item, out var pending)) await pending;
            if (item.Original is not { } s) return;
            await RestoreSnapshotAsync(s);
            item.Original = null; item.NotifyState(); Journal();
        }
        catch (Exception e) { System.Diagnostics.Trace.TraceWarning("Exit recovery: {0}", e.Message); }
    }
    public void RestoreForSessionEnd()
    {
        // Windows' session-end callback cannot wait for foreign WndProcs.
        // The existing durable journal remains available if shutdown interrupts us.
        foreach (var item in Items.Where(x => x.Original is not null).ToArray()) _ = RestoreOnExitAsync(item);
    }
}
