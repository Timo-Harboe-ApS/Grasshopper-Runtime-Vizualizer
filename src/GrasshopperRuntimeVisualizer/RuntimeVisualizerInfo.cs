using System.Drawing;
using Grasshopper.Kernel;

namespace GrasshopperRuntimeVisualizer;

public sealed class RuntimeVisualizerInfo : GH_AssemblyInfo
{
    public override string Name => "Runtime Visualizer";
    public override Bitmap? Icon => null;
    public override string Description => "Records Grasshopper runtime activity and visualizes expiration heat in a local browser.";
    public override Guid Id => new("7c7b8348-4b70-4731-82d4-bd78e63be5c5");
    public override string AuthorName => "Timo Harboe";
    public override string AuthorContact => "";
}
