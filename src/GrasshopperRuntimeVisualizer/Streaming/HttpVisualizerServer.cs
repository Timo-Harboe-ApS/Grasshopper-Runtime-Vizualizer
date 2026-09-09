using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GrasshopperRuntimeVisualizer.Streaming;

public sealed class HttpVisualizerServer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string[] _browserRoots;
    private readonly string _sessionsRoot;
    private readonly Func<string?> _fallbackIndex;
    private readonly Func<Dictionary<string, object?>> _diagnostics;
    private readonly DebugLog _log;
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _debugMode;

    private HttpVisualizerServer(
        string[] browserRoots,
        string sessionsRoot,
        Func<string?> fallbackIndex,
        Func<Dictionary<string, object?>> diagnostics,
        DebugLog log,
        int port)
    {
        _browserRoots = browserRoots;
        _sessionsRoot = sessionsRoot;
        _fallbackIndex = fallbackIndex;
        _diagnostics = diagnostics;
        _log = log;
        Port = port;
        Url = $"http://127.0.0.1:{port}/";
        DebugUrl = $"{Url}debug";
        _listener = new HttpListener();
        _listener.Prefixes.Add(Url);
    }

    public int Port { get; }
    public string Url { get; }
    public string DebugUrl { get; }

    public static HttpVisualizerServer Start(
        string[] browserRoots,
        string sessionsRoot,
        Func<string?> fallbackIndex,
        Func<Dictionary<string, object?>> diagnostics,
        DebugLog log)
    {
        for (var port = 8787; port < 8800; port++)
        {
            try
            {
                var server = new HttpVisualizerServer(browserRoots, sessionsRoot, fallbackIndex, diagnostics, log, port);
                server._listener.Start();
                _ = Task.Run(server.AcceptLoop);
                return server;
            }
            catch (Exception ex)
            {
                log.Write($"Could not bind port {port}: {ex.Message}");
                // Try the next local port.
            }
        }

        throw new InvalidOperationException("Could not start the Runtime Visualizer local server on ports 8787-8799.");
    }

    public void SetDebugMode(bool enabled)
    {
        _debugMode = enabled;
    }

    public void Broadcast(Dictionary<string, object?> evt)
    {
        var json = JsonSerializer.Serialize(evt, JsonOptions);
        var payload = Encoding.UTF8.GetBytes(json);

        foreach (var (id, socket) in _sockets)
        {
            if (socket.State != WebSocketState.Open)
            {
                _sockets.TryRemove(id, out _);
                continue;
            }

            _ = Send(socket, payload, id);
        }
    }

    private async Task AcceptLoop()
    {
        while (!_shutdown.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => Handle(context));
            }
            catch
            {
                if (_listener.IsListening)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            var path = request.Url?.AbsolutePath ?? "/";
            ApplyNoCache(response);
            _log.Write($"HTTP {request.HttpMethod} {path}");

            if (path == "/ws" && request.IsWebSocketRequest)
            {
                var ws = await context.AcceptWebSocketAsync(null);
                var id = Guid.NewGuid();
                _sockets[id] = ws.WebSocket;
                await DrainSocket(id, ws.WebSocket);
                return;
            }

            if (path == "/debug")
            {
                await WriteDebugPage(response);
                return;
            }

            if (path == "/api/debug")
            {
                await WriteJson(response, _diagnostics());
                return;
            }

            if (path == "/api/session/latest")
            {
                await WriteLatestSession(response);
                return;
            }

            if (path.StartsWith("/sessions/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSessionFile(path, response);
                return;
            }

            await WriteBrowserFile(path == "/" ? "/index.html" : path, response);
        }
        catch (Exception ex)
        {
            _log.Write($"HTTP error: {ex}");
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = 500;
                var payload = Encoding.UTF8.GetBytes(ex.Message);
                await context.Response.OutputStream.WriteAsync(payload);
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task WriteLatestSession(HttpListenerResponse response)
    {
        var latest = Directory.Exists(_sessionsRoot)
            ? Directory.EnumerateDirectories(_sessionsRoot).OrderByDescending(Path.GetFileName).FirstOrDefault()
            : null;

        if (latest is null)
        {
            await WriteJson(response, new { session = (string?)null });
            return;
        }

        var name = Path.GetFileName(latest);
        await WriteJson(response, new
        {
            session = name,
            metadataUrl = $"/sessions/{Uri.EscapeDataString(name)}/metadata.json",
            graphUrl = $"/sessions/{Uri.EscapeDataString(name)}/graph.json",
            eventsUrl = $"/sessions/{Uri.EscapeDataString(name)}/events.jsonl"
        });
    }

    private async Task WriteSessionFile(string path, HttpListenerResponse response)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            response.StatusCode = 404;
            return;
        }

        var session = Uri.UnescapeDataString(parts[1]);
        var file = Uri.UnescapeDataString(parts[2]);
        if (session.Contains(Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar))
        {
            response.StatusCode = 400;
            return;
        }

        var fullPath = Path.Combine(_sessionsRoot, session, file);
        if (!File.Exists(fullPath))
        {
            response.StatusCode = 404;
            return;
        }

        response.ContentType = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/plain";
        await using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await stream.CopyToAsync(response.OutputStream);
    }

    private async Task WriteBrowserFile(string path, HttpListenerResponse response)
    {
        var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        foreach (var browserRoot in _browserRoots)
        {
            var fullPath = Path.GetFullPath(Path.Combine(browserRoot, relative));
            var root = Path.GetFullPath(browserRoot);

            if (!fullPath.StartsWith(root, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                continue;
            }

            response.ContentType = ContentType(fullPath);
            await using var stream = File.OpenRead(fullPath);
            await stream.CopyToAsync(response.OutputStream);
            return;
        }

        if (path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = _fallbackIndex();
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                response.ContentType = "text/html";
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(fallback));
                return;
            }
        }

        response.StatusCode = 404;
        response.ContentType = "text/html";
        var diagnosticLink = $"<p><a href=\"/debug\">Open Runtime Visualizer diagnostics</a></p>";
        var payload = Encoding.UTF8.GetBytes($"<!doctype html><title>Runtime Visualizer 404</title><h1>Runtime Visualizer file not found</h1><p>Requested: {WebUtility.HtmlEncode(path)}</p>{diagnosticLink}");
        await response.OutputStream.WriteAsync(payload);
        _log.Write($"404 for browser file '{path}'. Roots: {string.Join(" | ", _browserRoots)}");
    }

    private static async Task WriteJson(HttpListenerResponse response, object value)
    {
        response.ContentType = "application/json";
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        await response.OutputStream.WriteAsync(payload);
    }

    private static void ApplyNoCache(HttpListenerResponse response)
    {
        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Expires"] = "0";
    }

    private async Task WriteDebugPage(HttpListenerResponse response)
    {
        response.ContentType = "text/html";
        var diagnostics = _diagnostics();
        var rows = diagnostics
            .Select(pair => $"<tr><th>{WebUtility.HtmlEncode(pair.Key)}</th><td><pre>{WebUtility.HtmlEncode(FormatDiagnosticValue(pair.Value))}</pre></td></tr>");
        var html = """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Runtime Visualizer Debug</title>
              <style>
                body { margin: 24px; font: 13px/1.4 -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; color: #202124; }
                h1 { font-size: 20px; }
                table { border-collapse: collapse; width: 100%; }
                th, td { border: 1px solid #ddd; padding: 8px; vertical-align: top; text-align: left; }
                th { width: 220px; background: #f6f6f6; }
                pre { margin: 0; white-space: pre-wrap; word-break: break-word; }
                a { color: #1c7c78; }
              </style>
            </head>
            <body>
              <h1>Grasshopper Runtime Visualizer Debug</h1>
              <p><a href="/">Open visualizer</a> · <a href="/api/debug">Raw JSON</a></p>
              <table>
            """ + string.Join(Environment.NewLine, rows) + """
              </table>
            </body>
            </html>
            """;
        await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html));
    }

    private static string ContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            _ => "application/octet-stream"
        };
    }

    private static string FormatDiagnosticValue(object? value)
    {
        if (value is null)
        {
            return "";
        }

        if (value is string text)
        {
            return text;
        }

        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task DrainSocket(Guid id, WebSocket socket)
    {
        var buffer = new byte[256];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, _shutdown.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch
        {
            // Dropped browser connection.
        }
        finally
        {
            _sockets.TryRemove(id, out _);
            socket.Dispose();
        }
    }

    private async Task Send(WebSocket socket, byte[] payload, Guid id)
    {
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, _shutdown.Token);
        }
        catch
        {
            _sockets.TryRemove(id, out _);
        }
    }
}
