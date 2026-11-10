using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace WinWindow;

internal sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    public string DirectoryPath { get; }
    public Preferences Preferences { get; }
    public string LoadWarning { get; private set; } = "";
    public event Action<string>? Error;
    private readonly DispatcherTimer _saveTimer;
    public StateStore(string directory)
    {
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        Preferences = Read<Preferences>("settings.json") ?? new();
        Preferences.Rules ??= new();
        Preferences.Hotkeys ??= WinWindow.Preferences.DefaultHotkeys();
        foreach (var pair in WinWindow.Preferences.DefaultHotkeys()) Preferences.Hotkeys.TryAdd(pair.Key, pair.Value);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePreferences(); };
    }
    private T? Read<T>(string name)
    {
        string path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (JsonException)
        {
            File.Move(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            LoadWarning = $"{name} 无法解析，原文件已保留，正在使用默认设置。";
            return default;
        }
    }
    private void Write<T>(string name, T value)
    {
        string path = Path.Combine(DirectoryPath, name), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
    public List<WindowSnapshot> ReadRecovery() => Read<List<WindowSnapshot>>("recovery.json") ?? new();
    // Called BEFORE a first mutation. A failed write aborts that mutation.
    public void WriteRecovery(IEnumerable<WindowSnapshot> snapshots) => Write("recovery.json", snapshots);
    public void SaveSoon() { _saveTimer.Stop(); _saveTimer.Start(); }
    public void SavePreferences()
    {
        _saveTimer.Stop();
        try { Write("settings.json", Preferences); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error?.Invoke("设置未能保存：" + e.Message); }
    }
}
