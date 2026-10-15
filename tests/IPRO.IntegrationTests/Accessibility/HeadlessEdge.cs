using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace IPRO.IntegrationTests.Accessibility;

// 578: Microsoft Edge without a window, driven over the DevTools protocol with nothing but a web
// socket, so the accessibility checks need no new package and no network. Every host name is made
// unresolvable (--host-resolver-rules), so a page can only load what the test wrote to disk.
internal sealed class HeadlessEdge : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _profile;
    private readonly ClientWebSocket _socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _reader = Task.CompletedTask;
    private string _session = string.Empty;
    private int _nextId;

    private HeadlessEdge(Process process, string profile, ClientWebSocket socket)
    {
        _process = process;
        _profile = profile;
        _socket = socket;
    }

    private void StartReading() => _reader = Task.Run(ReadLoopAsync);

    public static string? FindEdge()
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static async Task<HeadlessEdge> StartAsync()
    {
        var edge = FindEdge() ?? throw new InvalidOperationException(
            "Microsoft Edge was not found; the public-site accessibility checks drive it without a window.");
        var profile = Path.Combine(Path.GetTempPath(), "ipro-a11y-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(edge)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in new[]
        {
            "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--disable-background-networking", "--disable-sync",
            "--disable-component-update", "--allow-file-access-from-files",
            "--host-resolver-rules=MAP * ~NOTFOUND", "--remote-debugging-port=0",
            "--user-data-dir=" + profile, "about:blank"
        })
        {
            start.ArgumentList.Add(arg);
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException("Edge did not start.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        HeadlessEdge? browser = null;
        try
        {
            // Edge writes the port it chose, and the browser's socket path, once it listens.
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            string[] lines = Array.Empty<string>();
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(portFile))
                {
                    try { lines = File.ReadAllLines(portFile); } catch (IOException) { }
                    if (lines.Length >= 2) break;
                }
                await Task.Delay(100);
            }
            if (lines.Length < 2) throw new InvalidOperationException("Edge did not open its DevTools port within 30 seconds.");

            var socket = new ClientWebSocket();
            browser = new HeadlessEdge(process, profile, socket);
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{lines[0].Trim()}{lines[1].Trim()}"), CancellationToken.None);
            browser.StartReading();
            var target = await browser.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, browserLevel: true);
            var attached = await browser.SendAsync("Target.attachToTarget",
                new JsonObject { ["targetId"] = target!["targetId"]!.GetValue<string>(), ["flatten"] = true }, browserLevel: true);
            browser._session = attached!["sessionId"]!.GetValue<string>();
            await browser.SendAsync("Page.enable");
            await browser.SendAsync("Runtime.enable");
            return browser;
        }
        catch
        {
            // Never leave a browser with an open DevTools port, or its profile, behind a failed start.
            if (browser != null) await browser.DisposeAsync();
            else { Stop(process); DeleteFolder(profile); }
            throw;
        }
    }

    public async Task<JsonNode?> SendAsync(string method, JsonObject? parameters = null, bool browserLevel = false)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        if (!browserLevel) message["sessionId"] = _session;
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        var finished = await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (finished != reply.Task) throw new TimeoutException($"Edge did not answer {method} within 60 seconds.");
        return await reply.Task;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var node = JsonNode.Parse(message.ToArray());
                message.SetLength(0);
                if (node?["id"] is JsonValue idValue && _pending.TryRemove(idValue.GetValue<int>(), out var reply))
                {
                    if (node["error"] is JsonNode error) reply.TrySetException(new InvalidOperationException("DevTools: " + error.ToJsonString()));
                    else reply.TrySetResult(node["result"]);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        foreach (var waiting in _pending.Values) waiting.TrySetException(new InvalidOperationException("Edge closed its DevTools connection."));
    }

    public Task SetViewportAsync(int width, int height) => SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject
    {
        ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = 1, ["mobile"] = width < 768
    });

    /// <summary>Opens a page from disk and waits until it has loaded.</summary>
    public async Task OpenAsync(string file)
    {
        await SendAsync("Page.navigate", new JsonObject { ["url"] = new Uri(file).AbsoluteUri });
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await EvaluateAsync<string>("document.readyState") == "complete"
                && await EvaluateAsync<string>("location.href") == new Uri(file).AbsoluteUri) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The page did not finish loading: " + file);
    }

    public async Task<T?> EvaluateAsync<T>(string expression)
    {
        var result = await SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true
        });
        if (result?["exceptionDetails"] is JsonNode failed)
        {
            throw new InvalidOperationException("Script failed in the page: " + failed.ToJsonString());
        }
        var value = result?["result"]?["value"];
        return value == null ? default : value.Deserialize<T>();
    }

    /// <summary>One key press, as a keyboard user makes it (Tab, Shift+Tab, Enter, Space, Escape).</summary>
    public async Task PressAsync(string key, bool shift = false)
    {
        var (code, keyCode, text) = key switch
        {
            "Tab" => ("Tab", 9, (string?)null),
            "Enter" => ("Enter", 13, "\r"),
            " " => ("Space", 32, " "),
            "Escape" => ("Escape", 27, null),
            "ArrowDown" => ("ArrowDown", 40, null),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "not a key these tests press")
        };
        var down = new JsonObject
        {
            ["type"] = text == null ? "rawKeyDown" : "keyDown", ["key"] = key, ["code"] = code,
            ["windowsVirtualKeyCode"] = keyCode, ["nativeVirtualKeyCode"] = keyCode, ["modifiers"] = shift ? 8 : 0
        };
        if (text != null) { down["text"] = text; down["unmodifiedText"] = text; }
        await SendAsync("Input.dispatchKeyEvent", down);
        await SendAsync("Input.dispatchKeyEvent", new JsonObject
        {
            ["type"] = "keyUp", ["key"] = key, ["code"] = code,
            ["windowsVirtualKeyCode"] = keyCode, ["nativeVirtualKeyCode"] = keyCode, ["modifiers"] = shift ? 8 : 0
        });
        await Task.Delay(30);
    }

    public async Task MoveMouseAsync(double x, double y)
    {
        await SendAsync("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y });
        await Task.Delay(50);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        try { await _reader; } catch { }
        Stop(_process);
        _socket.Dispose();
        DeleteFolder(_profile);
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { process.WaitForExit(10000); } catch { }
    }

    /// <summary>Deletes a folder this harness made, trying again while Edge lets go of its files.</summary>
    internal static void DeleteFolder(string folder)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); return; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}
