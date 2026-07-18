using Autodesk.Revit.UI;

namespace OpenStrateBridge;

/// <summary>
/// Add-in entry point: on Revit startup, attach the ExternalEvent dispatcher
/// (must happen on the UI thread) and start the localhost tool server.
/// </summary>
public class BridgeApplication : IExternalApplication
{
    private static RevitDispatcher? _dispatcher;
    private static MiniHttpServer? _server;

    public Result OnStartup(UIControlledApplication application)
    {
        _dispatcher = new RevitDispatcher();
        _dispatcher.Attach();                      // UI thread: legal here
        _server = new MiniHttpServer(_dispatcher);
        _server.Start();                           // background accept loop
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _server?.Stop();
        return Result.Succeeded;
    }
}
