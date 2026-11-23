using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Automation;
using Accessibility;
using NeatWin.Core;
using NeatWin.Windows;

namespace NeatWin.Tests;

[CollectionDefinition("Browser desktop", DisableParallelization = true)]
public sealed class BrowserDesktopCollection;

[Collection("Browser desktop")]
public sealed class BrowserAccessibilityTests
{
    [Theory]
    [InlineData("Google/Chrome/Application/chrome.exe")]
    [InlineData("Microsoft/Edge/Application/msedge.exe")]
    public async Task SameCaptionNavigationUpdatesNativeDocumentIdentity(string browser)
    {
        var executable = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData }.Select(root => Path.Combine(Environment.GetFolderPath(root), browser))
            .FirstOrDefault(File.Exists);
        if (executable == null)
        {
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", $"Browser missing on Windows CI: {browser}");
            return;
        }

        string id = Guid.NewGuid().ToString("N"), title = "WinWindow fixture " + id;
        string profile = Path.Combine(Path.GetTempPath(), "ww-browser-" + id);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        using var stop = new CancellationTokenSource();
        int navigate = 0;
        var serving = ServePages();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
            "--disable-sync", "--disable-background-networking", "--new-window", origin + "/a" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            var first = await WaitForPage("/a");
            Volatile.Write(ref navigate, 1);
            var second = await WaitForPage("/b");
            Assert.Equal(first.Target, second.Target);
            Assert.NotEqual(first.UrlKey, second.UrlKey);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (OperationCanceledException) { }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }

        async Task<AccessibleBrowserPage> WaitForPage(string path)
        {
            var timeout = Stopwatch.StartNew();
            string? lastCaption = null;
            nint renderer = 0;
            while (timeout.Elapsed < TimeSpan.FromSeconds(18))
            {
                BrowserWindowTarget? target = null;
                NativeMethods.EnumWindows((handle, _) =>
                {
                    if (BrowserAccessibility.Target(handle) is { } candidate && candidate.Caption.Contains(title))
                        target = candidate;
                    return target == null;
                }, 0);
                if (target != null)
                {
                    renderer = BrowserAccessibility.FindViewport(target.Handle).Renderer;
                    lastCaption = target.Caption;
                    var page = await Task.Run(() => BrowserAccessibility.Read(target, NativeMethods.GetForegroundWindow()))
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    if (page is { UrlKey: not null } && page.UrlKey == BrowserPageEvidence.UrlKey(origin + path))
                    {
                        if (!page.Reading.IsEmpty && (NativeMethods.GetForegroundWindow() != target.Handle || page.Editing)) return page;
                        lastCaption += $" (document matched, reading={page.Reading}, editing={page.Editing})";
                    }
                }
                await Task.Delay(150);
            }
            var state = renderer == 0 ? "missing content viewport" : await Task.Run(() => Describe(renderer)).WaitAsync(TimeSpan.FromSeconds(5));
            throw new TimeoutException($"{browser} did not expose document {path}. Window: {lastCaption ?? "not found"}, {state}");
        }

        static string Describe(nint renderer)
        {
            var status = new StringBuilder();
            try
            {
                var iid = typeof(IAccessible).GUID;
                var hr = NativeMethods.AccessibleObjectFromWindow(renderer, 0xfffffffc, ref iid, out var root);
                status.Append($"MSAA={hr:X}, role={root?.get_accRole(0)}, children={root?.accChildCount}, hasURL={BrowserPageEvidence.UrlKey(root?.get_accValue(0)) != null}. ");
            }
            catch (Exception ex) { status.Append($"MSAA {ex.GetType().Name}/{ex.HResult:X}. "); }
            try
            {
                var root = AutomationElement.FromHandle(renderer);
                var doc = root.Current.ControlType == ControlType.Document ? root : root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                status.Append($"UIA={root.Current.ControlType.ProgrammaticName}, document={doc != null}, value={doc?.GetCurrentPropertyValue(ValuePattern.ValueProperty)}");
            }
            catch (Exception ex) { status.Append($"UIA {ex.GetType().Name}/{ex.HResult:X}."); }
            return status.ToString();
        }

        async Task ServePages()
        {
            var connections = new List<Task>();
            try
            {
                while (!stop.IsCancellationRequested)
                    connections.Add(Respond(await listener.AcceptTcpClientAsync(stop.Token)));
            }
            finally { await Task.WhenAll(connections); }
        }

        async Task Respond(TcpClient client)
        {
            using (client)
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                string? request = await reader.ReadLineAsync(stop.Token);
                if (request == null) return;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                string body = request.StartsWith("GET /phase ") ? Volatile.Read(ref navigate).ToString() :
                    $"<title>{title}</title><article><p>Reading a sufficiently long line inside a real browser window.</p>" +
                    "<p>A second line of visible reference material for the Windows fixture.</p></article><textarea autofocus></textarea>" +
                    "<script>if(location.pathname=='/a')setInterval(async()=>{if(await(await fetch('/phase')).text()=='1')location.href='/b'},250)</script>";
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(bytes, stop.Token);
            }
            catch (IOException) { /* Browser abandoned this speculative connection. */ }
        }
    }
}
