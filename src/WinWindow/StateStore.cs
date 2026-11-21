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

    public static bool RequestAdministratorOnLaunch(string directory)
    {
        // Leave invalid settings for the running instance to recover and report.
        try { return ReadJson<Preferences>(Path.Combine(directory, "settings.json"))?.RunAsAdministrator ?? true; }
        catch (JsonException) { return true; }
    }

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
        try { return ReadJson<T>(path); }
        catch (JsonException)
        {
            File.Move(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            LoadWarning = UiText.Format("InvalidSettings", name);
            return default;
        }
    }
    private static T? ReadJson<T>(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
    private void Write<T>(string name, T value)
    {
        string path = Path.Combine(DirectoryPath, name), temp = path + ".tmp";
        // 序列化和写入期间保留旧文件。临时文件写完才替换，避免下次启动读到只写了一半的设置或恢复记录。
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error?.Invoke(UiText.Get("SaveFailed") + e.Message); }
    }
}
