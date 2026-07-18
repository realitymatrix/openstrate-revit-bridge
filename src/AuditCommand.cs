using System.IO;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace OpenStrateBridge;

/// <summary>
/// Diffs what Revit actually ingested against the OpenStrate pipeline's own
/// element manifest (/ifc.json) -- the authoritative source of truth. Flags
/// IFC classes the pipeline emitted that Revit dropped or re-categorized, and
/// count mismatches for walls and editable objects. Read-only: no transaction.
/// Elements are keyed by UniqueId (stable across sessions), never ElementId.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class AuditCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            Document? doc = BridgeSession.LastIngestedDoc ?? data.Application.ActiveUIDocument?.Document;
            if (doc is null)
            {
                message = "No document. Run \"OpenStrate: Ingest Scan\" first.";
                return Result.Failed;
            }

            // --- Source of truth: the pipeline manifest (network BEFORE any doc work) ---
            JsonDocument manifest;
            using (var client = new OpenStrateClient())
            {
                manifest = client.GetManifest();
            }

            var root = manifest.RootElement;
            var pipelineClasses = root.GetProperty("classes").EnumerateObject()
                .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int pipelineWalls = root.TryGetProperty("walls_edit", out var w) ? w.GetArrayLength() : 0;
            int pipelineObjects = root.TryGetProperty("edit_objects", out var eo) ? eo.GetArrayLength() : 0;

            // --- What Revit actually sees ---
            var revitElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e.Category is not null)
                .ToList();

            var revitByCategory = revitElements
                .GroupBy(e => e.Category!.Name)
                .ToDictionary(g => g.Key, g => g.Count());

            // IFC classes ingested by Revit surface via the "IFC Export Class"/"IfcExportAs"
            // parameter on open-IFC documents; fall back to category mapping when absent.
            var revitIfcClasses = revitElements
                .Select(e => e.LookupParameter("Export to IFC As")?.AsString()
                          ?? e.LookupParameter("IfcExportAs")?.AsString()
                          ?? MapCategoryToIfc(e.Category!.Name))
                .Where(c => c is not null)
                .GroupBy(c => c!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            int revitWalls = revitByCategory.TryGetValue("Walls", out var rw) ? rw : 0;

            // --- Diff ---
            var droppedClasses = pipelineClasses
                .Where(c => !revitIfcClasses.ContainsKey(c))
                .OrderBy(c => c).ToList();

            var audit = new
            {
                at = DateTime.UtcNow.ToString("o"),
                source = OpenStrateClient.DefaultBaseUrl,
                ifc_path = BridgeSession.LastIngestedPath,
                pipeline = new { classes = pipelineClasses.OrderBy(c => c), walls = pipelineWalls, edit_objects = pipelineObjects },
                revit = new { by_category = revitByCategory, by_ifc_class = revitIfcClasses, walls = revitWalls, total = revitElements.Count },
                findings = new
                {
                    dropped_ifc_classes = droppedClasses,
                    wall_count_match = revitWalls == pipelineWalls,
                    wall_delta = revitWalls - pipelineWalls,
                },
                element_index = revitElements.Take(500).Select(e => new
                {
                    unique_id = e.UniqueId,           // stable identity -- never ElementId
                    category = e.Category!.Name,
                    name = e.Name,
                }),
            };

            var auditPath = Path.Combine(Path.GetTempPath(), "openstrate",
                $"revit_audit_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            File.WriteAllText(auditPath, JsonSerializer.Serialize(audit,
                new JsonSerializerOptions { WriteIndented = true }));

            TaskDialog.Show("OpenStrate Audit",
                $"Pipeline manifest: {pipelineClasses.Count} IFC classes, {pipelineWalls} walls, {pipelineObjects} objects\n" +
                $"Revit ingested:   {revitElements.Count} elements, {revitWalls} walls\n\n" +
                (droppedClasses.Count == 0
                    ? "No pipeline classes were dropped by Revit."
                    : $"DROPPED by Revit: {string.Join(", ", droppedClasses)}") + "\n\n" +
                $"Full audit: {auditPath}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    /// <summary>Fallback mapping for open-IFC documents that do not carry export-class parameters.</summary>
    private static string? MapCategoryToIfc(string category) => category switch
    {
        "Walls" => "IfcWall",
        "Floors" => "IfcSlab",
        "Ceilings" => "IfcCovering",
        "Furniture" => "IfcFurniture",
        "Doors" => "IfcDoor",
        "Windows" => "IfcWindow",
        "Generic Models" => "IfcBuildingElementProxy",
        _ => null,
    };
}
