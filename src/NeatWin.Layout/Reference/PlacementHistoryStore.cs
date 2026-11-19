using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeatWin.Core;

namespace NeatWin.Reference;

// Only the recorder that owns the directory mutex creates keys or updates examples.
internal sealed class PlacementHistoryStore(string directory)
{
    private string HistoryPath => Path.Combine(directory, "placement-v1.json");
    private string KeyPath => Path.Combine(directory, "identity.key");
    private byte[]? _key;
    private LayoutHabit[]? _samples;

    internal string? Key(string identity, bool create)
    {
        if (_key == null)
        {
            if (!File.Exists(KeyPath))
            {
                if (!create) return null;
                Directory.CreateDirectory(directory);
                var key = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(KeyPath + ".tmp", key);
                File.Move(KeyPath + ".tmp", KeyPath, true);
            }
            _key = File.ReadAllBytes(KeyPath);
        }
        if (_key.Length != 32) return null;
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(identity)))[..24];
    }

    internal LayoutHabit[] Read()
    {
        if (_samples != null) return _samples;
        try
        {
            if (File.Exists(HistoryPath) && new FileInfo(HistoryPath).Length <= 512 * 1024)
                _samples = JsonSerializer.Deserialize<LayoutHabit[]>(File.ReadAllText(HistoryPath))?
                    .Where(s => s != null && double.IsFinite(s.Center) && double.IsFinite(s.Width) &&
                        s.Center is >= 0 and <= 1 && s.Width is > 0 and <= 1 && s.RevealedCell is >= -1 and < 16)
                    .TakeLast(1024).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return _samples ??= [];
    }

    internal void Learn(WindowAdjustmentObservation observation)
    {
        if (LayoutHabits.Observe(observation) is not { } edit) return;
        var next = LayoutHabits.Add(Read(), edit);
        Directory.CreateDirectory(directory);
        File.WriteAllText(HistoryPath + ".tmp", JsonSerializer.Serialize(next));
        File.Move(HistoryPath + ".tmp", HistoryPath, true);
        _samples = next;
    }

    internal void Clear()
    {
        _key = null; _samples = null;
        foreach (var p in new[] { HistoryPath, KeyPath }) { File.Delete(p); File.Delete(p + ".tmp"); }
    }
}
