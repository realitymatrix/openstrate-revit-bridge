using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OpenStrateBridge;

/// <summary>
/// Thin HTTP client for the OpenStrate scan-to-BIM service (ifc_cloud_viz.py).
/// All network I/O happens here, and callers must finish with it BEFORE opening
/// a Revit Transaction: no network inside a transaction, ever.
/// </summary>
public sealed class OpenStrateClient : IDisposable
{
    public const string DefaultBaseUrl = "http://192.168.7.182:8012";

    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public OpenStrateClient(string? baseUrl = null)
    {
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>GET /download -> save the current IFC deliverable to a temp file, return its path.</summary>
    public string DownloadIfc()
    {
        var bytes = _http.GetByteArrayAsync($"{BaseUrl}/download").GetAwaiter().GetResult();
        var path = Path.Combine(Path.GetTempPath(), "openstrate",
            $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.ifc");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// GET /ifc.json -> the pipeline's own element manifest: the authoritative
    /// source of truth the Revit-side audit diffs against.
    /// </summary>
    public JsonDocument GetManifest()
    {
        var json = _http.GetStringAsync($"{BaseUrl}/ifc.json").GetAwaiter().GetResult();
        return JsonDocument.Parse(json);
    }

    /// <summary>POST /relabel -- round-trip: push a label correction back into the pipeline (stretch goal).</summary>
    public string Relabel(object payload)
    {
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = _http.PostAsync($"{BaseUrl}/relabel", content).GetAwaiter().GetResult();
        return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _http.Dispose();
}
