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
    private long _lastDemand = -10_000;
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
        _ = ReceiveUntilClosedAsync(ReceiveOneAsync);
    }

    internal BrowserExtensionSample[] Read(long now)
    {
        // 短时间内可能还有真实的整理请求。让扩展预热下一轮，后台空闲后停止测量页面。
        Volatile.Write(ref _lastDemand, Environment.TickCount64);
        lock (_gate) return _latest.Values.Where(sample => sample.IsFresh(now)).ToArray();
    }

    internal static async Task ReceiveUntilClosedAsync(Func<Task> receiveOne)
    {
        while (true)
        {
            try { await receiveOne().ConfigureAwait(false); }
            // 旧中继关闭端口后，Windows 可能在下一次收包时交付上次回包的 ICMP 错误。
            // 它只属于那个数据报；已释放或其他不可恢复的套接字仍结束，不循环重试。
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused) { }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { return; }
        }
    }

    private async Task ReceiveOneAsync()
    {
        var datagram = await _socket.ReceiveAsync().ConfigureAwait(false);
        byte[] packet = datagram.Buffer;
        if (packet.Length <= TokenLength || packet.Length > TokenLength + MaxMessage ||
            !CryptographicOperations.FixedTimeEquals(_token, packet.AsSpan(0, TokenLength))) return;
        if (packet.Length == TokenLength + 1 && packet[TokenLength] <= 1)
        {
            bool wanted = packet[TokenLength] == 1 || Environment.TickCount64 - Volatile.Read(ref _lastDemand) <= 2500;
            await _socket.SendAsync(new byte[] { wanted ? (byte)1 : (byte)0 }, datagram.RemoteEndPoint).ConfigureAwait(false);
            return;
        }
        BrowserExtensionSample? sample;
        try { sample = JsonSerializer.Deserialize<BrowserExtensionSample>(packet.AsSpan(TokenLength), Json); }
        catch (JsonException) { return; }
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (sample == null || !sample.IsFresh(now)) return;
        lock (_gate)
        {
            var key = (sample.Session, sample.WindowId);
            if (_latest.TryGetValue(key, out var old) && old.SampledAt > sample.SampledAt) return;
            if (_latest.Count >= 32)
                foreach (var stale in _latest.Where(pair => !pair.Value.IsFresh(now)).Select(pair => pair.Key).ToArray())
                    _latest.Remove(stale);
            if (_latest.Count < 32 || _latest.ContainsKey(key)) _latest[key] = sample;
        }
    }

    internal static async Task<int> RunNativeHostAsync(string origin, string directory, Stream input, Stream output)
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
                // Chromium 的轻量探针只询问桌面实例。连接中继本身不代表 WinWindow 正在运行。
                var body = packet.AsMemory(TokenLength, (int)length);
                bool changed = body.Span.SequenceEqual("\"changed\""u8);
                if (changed || body.Span.SequenceEqual("\"poll\""u8))
                {
                    bool wanted = await WantsSampleAsync(directory, changed).ConfigureAwait(false);
                    var reply = JsonSerializer.SerializeToUtf8Bytes(wanted ? "sample" : "idle");
                    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)reply.Length);
                    await output.WriteAsync(header).ConfigureAwait(false);
                    await output.WriteAsync(reply).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                    continue;
                }
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

    private static async Task<bool> WantsSampleAsync(string directory, bool changed)
    {
        if (ReadEndpoint(directory) is not { } endpoint) return false;
        using var probe = new UdpClient(AddressFamily.InterNetwork);
        probe.Connect(IPAddress.Loopback, endpoint.Port);
        var message = new byte[TokenLength + 1];
        Convert.FromHexString(endpoint.Token).CopyTo(message, 0);
        message[^1] = changed ? (byte)1 : (byte)0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await probe.SendAsync(message, timeout.Token).ConfigureAwait(false);
            var reply = await probe.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return reply.Buffer is [1];
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { return false; }
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
