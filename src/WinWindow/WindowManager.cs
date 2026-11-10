using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinWindow;

internal sealed class WindowManager
{
    private readonly StateStore _store;
    private bool _refreshing;
    public ObservableCollection<WindowItem> Items { get; } = new();
    public event Action<string>? Notice;
    public event Action? Changed;
    public bool HasRules => _store.Preferences.Rules.Count != 0;
    public WindowManager(StateStore store) => _store = store;
    private static bool Same(WindowIdentity? a, WindowIdentity? b) => a is not null && b is not null && a.Handle == b.Handle && a.ProcessId == b.ProcessId && a.ProcessStarted == b.ProcessStarted && a.ClassName == b.ClassName;

    public async Task RecoverPreviousSessionAsync()
    {
        foreach (var snapshot in _store.ReadRecovery())
        {
            if (!Native.Matches(snapshot.Identity)) continue;
            Items.Add(new() { Identity = snapshot.Identity, Title = snapshot.Title, Original = snapshot, IsHidden = !Native.IsWindowVisible(snapshot.Identity.Hwnd), IsMinimized = Native.IsIconic(snapshot.Identity.Hwnd) });
        }
        if (Items.Count != 0)
        {
            int failures = await RestoreAllAsync();
            Notice?.Invoke(failures == 0 ? "已恢复上次运行遗留的窗口状态。" : $"有 {failures} 个窗口尚未恢复，请在列表中处理。");
        }
        Journal();
    }

    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var found = Native.Enumerate();
            bool removedManaged = false;
            foreach (var item in Items.Where(x => x.Identity is not null).ToArray())
            {
                if (!Native.Matches(item.Identity!))
                {
                    removedManaged |= item.Original is not null;
                    Items.Remove(item);
                    continue;
                }
                var live = found.FirstOrDefault(x => Same(x.Identity, item.Identity));
                if (live is null && !item.IsManaged) { Items.Remove(item); continue; }
                item.Title = Native.Title(item.Hwnd);
                item.IsMinimized = Native.IsIconic(item.Hwnd);
                item.IsHidden = !Native.IsWindowVisible(item.Hwnd);
                if (!item.IsBusy)
                {
                    item.Pinned = Native.IsTopmost(item.Hwnd);
                    item.ClickThrough = (Native.Style(item.Hwnd) & Native.Transparent) != 0;
                    if (Native.GetLayeredWindowAttributes(item.Hwnd, out _, out byte alpha, out uint flags) && (flags & 2) != 0) item.Opacity = Native.ToPercent(alpha);
                    else if ((Native.Style(item.Hwnd) & Native.Layered) == 0) item.Opacity = 100;
                }
            }
            foreach (var item in found)
                if (!Items.Any(x => Same(x.Identity, item.Identity))) Items.Add(item);
            if (removedManaged) Journal();
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
                if (item.RuleId == rule.Id) continue;
                item.RuleId = rule.Id;
                // Mark association before applying. A failed rule is reported, not retried on every event.
                try
                {
                    bool pin = rule.Pinned, through = rule.ClickThrough;
                    int opacity = rule.Opacity;
                    SetPin(item, pin, false);
                    if (opacity != 100) SetOpacity(item, opacity, false);
                    if (through) SetClickThrough(item, true, false);
                }
                catch (Exception e) { Notice?.Invoke($"未能完整应用“{item.Title}”的规则：{e.Message}"); }
                item.NotifyState();
            }
            else if (placeholder is null && !Items.Any(x => x.RuleId == rule.Id))
                Items.Add(new() { Title = rule.Title, RuleId = rule.Id, IsAvailable = false, Pinned = rule.Pinned, Opacity = rule.Opacity, ClickThrough = rule.ClickThrough });
        }
        foreach (var pending in Items.Where(x => x.Identity is null && !_store.Preferences.Rules.Any(r => r.Id == x.RuleId)).ToArray()) Items.Remove(pending);
    }

    public WindowItem SelectTarget(nint hwnd)
    {
        hwnd = Native.GetAncestor(hwnd, 2);
        if (!Native.IsTarget(hwnd)) throw new InvalidOperationException("请选择其他应用的窗口，不能管理桌面或本程序自身。");
        var id = Native.Identity(hwnd) ?? throw new InvalidOperationException("无法读取目标窗口身份，请检查权限。");
        var existing = Items.FirstOrDefault(x => Same(x.Identity, id));
        if (existing is not null) return existing;
        var item = new WindowItem { Identity = id, Title = Native.Title(hwnd), Pinned = Native.IsTopmost(hwnd), IsMinimized = Native.IsIconic(hwnd) };
        Items.Add(item);
        Changed?.Invoke();
        return item;
    }

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
        catch
        {
            (s.EditedTopmost, s.EditedOpacity, s.EditedClickThrough, s.Parked, s.BeforeParking) = previous;
            throw;
        }
    }
    private void Journal() => _store.WriteRecovery(Items.Where(x => x.Original is not null).Select(x => x.Original!));
    private void Finish(WindowItem item, bool updateRule)
    {
        item.NotifyState();
        if (updateRule) UpdateRule(item);
        Changed?.Invoke();
    }
    public void SetPin(WindowItem item, bool value, bool updateRule = true)
    {
        Track(item, s => s.EditedTopmost = true);
        Native.SetTopmost(item.Hwnd, value);
        item.Pinned = Native.IsTopmost(item.Hwnd);
        if (item.Pinned != value) throw new InvalidOperationException("窗口未接受置顶设置。");
        Finish(item, updateRule);
    }
    public void SetOpacity(WindowItem item, int value, bool updateRule = true)
    {
        value = Math.Clamp(value, 0, 100);
        Track(item, s => s.EditedOpacity = true);
        Native.SetOpacity(item.Original!, value);
        item.Opacity = value;
        Finish(item, updateRule);
    }
    public void SetClickThrough(WindowItem item, bool value, bool updateRule = true)
    {
        Track(item, s => s.EditedClickThrough = true);
        Native.SetClickThrough(item.Original!, value, item.Opacity);
        item.ClickThrough = (Native.Style(item.Hwnd) & Native.Transparent) != 0;
        Finish(item, updateRule);
    }
    public void SetRemembered(WindowItem item, bool remember)
    {
        if (!remember)
        {
            _store.Preferences.Rules.RemoveAll(x => x.Id == item.RuleId);
            item.RuleId = null;
            if (item.Identity is null) Items.Remove(item);
        }
        else
        {
            Manage(item);
            var rule = new WindowRule { ProcessPath = item.ProcessPath, ClassName = item.Identity!.ClassName, Title = item.Title, Pinned = item.Pinned, Opacity = item.Opacity, ClickThrough = item.ClickThrough };
            _store.Preferences.Rules.Add(rule);
            item.RuleId = rule.Id;
        }
        _store.SaveSoon(); Finish(item, false);
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
        for (int i = 0; i < 8; i++)
        {
            if (!Native.Matches(id)) throw new InvalidOperationException("操作期间目标窗口已关闭。");
            if (predicate(id.Hwnd)) return true;
            await Task.Delay(60);
        }
        return false;
    }

    public async Task<string> ParkAsync(WindowItem item)
    {
        if (item.IsBusy) return "该窗口正在处理中。";
        var snapshot = Manage(item);
        if (Native.IsIconic(item.Hwnd) || !Native.IsWindowVisible(item.Hwnd)) return "窗口已经最小化或隐藏。";
        item.IsBusy = true;
        try
        {
            var placement = Native.GetPlacement(item.Hwnd);
            Track(item, s => { s.BeforeParking = placement; s.Parked = true; });
            Native.ShowWindowAsync(item.Hwnd, Native.ForceMinimize);
            bool minimized = await WaitForAsync(snapshot.Identity, Native.IsIconic);
            if (!minimized)
            {
                var fallback = placement;
                fallback.ShowCmd = 7; // SW_SHOWMINNOACTIVE; async placement avoids cross-thread hangs.
                Native.ApplyPlacement(item.Hwnd, fallback);
                minimized = await WaitForAsync(snapshot.Identity, Native.IsIconic);
            }
            if (!minimized)
            {
                Native.ShowWindowAsync(item.Hwnd, Native.Hide);
                bool hidden = await WaitForAsync(snapshot.Identity, h => !Native.IsWindowVisible(h) || Native.IsIconic(h));
                if (!hidden) return "未确认最小化或隐藏。恢复记录已保留，请检查目标权限或显示模式。";
            }
            item.IsMinimized = Native.IsIconic(item.Hwnd);
            item.IsHidden = !Native.IsWindowVisible(item.Hwnd);
            return item.IsHidden ? "已隐藏：该窗口没有接受最小化，可随时恢复。" : "已真正最小化。";
        }
        finally { item.IsBusy = false; Finish(item, false); }
    }

    private async Task RestoreParkingCoreAsync(WindowItem item)
    {
        var s = item.Original;
        if (s?.Parked != true) return;
        Native.EnsureIdentity(s.Identity);
        Native.ApplyPlacement(item.Hwnd, s.BeforeParking);
        Native.ShowWindowAsync(item.Hwnd, (int)s.BeforeParking.ShowCmd);
        if (!await WaitForAsync(s.Identity, h => Native.IsWindowVisible(h) && !Native.IsIconic(h)))
            throw new InvalidOperationException("目标尚未恢复，记录仍保留。请检查权限或再次点击恢复。");
        s.Parked = false;
        item.IsHidden = false; item.IsMinimized = false;
        Journal();
    }
    public async Task RestoreParkingAsync(WindowItem item, bool activate = true)
    {
        if (item.IsBusy) return;
        item.IsBusy = true;
        try
        {
            Manage(item);
            if (item.Original!.Parked) await RestoreParkingCoreAsync(item);
            else
            {
                Native.ShowWindowAsync(item.Hwnd, Native.Restore);
                if (!await WaitForAsync(item.Identity!, h => Native.IsWindowVisible(h) && !Native.IsIconic(h)))
                    throw new InvalidOperationException("目标未接受恢复操作。");
            }
            if (activate) Native.SetForegroundWindow(item.Hwnd);
        }
        finally { item.IsBusy = false; Finish(item, false); }
    }
    public async Task ReleaseAsync(WindowItem item, bool forgetRule = false, bool updateRule = true)
    {
        if (item.IsBusy) throw new InvalidOperationException("请等待该窗口的当前操作结束。");
        if (item.Original is null) { if (forgetRule) SetRemembered(item, false); return; }
        item.IsBusy = true;
        try
        {
            var s = item.Original;
            if (Native.Matches(s.Identity))
            {
                await RestoreParkingCoreAsync(item);
                Native.RestoreStyles(s);
                item.Pinned = s.Topmost;
                item.Opacity = s.LayeredReadable && (s.LayeredFlags & 2) != 0 ? Native.ToPercent(s.Alpha) : 100;
                item.ClickThrough = (s.ExtendedStyle & Native.Transparent) != 0;
            }
            item.Original = null;
            Journal();
            if (forgetRule) SetRemembered(item, false);
            else if (updateRule) UpdateRule(item);
        }
        finally { item.IsBusy = false; Finish(item, false); }
    }
    public async Task<int> RestoreAllAsync(bool updateRules = true)
    {
        int failures = 0;
        foreach (var item in Items.Where(x => x.IsManaged).ToArray())
        {
            try { await ReleaseAsync(item, false, updateRules); }
            catch (Exception e) { failures++; Notice?.Invoke($"“{item.Title}”未恢复：{e.Message}"); }
        }
        return failures;
    }
    public void RestoreForSessionEnd()
    {
        // Windows shutdown cannot await the UI loop. Keep the recovery journal until next launch.
        foreach (var s in Items.Where(x => x.Original is not null).Select(x => x.Original!))
        {
            if (!Native.Matches(s.Identity)) continue;
            try
            {
                if (s.Parked) { Native.ApplyPlacement(s.Identity.Hwnd, s.BeforeParking); Native.ShowWindowAsync(s.Identity.Hwnd, (int)s.BeforeParking.ShowCmd); }
                Native.RestoreStyles(s);
            }
            catch (Exception e) { Notice?.Invoke(e.Message); }
        }
    }
}
