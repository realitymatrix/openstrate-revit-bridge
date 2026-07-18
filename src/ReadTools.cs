using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace OpenStrateBridge;

/// <summary>
/// Read-only tools over the active Revit document. Every method here runs on
/// the Revit UI thread (invoked via RevitDispatcher) and mutates nothing.
/// Element identity in all outputs is UniqueId, never ElementId.
/// </summary>
public static class ReadTools
{
    public static JsonNode ToolCatalog() => new JsonArray(
        Tool("open_host", "Ensure an active host project exists (creates + activates one if needed)."),
        Tool("ingest", "Fetch the latest IFC from the OpenStrate service and link it into the host project."),
        Tool("model_stats", "Element census of the active document: counts by category and by level."),
        Tool("query_elements", "List elements. Args: category (e.g. 'Walls', optional), name_contains (optional), limit (default 50)."),
        Tool("get_element", "Full parameter dump for one element. Args: unique_id."));

    private static JsonNode Tool(string name, string description) =>
        new JsonObject { ["name"] = name, ["description"] = description };

    public static object Dispatch(string tool, JsonElement args, UIApplication app)
    {
        // Session-control tools do not need an existing document.
        switch (tool)
        {
            case "open_host": return BridgeCore.OpenHost(app);
            case "ingest": return BridgeCore.Ingest(app);
        }

        // Prefer the ingested IFC document: OpenIFCDocument opens it in the
        // background with no window, so ActiveUIDocument still points at
        // whatever project the user has focused (e.g. a blank template).
        Document doc = (BridgeSession.LastIngestedDoc is { IsValidObject: true } ingested
                ? ingested
                : app.ActiveUIDocument?.Document)
            ?? throw new InvalidOperationException("No document available. Run OpenStrate: Ingest Scan first.");
        return tool switch
        {
            "model_stats" => ModelStats(doc),
            "query_elements" => QueryElements(doc, args),
            "get_element" => GetElement(doc, args),
            _ => throw new InvalidOperationException($"Unknown tool '{tool}'."),
        };
    }

    private static object ModelStats(Document doc)
    {
        var elements = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .Where(e => e.Category is not null)
            .ToList();

        return new
        {
            document = doc.Title,
            total_elements = elements.Count,
            by_category = elements.GroupBy(e => e.Category!.Name)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count()),
            by_level = elements
                .Select(e => (doc.GetElement(e.LevelId) as Level)?.Name ?? "(none)")
                .GroupBy(n => n)
                .ToDictionary(g => g.Key, g => g.Count()),
        };
    }

    private static object QueryElements(Document doc, JsonElement args)
    {
        string? category = args.TryGetProperty("category", out var c) ? c.GetString() : null;
        string? nameContains = args.TryGetProperty("name_contains", out var n) ? n.GetString() : null;
        int limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 50;

        IEnumerable<Element> q = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .Where(e => e.Category is not null);

        if (!string.IsNullOrEmpty(category))
            q = q.Where(e => string.Equals(e.Category!.Name, category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(nameContains))
            q = q.Where(e => e.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

        return q.Take(Math.Clamp(limit, 1, 500)).Select(e => new
        {
            unique_id = e.UniqueId,
            category = e.Category!.Name,
            name = e.Name,
            level = (doc.GetElement(e.LevelId) as Level)?.Name,
        }).ToList();
    }

    private static object GetElement(Document doc, JsonElement args)
    {
        string uid = args.GetProperty("unique_id").GetString()
            ?? throw new InvalidOperationException("unique_id is required.");
        Element e = doc.GetElement(uid)
            ?? throw new InvalidOperationException($"No element with UniqueId {uid}.");

        return new
        {
            unique_id = e.UniqueId,
            category = e.Category?.Name,
            name = e.Name,
            // IFC-imported elements can carry DUPLICATE parameter display names
            // (built-in vs shared): group and keep the first non-empty value.
            parameters = e.Parameters.Cast<Parameter>()
                .Where(p => p.HasValue)
                .GroupBy(p => p.Definition.Name)
                .OrderBy(g => g.Key)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(p => p.StorageType switch
                    {
                        StorageType.String => (object?)p.AsString(),
                        StorageType.Integer => p.AsInteger(),
                        StorageType.Double => p.AsDouble(),   // internal units (feet) -- documented
                        StorageType.ElementId => p.AsValueString(),
                        _ => p.AsValueString(),
                    }).FirstOrDefault(v => v is not null)),
            units_note = "Double values are Revit internal units (feet).",
        };
    }
}
