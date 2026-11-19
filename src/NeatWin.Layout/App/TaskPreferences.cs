using System.Text.Json;
using NeatWin.Core;

namespace NeatWin.App;

internal sealed record TaskPreferences(TaskLayoutProfile Profile, ViewingCalibration? Calibration = null)
{
    internal static TaskPreferences Load(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<TaskPreferences>(File.ReadAllText(path));
            return value is null ? new TaskPreferences(new TaskLayoutProfile()) : value with { Profile = (value.Profile ?? new TaskLayoutProfile()).Normalize() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new TaskPreferences(new TaskLayoutProfile()); }
    }
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this with { Profile = Profile.Normalize() }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
