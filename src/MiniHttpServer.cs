using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenStrateBridge;

/// <summary>
/// Minimal HTTP/1.1 server over TcpListener, bound to 127.0.0.1 only.
/// Raw sockets instead of HttpListener so no admin URL-ACL is needed.
/// Endpoints:
///   GET  /tools  -> tool catalog
///   POST /call   -> { "tool": "...", "args": { ... } }
/// Requests execute inside Revit via RevitDispatcher (ExternalEvent).
/// </summary>
public sealed class MiniHttpServer
{
    public const int Port = 8090;

    private readonly RevitDispatcher _dispatcher;
    private TcpListener? _listener;
    private volatile bool _running;

    public MiniHttpServer(RevitDispatcher dispatcher) => _dispatcher = dispatcher;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _running = true;
        _ = Task.Run(AcceptLoop);
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { /* shutdown */ }
    }

    private async Task AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);

            string? requestLine = await reader.ReadLineAsync();
            if (requestLine is null) return;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            string method = parts[0], path = parts[1];

            int contentLength = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line.Split(':')[1].Trim());

            string body = "";
            if (contentLength > 0)
            {
                var buf = new char[Math.Min(contentLength, 1 << 20)];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = await reader.ReadAsync(buf, read, buf.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                body = new string(buf, 0, read);
            }

            (int status, string json) = await Route(method, path, body);
            var payload = Encoding.UTF8.GetBytes(json);
            var header = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {payload.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
        }
    }

    private async Task<(int, string)> Route(string method, string path, string body)
    {
        try
        {
            if (method == "GET" && path == "/tools")
                return (200, ReadTools.ToolCatalog().ToJsonString());

            if (method == "POST" && path == "/call")
            {
                using var docJson = JsonDocument.Parse(body);
                string tool = docJson.RootElement.GetProperty("tool").GetString()
                    ?? throw new InvalidOperationException("'tool' is required.");
                JsonElement args = docJson.RootElement.TryGetProperty("args", out var a)
                    ? a.Clone()
                    : JsonDocument.Parse("{}").RootElement.Clone();

                // The bridge: hop from this socket thread onto Revit's UI thread.
                // Heavy tools (IFC conversion) legitimately run for minutes.
                var budget = tool is "ingest" or "open_host"
                    ? TimeSpan.FromMinutes(15)
                    : TimeSpan.FromSeconds(60);
                object result = await _dispatcher.Run(app => ReadTools.Dispatch(tool, args, app), budget);
                return (200, JsonSerializer.Serialize(new { ok = true, result }));
            }

            return (404, """{"ok":false,"error":"Not found. Use GET /tools or POST /call."}""");
        }
        catch (Exception ex)
        {
            return (500, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }
}
