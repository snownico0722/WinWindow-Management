using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using NeatWin.Core;

namespace NeatWin.App;

// The browser launches the same executable at its own integrity level. A local, authenticated
// datagram keeps the relay independent of the desktop instance's optional UAC elevation.
internal sealed class BrowserBridge : IDisposable
{
    private const string ManifestFile = "native-host.json";
    private const string EndpointFile = "endpoint.json";
    private const int TokenLength = 32;
    private const int MaxMessage = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly UdpClient _socket;
    private readonly string _endpointPath;
    private readonly byte[] _token = RandomNumberGenerator.GetBytes(TokenLength);
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Session, int Window), BrowserExtensionSample> _latest = [];

    internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinWindow-Management", "Layout", "BrowserBridge");

    internal static bool IsConfigured(string layoutDirectory) => File.Exists(Path.Combine(layoutDirectory, "BrowserBridge", ManifestFile));

    internal static BrowserBridge? TryStart(string layoutDirectory)
    {
        if (!IsConfigured(layoutDirectory)) return null;
        try { return new BrowserBridge(Path.Combine(layoutDirectory, "BrowserBridge")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException) { return null; }
    }

    private BrowserBridge(string directory)
    {
        _endpointPath = Path.Combine(directory, EndpointFile);
        _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            var endpoint = new Endpoint(((IPEndPoint)_socket.Client.LocalEndPoint!).Port, Convert.ToHexString(_token));
            string pending = _endpointPath + ".tmp";
            File.WriteAllBytes(pending, JsonSerializer.SerializeToUtf8Bytes(endpoint, Json));
            File.Move(pending, _endpointPath, overwrite: true);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        _ = ReceiveAsync();
    }

    internal BrowserExtensionSample[] Read(long now)
    {
        lock (_gate) return _latest.Values.Where(sample => sample.IsFresh(now)).ToArray();
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (true)
            {
                var datagram = await _socket.ReceiveAsync().ConfigureAwait(false);
                byte[] packet = datagram.Buffer;
                if (packet.Length <= TokenLength || packet.Length > TokenLength + MaxMessage ||
                    !CryptographicOperations.FixedTimeEquals(_token, packet.AsSpan(0, TokenLength))) continue;
                BrowserExtensionSample? sample;
                try { sample = JsonSerializer.Deserialize<BrowserExtensionSample>(packet.AsSpan(TokenLength), Json); }
                catch (JsonException) { continue; }
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (sample == null || !sample.IsFresh(now)) continue;
                lock (_gate)
                {
                    var key = (sample.Session, sample.WindowId);
                    if (_latest.TryGetValue(key, out var old) && old.SampledAt > sample.SampledAt) continue;
                    if (_latest.Count >= 32)
                        foreach (var stale in _latest.Where(pair => !pair.Value.IsFresh(now)).Select(pair => pair.Key).ToArray())
                            _latest.Remove(stale);
                    if (_latest.Count < 32 || _latest.ContainsKey(key)) _latest[key] = sample;
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { }
    }

    internal static async Task<int> RunNativeHostAsync(string origin, string directory, Stream input)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, ManifestFile)));
            if (!manifest.RootElement.GetProperty("allowed_origins").EnumerateArray().Any(value => value.GetString() == origin)) return 1;
            using var relay = new UdpClient(AddressFamily.InterNetwork);
            var header = new byte[sizeof(uint)];
            var packet = new byte[TokenLength + MaxMessage];
            while (true)
            {
                await input.ReadExactlyAsync(header).ConfigureAwait(false);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (length is 0 or > MaxMessage) return 1;
                await input.ReadExactlyAsync(packet.AsMemory(TokenLength, (int)length)).ConfigureAwait(false);
                if (!File.Exists(Path.Combine(directory, ManifestFile))) return 0;
                if (ReadEndpoint(directory) is not { } endpoint) continue;
                Convert.FromHexString(endpoint.Token).CopyTo(packet, 0);
                await relay.SendAsync(packet.AsMemory(0, TokenLength + (int)length),
                    new IPEndPoint(IPAddress.Loopback, endpoint.Port)).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException) { return 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            KeyNotFoundException or JsonException or SocketException) { return 1; }
    }

    private static Endpoint? ReadEndpoint(string directory)
    {
        try
        {
            var endpoint = JsonSerializer.Deserialize<Endpoint>(File.ReadAllBytes(Path.Combine(directory, EndpointFile)), Json);
            return endpoint is { Port: > 0 and <= 65535, Token.Length: TokenLength * 2 } && endpoint.Token.All(Uri.IsHexDigit) ? endpoint : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Dispose()
    {
        _socket.Dispose();
        try { File.Delete(_endpointPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record Endpoint(int Port, string Token);
}
