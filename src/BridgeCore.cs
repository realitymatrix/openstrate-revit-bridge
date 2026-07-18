using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace OpenStrateBridge;

/// <summary>
/// Command-agnostic core operations, callable from ribbon commands (with
/// dialogs) or from the HTTP bridge (silent, JSON-returning). All methods run
/// on the Revit UI thread via RevitDispatcher.
/// </summary>
public static class BridgeCore
{
    /// <summary>Ensure there is an active host document; create + activate one if not.</summary>
    public static object OpenHost(UIApplication uiApp)
    {
        if (uiApp.ActiveUIDocument?.Document is { } existing)
            return new { document = existing.Title, created = false };

        var app = uiApp.Application;
        string template = app.DefaultProjectTemplate;
        Document host = string.IsNullOrEmpty(template) || !File.Exists(template)
            ? app.NewProjectDocument(UnitSystem.Metric)
            : app.NewProjectDocument(template);

        // Background docs cannot be activated until saved to disk.
        string hostPath = Path.Combine(Path.GetTempPath(), "openstrate", "host.rvt");
        Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);
        host.SaveAs(hostPath, new SaveAsOptions { OverwriteExistingFile = true });
        host.Close(false);                       // close the background copy FIRST:
        uiApp.OpenAndActivateDocument(hostPath); // the active document may not be closed via API

        return new { document = uiApp.ActiveUIDocument!.Document.Title, created = true, path = hostPath };
    }

    /// <summary>Fetch the IFC from the OpenStrate service and link it into the active project.</summary>
    public static object Ingest(UIApplication uiApp)
    {
        Document hostDoc = uiApp.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No active document. Call open_host first.");

        string ifcPath;
        using (var client = new OpenStrateClient())
        {
            ifcPath = client.DownloadIfc();
        }

        // Revit 2027's CreateFromIFC requires the cache RVT to already exist:
        // background-open the IFC, save the cache, close, then link.
        string cacheRvt = ifcPath + ".RVT";
        var app = uiApp.Application;
        Document ifcDoc = app.OpenIFCDocument(ifcPath);
        ifcDoc.SaveAs(cacheRvt, new SaveAsOptions { OverwriteExistingFile = true });
        ifcDoc.Close(false);

        ElementId linkTypeId;
        using (var tx = new Transaction(hostDoc, "OpenStrate: Link scan IFC"))
        {
            tx.Start();
            LinkLoadResult load = RevitLinkType.CreateFromIFC(
                hostDoc, ifcPath, cacheRvt, true, new RevitLinkOptions(false));
            if (!LinkLoadResult.IsCodeSuccess(load.LoadResult))
                throw new InvalidOperationException($"IFC link failed: {load.LoadResult}");
            linkTypeId = load.ElementId;
            RevitLinkInstance.Create(hostDoc, linkTypeId);
            tx.Commit();
        }

        var linkInstance = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .First(li => li.GetTypeId() == linkTypeId);
        Document linkDoc = linkInstance.GetLinkDocument();

        BridgeSession.LastIngestedPath = ifcPath;
        BridgeSession.LastIngestedDoc = linkDoc;

        return new
        {
            linked = true,
            ifc_path = ifcPath,
            cache_rvt = cacheRvt,
            elements_visible = new FilteredElementCollector(linkDoc)
                .WhereElementIsNotElementType().GetElementCount(),
        };
    }
}
