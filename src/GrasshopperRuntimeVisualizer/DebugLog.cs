using Rhino;

namespace GrasshopperRuntimeVisualizer;

public sealed class DebugLog
{
    private readonly object _gate = new();

    public DebugLog(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    public string Path { get; }
    public bool Enabled { get; set; }

    public void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:O} {message}";

        lock (_gate)
        {
            File.AppendAllText(Path, line + Environment.NewLine);
        }

        try
        {
            RhinoApp.WriteLine("[Runtime Visualizer] " + message);
        }
        catch
        {
            // RhinoApp can be unavailable in non-Rhino test contexts.
        }
    }
}
