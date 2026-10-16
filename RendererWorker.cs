using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;

namespace AmbientMonitor;

// One graphics backend per process. A backend change creates a new process,
// not a replacement device inside a process that has already used the other engine.
internal sealed class RendererWorker : IDisposable
{
    private readonly App _app;
    private readonly string _pipeName;
    private readonly bool _offscreen;
    private readonly List<IOverlayWindow> _overlays = new();
    private readonly Dictionary<string, DisplayProfile> _profiles = new();
    private readonly Channel<RendererReply> _outgoing = Channel.CreateUnbounded<RendererReply>(new UnboundedChannelOptions { SingleReader = true });
    private GpuRenderService? _gpu;
    private CpuRenderService? _cpu;
    private OverlayLayerService? _layers;
    private RenderingBackend? _backend;
    private bool _disposed;
    private bool _hdrEnabled;
    private long _generation;

    public RendererWorker(App app, string pipeName, bool offscreen) { _app = app; _pipeName = pipeName; _offscreen = offscreen; }
    public async Task RunAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(10000);
            using var input = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var output = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var writer = WriteReplies(output);
            Reply("ready", "Renderer worker ready");
            try
            {
                while (await input.ReadLineAsync() is { } line)
                {
                    var command = JsonSerializer.Deserialize<RendererCommand>(line) ?? throw new InvalidDataException("Invalid renderer command.");
                    if (command.Operation == "shutdown") break;
                    await _app.Dispatcher.InvokeAsync(() => Configure(command));
                }
            }
            finally
            {
                await _app.Dispatcher.InvokeAsync(StopRendering);
                _outgoing.Writer.TryComplete();
                await writer;
            }
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Renderer worker: {0}", ex); }
        finally { await _app.Dispatcher.InvokeAsync(() => { Dispose(); _app.Shutdown(); }); }
    }
    private async Task WriteReplies(StreamWriter output)
    {
        await foreach (var reply in _outgoing.Reader.ReadAllAsync()) await output.WriteLineAsync(JsonSerializer.Serialize(reply));
    }
    private void Configure(RendererCommand command)
    {
        if (_backend is { } selected && selected != command.Backend)
            throw new InvalidOperationException("Backend changes require a fresh renderer process.");
        _backend = command.Backend;
        if (!command.Running)
        {
            StopRendering();
            Reply("configured", $"{command.Backend} worker prepared");
            return;
        }
        if (_layers is null || _hdrEnabled != command.EnableHdr) StartRendering(command);
        else
        {
            var incoming = command.Profiles.Select(p => p.DeviceName).ToHashSet();
            foreach (var overlay in _overlays.Where(o => !incoming.Contains(o.DeviceName)).ToArray())
            {
                overlay.Close(); _overlays.Remove(overlay); _profiles.Remove(overlay.DeviceName);
            }
            foreach (var profile in command.Profiles)
            {
                var overlay = _overlays.FirstOrDefault(o => o.DeviceName == profile.DeviceName);
                if (overlay is not null && _profiles.TryGetValue(profile.DeviceName, out var previous) &&
                    (previous.Canvas != profile.Canvas || (previous.Mode == OverlayMode.ImageBorder) != (profile.Mode == OverlayMode.ImageBorder)))
                {
                    overlay.Close(); _overlays.Remove(overlay); overlay = null;
                }
                _profiles[profile.DeviceName] = profile;
                try
                {
                    if (overlay is not null) overlay.UpdateSettings(profile);
                    else if (profile.Enabled) AddOverlay(profile);
                }
                catch (Exception ex) { Reply("status", $"{profile.TabTitle}: {ex.Message}"); }
            }
            Reply("configured", "Renderer settings updated");
        }
    }
    private void StartRendering(RendererCommand command)
    {
        StopRendering();
        _hdrEnabled = command.EnableHdr;
        _layers = new OverlayLayerService(_app.Dispatcher, _offscreen ? new OffscreenLayerPlatform() : null);
        _layers.NoticeChanged += (window, notice) =>
        {
            var overlay = _overlays.FirstOrDefault(o => o.LayerHandle == window);
            if (overlay is not null) Reply("layer", notice ?? "", overlay.DeviceName);
        };
        long generation = _generation;
        void Status(string text) { if (generation == _generation) Reply("status", text); }
        if (command.Backend == RenderingBackend.Gpu)
        {
            _gpu = new GpuRenderService(captureDesktop: !_offscreen, enableHdr: command.EnableHdr);
            _gpu.StatusChanged += Status;
        }
        else { _cpu = new CpuRenderService(); _cpu.StatusChanged += Status; }
        bool failed = false;
        foreach (var profile in command.Profiles)
        {
            _profiles[profile.DeviceName] = profile;
            if (profile.Enabled)
                try { AddOverlay(profile); }
                catch (Exception ex) { failed = true; Reply("status", $"{profile.TabTitle}: {ex.Message}"); }
        }
        Reply("configured", $"{command.Backend} renderer configured on {_overlays.Count} display(s)");
        if (!failed && _overlays.Count > 0 && command.Profiles.Where(p => p.Enabled).All(p => p.Mode == OverlayMode.ImageBorder))
            Reply("status", "Image borders running · static images, no desktop capture");
    }
    private void AddOverlay(DisplayProfile profile)
    {
        System.Drawing.Rectangle bounds;
        if (_offscreen)
        {
            if (profile.Mode == OverlayMode.Ambient) throw new InvalidOperationException("Offscreen worker checks must use veil or image mode, without desktop capture.");
            bounds = new System.Drawing.Rectangle(-30000 + _overlays.Count * 128, -30000, 64, 64);
        }
        else
        {
            if (profile.Canvas is { } canvas)
            {
                if (!canvas.IsValid) throw new InvalidDataException("Canvas size must be between 16 and 16384 pixels per side.");
                bounds = canvas.Rectangle;
            }
            else
            {
                var screen = CaptureService.GetScreens().FirstOrDefault(s => s.DeviceName == profile.DeviceName);
                if (screen is null) return;
                bounds = screen.Bounds;
            }
        }
        IOverlayWindow overlay = profile.Mode == OverlayMode.ImageBorder
            ? new ImageBorderWindow(profile.DeviceName, bounds, profile, _layers!)
            : _gpu is not null
            ? new NativeGpuOverlay(profile.DeviceName, bounds, profile, _gpu, _layers!)
            : new OverlayWindow(profile.DeviceName, bounds, profile, null, _cpu, _layers);
        _overlays.Add(overlay);
        overlay.Show();
    }
    private void StopRendering()
    {
        _generation++;
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear(); _profiles.Clear();
        _layers?.Dispose(); _layers = null;
        _gpu?.Dispose(); _gpu = null;
        _cpu?.Dispose(); _cpu = null;
    }
    private void Reply(string kind, string text, string? device = null) => _outgoing.Writer.TryWrite(new RendererReply
    { Kind = kind, Text = text, DeviceName = device, Backend = _backend ?? RenderingBackend.Gpu });
    public void Dispose() { if (_disposed) return; _disposed = true; StopRendering(); _outgoing.Writer.TryComplete(); }

    private sealed class OffscreenLayerPlatform : IWindowLayerPlatform
    {
        private readonly HashSet<nint> _topmost = new();
        public LayerSnapshot Snapshot() => new([], 0);
        public bool IsValid(nint window) => true;
        public bool IsTopmost(nint window) => _topmost.Contains(window);
        public bool IsHiddenOrMinimized(nint window) => false;
        public void ShowWithoutActivation(nint window) { }
        public void SetOrder(nint window, nint after) { if (after == -1) _topmost.Add(window); else _topmost.Remove(window); }
        public IDisposable Watch(Action changed) => new EmptyLease();
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    }
}
