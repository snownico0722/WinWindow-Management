using System.Text.Json;
using System.Text.Json.Serialization;
using NeatWin.Core;

namespace NeatWin.Reference;

// Read one bounded snapshot per call. A partially-written edit retains the last validated rules.
internal sealed class LayoutRuleFile(string directory)
{
    internal const string Name = "compiled-rules.json";
    internal static readonly JsonSerializerOptions Json = new()
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private byte[]? _lastBytes;
    private CompiledLayoutRules _active = CompiledLayoutRules.Default;

    internal CompiledLayoutRules Read()
    {
        try
        {
            using var input = new FileStream(Path.Combine(directory, Name), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (input.Length is <= 0 or > 65536) return _active;
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1 || bytes.AsSpan().SequenceEqual(_lastBytes)) return _active;
            using var reader = new StreamReader(new MemoryStream(bytes));
            var rules = JsonSerializer.Deserialize<CompiledLayoutRules>(reader.ReadToEnd(), Json);
            if (rules?.IsValid() == true) { _active = rules; _lastBytes = bytes; }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { _active = CompiledLayoutRules.Default; _lastBytes = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return _active;
    }

    internal void EnsureTemplate()
    {
        string path = Path.Combine(directory, Name);
        if (File.Exists(path)) return;
        string temporary = Path.Combine(directory, $"{Name}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, CompiledLayoutRules.Default, Json);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { File.Delete(temporary); }
    }
}
