namespace AmbientMonitor;

internal sealed class RendererCommand
{
    public string Operation { get; set; } = "configure";
    public RenderingBackend Backend { get; set; }
    public bool Running { get; set; }
    public bool EnableHdr { get; set; } = true;
    public List<DisplayProfile> Profiles { get; set; } = new();
}

internal sealed class RendererReply
{
    public string Kind { get; set; } = "status";
    public string Text { get; set; } = "";
    public string? DeviceName { get; set; }
    public int ProcessId { get; set; } = Environment.ProcessId;
    public RenderingBackend Backend { get; set; }
}
