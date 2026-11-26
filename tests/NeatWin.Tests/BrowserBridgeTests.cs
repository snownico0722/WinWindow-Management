using System.Buffers.Binary;
using System.Text.Json;
using NeatWin.App;
using NeatWin.Core;

namespace NeatWin.Tests;

public class BrowserBridgeTests
{
    [Fact]
    public async Task NativeMessagingRelaysOnlyRegisteredOriginsAndFreshSamples()
    {
        string layout = Path.Combine(Path.GetTempPath(), "winwindow-browser-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(layout, "BrowserBridge");
        Directory.CreateDirectory(directory);
        const string origin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop/";
        try
        {
            File.WriteAllText(Path.Combine(directory, "native-host.json"), "{\"allowed_origins\":[\"" + origin + "\"]}");
            using var bridge = BrowserBridge.TryStart(layout);
            Assert.NotNull(bridge);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sample = new BrowserExtensionSample(1, now, Guid.NewGuid(), 3, 7, new string('a', 64), null,
                10, 20, 1000, 700, true, false, true, 2, null, false, new(0, 0, 1, 1), true);
            async Task<string?> Probe(string command)
            {
                using var request = Frame(command);
                using var reply = new MemoryStream();
                Assert.Equal(0, await BrowserBridge.RunNativeHostAsync(origin, directory, request, reply));
                byte[] bytes = reply.ToArray();
                Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(bytes));
                return JsonSerializer.Deserialize<string>(bytes.AsSpan(4));
            }
            Assert.Equal("idle", await Probe("poll"));
            Assert.Equal("sample", await Probe("changed"));
            using var input = Frame(sample);
            Assert.Equal(1, await BrowserBridge.RunNativeHostAsync("chrome-extension://unauthorized/", directory, input, Stream.Null));
            Assert.Equal(0, input.Position);
            Assert.Equal(0, await BrowserBridge.RunNativeHostAsync(origin, directory, input, Stream.Null));
            for (int attempt = 0; attempt < 20 && bridge.Read(now).Length == 0; attempt++) await Task.Delay(20);
            Assert.Equal(sample, Assert.Single(bridge.Read(now)));
            Assert.Empty(bridge.Read(now + 1_501));
            Assert.Equal("sample", await Probe("poll"));
            bridge.Dispose();
            Assert.Equal("idle", await Probe("changed"));
        }
        finally { Directory.Delete(layout, recursive: true); }
    }

    [Fact]
    public async Task ReceiverProcessesNextPacketAfterPeerResetButStopsOnBrokenSocket()
    {
        int calls = 0, delivered = 0;
        Task ReceiveOne()
        {
            switch (++calls)
            {
                case 1: throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset);
                case 2: delivered++; return Task.CompletedTask;
                case 3: throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NotSocket);
                default: throw new InvalidOperationException("Retried a terminal error.");
            }
        }
        await BrowserBridge.ReceiveUntilClosedAsync(ReceiveOne);
        Assert.Equal(1, delivered);
        Assert.Equal(3, calls);
    }

    private static MemoryStream Frame<T>(T sample)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(sample, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        byte[] message = new byte[sizeof(uint) + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)json.Length);
        json.CopyTo(message, sizeof(uint));
        return new(message);
    }
}
