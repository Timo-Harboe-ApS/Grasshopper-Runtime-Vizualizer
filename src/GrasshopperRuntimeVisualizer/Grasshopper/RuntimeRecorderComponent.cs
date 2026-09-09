using Grasshopper.Kernel;

namespace GrasshopperRuntimeVisualizer.Grasshopper;

public sealed class RuntimeRecorderComponent : GH_Component
{
    private bool _lastOpen;

    public RuntimeRecorderComponent()
        : base(
            "Runtime Recorder",
            "RuntimeRec",
            "Records Grasshopper expiration and solution activity for browser playback.",
            "Runtime",
            "Debug")
    {
    }

    public override Guid ComponentGuid => new("8b6f10a5-a579-47f8-a420-c71a340852c7");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddBooleanParameter("Record", "R", "Start or stop a disk-backed runtime recording.", GH_ParamAccess.item, false);
        pManager.AddBooleanParameter("Open Visualizer", "O", "Open the browser visualizer for the current or latest session.", GH_ParamAccess.item, false);
        pManager.AddBooleanParameter("Capture Stacks", "S", "Capture and deduplicate .NET call stacks for expiration events. Higher overhead.", GH_ParamAccess.item, false);
        pManager.AddBooleanParameter("Debug Mode", "D", "Write diagnostics to Rhino command history, log file, and /debug.", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter("Status", "S", "Current recorder status.", GH_ParamAccess.item);
        pManager.AddTextParameter("Session Folder", "F", "Current or latest session folder.", GH_ParamAccess.item);
        pManager.AddTextParameter("Visualizer URL", "U", "Local browser visualizer URL.", GH_ParamAccess.item);
        pManager.AddTextParameter("Debug URL", "D", "Local debug diagnostics URL.", GH_ParamAccess.item);
        pManager.AddTextParameter("Diagnostics", "X", "Current server and file path diagnostics.", GH_ParamAccess.item);
        pManager.AddIntegerParameter("Events", "E", "Events recorded in the current session.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var record = false;
        var open = false;
        var captureStacks = false;
        var debugMode = false;

        DA.GetData(0, ref record);
        DA.GetData(1, ref open);
        DA.GetData(2, ref captureStacks);
        DA.GetData(3, ref debugMode);

        var document = OnPingDocument();
        var service = RuntimeVisualizerService.Instance;
        service.SetDebugMode(debugMode);

        if (record && !service.IsRecording)
        {
            if (document is null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No Grasshopper document is available to record.");
            }
            else
            {
                service.StartRecording(document, captureStacks);
            }
        }
        else if (!record && service.IsRecording)
        {
            service.StopRecording("stopped");
        }

        if (open && !_lastOpen)
        {
            service.OpenVisualizer();
        }

        _lastOpen = open;

        DA.SetData(0, service.Status);
        DA.SetData(1, service.CurrentOrLatestSessionFolder ?? "");
        DA.SetData(2, service.VisualizerUrl);
        DA.SetData(3, service.DebugUrl);
        DA.SetData(4, service.BuildDiagnosticsText());
        DA.SetData(5, service.EventCount);
    }
}
