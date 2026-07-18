using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace OpenStrateBridge;

/// <summary>Ribbon wrapper around BridgeCore.Ingest (same logic the HTTP bridge exposes).</summary>
[Transaction(TransactionMode.Manual)]
public class IngestCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            dynamic r = BridgeCore.Ingest(data.Application);
            TaskDialog.Show("OpenStrate Ingest",
                $"Fetched and LINKED:\n{r.ifc_path}\n\n" +
                $"Elements visible to Revit: {r.elements_visible}\n\n" +
                "The scan is now visible in this project's views.\n" +
                "Run \"OpenStrate: Audit vs Manifest\" to diff against the pipeline manifest.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

/// <summary>Per-session bridge state shared between commands (Revit API is single-threaded, so this is safe).</summary>
public static class BridgeSession
{
    public static string? LastIngestedPath { get; set; }
    public static Document? LastIngestedDoc { get; set; }
}
