using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ResultCode = Vortice.DXGI.ResultCode;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace AmbientMonitor;

// One device, capture stream and immediate-context thread shared by all overlays.
// Only small parameter structs cross from the UI thread. Desktop pixels never do.
internal sealed class GpuRenderService : IDisposable
{
    private static readonly Lazy<ShaderBytecode> Shaders = new(CompileShaders);
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<nint, Target> _targets = new();
    private readonly List<Target> _presentationOrder = new();
    private int _presentationStart;
    private readonly Thread _thread;
    private readonly bool _captureDesktop;
    private readonly bool _enableHdr, _forceHdr;
    private bool _hdrActive;
    private bool _sourceHdr;
    private TimeSpan _nextHdrCheck;
    private readonly HdrCompatibility _captureCompatibility = new();
    private readonly Action<string>? _hdrProbe;
    private bool _hdrPipelineRejected;
    private string? _captureFallback;
    private string _stage = "initialization";
    private volatile bool _stopping;
    private IDXGIFactory2? _factory;
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _desktop;
    private ID3D11ShaderResourceView? _desktopView;
    private ID3D11VertexShader? _vertex;
    private ID3D11PixelShader? _cropShader, _blurShader, _compositeShader;
    private ID3D11Buffer? _constants;
    private ID3D11SamplerState? _sampler;
    private int _sourceWidth, _sourceHeight;
    private ModeRotation _rotation;
    private long _frameVersion;
    private string? _lastStatus;

    public event Action<string>? StatusChanged;

    public GpuRenderService(bool captureDesktop = true, bool enableHdr = false, bool forceHdr = false, Action<string>? hdrProbe = null)
    {
        _captureDesktop = captureDesktop;
        if (forceHdr && captureDesktop) throw new ArgumentException("Forced HDR verification must not capture the desktop.");
        if (hdrProbe is not null && captureDesktop) throw new ArgumentException("HDR fault verification must not capture the desktop.");
        _hdrProbe = hdrProbe;
        _enableHdr = enableHdr; _forceHdr = forceHdr;
        _thread = new Thread(Run) { IsBackground = true, Name = "Ambient Monitor Direct3D" };
        _thread.Start();
    }

    public void Add(nint window, int width, int height, OverlaySettings settings)
    {
        var snapshot = settings.Snapshot();
        Enqueue(() =>
        {
            var target = new Target(window, width, height, snapshot);
            _targets.Add(window, target);
            _presentationOrder.Add(target);
        });
    }

    public void Update(nint window, OverlaySettings settings)
    {
        var snapshot = settings.Snapshot();
        Enqueue(() =>
        {
            if (_targets.TryGetValue(window, out var target))
            {
                target.ImageDirty |= target.Settings.HasDifferentImageSettings(snapshot);
                target.Settings = snapshot;
                target.Dirty = true;
            }
        });
    }

    public void Remove(nint window)
    {
        using var done = new ManualResetEventSlim();
        Enqueue(() =>
        {
            try
            {
                if (_targets.Remove(window, out var target))
                {
                    _presentationOrder.Remove(target);
                    target.Dispose();
                    // Submit deferred destruction before the UI destroys/reuses the HWND.
                    _context?.Flush();
                }
            }
            finally { done.Set(); }
        });
        done.Wait();
    }

    private void Enqueue(Action action) { _commands.Enqueue(action); _wake.Set(); }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        var retryAt = TimeSpan.Zero;
        try
        {
            while (!_stopping)
            {
                var started = clock.Elapsed;
                while (_commands.TryDequeue(out var command)) command();
                if (!_targets.Values.Any(t => t.Settings.Enabled))
                {
                    ReleaseDevice();
                    _wake.WaitOne();
                    continue;
                }
                if (started < retryAt) { _wake.WaitOne(100); continue; }
                try
                {
                    _stage = "device initialization";
                    if (_device is null) Initialize();
                    if (_enableHdr && !_forceHdr && started >= _nextHdrCheck)
                    {
                        _nextHdrCheck = started + TimeSpan.FromSeconds(2);
                        if (!_factory!.IsCurrent) { ReleaseDevice(); Initialize(); }
                        bool sourceHdr = HdrColor.IsHdr(_output!);
                        if ((!_hdrPipelineRejected && AnyHdrOutput()) != _hdrActive || sourceHdr != _sourceHdr)
                        { ReleaseDevice(); Initialize(); }
                        foreach (var target in _targets.Values)
                        {
                            if (target.SwapChain is null) continue;
                            bool previous = target.HdrOutput;
                            target.RefreshOutputColor();
                            target.Dirty |= previous != target.HdrOutput;
                        }
                    }
                    bool needsCapture = _targets.Values.Any(t => t.Settings.Enabled && t.Settings.Mode == OverlayMode.Ambient);
                    if (needsCapture)
                    {
                        _stage = "desktop capture";
                        if (_captureDesktop) Capture();
                    }
                    else ReleaseCapture();
                    int targetCount = _presentationOrder.Count;
                    int start = targetCount == 0 ? 0 : _presentationStart % targetCount;
                    for (int index = 0; index < targetCount; index++)
                    {
                        var target = _presentationOrder[(start + index) % targetCount];
                        if (!target.Settings.Enabled) continue;
                        _stage = "canvas presentation initialization";
                        if (target.SwapChain is null) target.Initialize(_factory!, _device!, _hdrActive, _hdrProbe);
                        bool ambient = target.Settings.Mode == OverlayMode.Ambient;
                        if (ambient && _desktopView is null) continue;
                        bool imageChanged = ambient && target.FrameVersion != _frameVersion;
                        if (!target.Dirty && !target.ImageDirty && target.RenderedImageKey == target.Settings.ImageKey &&
                            !imageChanged && !target.HasAnimatedNoise) continue;
                        _stage = "canvas effects/presentation";
                        Render(target, imageChanged, (float)(clock.Elapsed.TotalSeconds * 30 % 65536));
                    }
                    // A temporarily busy or slower display must not permanently
                    // lose its slot to the target submitted first on every tick.
                    _presentationStart = targetCount == 0 ? 0 : (start + 1) % targetCount;
                    SetStatus(needsCapture && _desktopView is null
                        ? "GPU waiting for the first desktop image"
                        : RunningStatus());
                }
                catch (Exception ex)
                {
                    if (_hdrActive && HdrCompatibility.IsUnsupported(ex))
                    {
                        _hdrPipelineRejected = true;
                        SetStatus($"GPU HDR fallback · {_stage} unsupported; retrying with SDR compatibility");
                        Trace.TraceWarning("HDR pipeline fallback at {0}: {1}", _stage, ex);
                        ReleaseDevice(); retryAt = clock.Elapsed; continue;
                    }
                    // Access is lost on lock/unlock, resolution changes and device removal.
                    // Re-enumerate the primary output and recreate all GPU resources.
                    SetStatus($"GPU paused; retrying ({_stage}): {ex.Message}");
                    Trace.TraceError("GPU pipeline: {0}", ex);
                    ReleaseDevice();
                    retryAt = clock.Elapsed + TimeSpan.FromSeconds(1);
                }
                if (_device is not null && !_targets.Values.Any(t => t.Settings.Enabled &&
                    (t.Settings.Mode == OverlayMode.Ambient || t.HasAnimatedNoise || t.Dirty)))
                {
                    // Static veils do not render continuously; HDR-enabled
                    // workers wake only to recheck display color configuration.
                    if (_enableHdr) _wake.WaitOne(2000); else _wake.WaitOne();
                    continue;
                }
                // Capped at 30 Hz, with no busy-waiting or unbounded render queue.
                int delay = Math.Max(1, (int)(33.333 - (clock.Elapsed - started).TotalMilliseconds));
                _wake.WaitOne(delay);
            }
        }
        finally
        {
            while (_commands.TryDequeue(out var command)) command();
            ReleaseDevice();
            foreach (var target in _targets.Values) target.Dispose();
            _targets.Clear();
            _presentationOrder.Clear();
        }
    }

    private void SetStatus(string status)
    {
        if (_lastStatus == status) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }
    private string RunningStatus()
    {
        int outputFallbacks = _targets.Values.Count(t => t.Settings.Enabled && t.HdrPresentationRejected);
        if (_hdrPipelineRejected) return "GPU running · SDR compatibility (HDR unsupported by this driver/path)";
        if (!_hdrActive) return "GPU running · Direct3D 11 / Desktop Duplication · SDR";
        string status = "GPU running · HDR-aware FP16 processing";
        if (_captureFallback is not null) status += " · SDR capture fallback";
        if (outputFallbacks > 0) status += $" · {outputFallbacks} SDR canvas output fallback(s)";
        else status += " / scRGB · SDR destinations tone-mapped";
        return status;
    }

    private void Initialize()
    {
        _factory = CreateDXGIFactory2<IDXGIFactory2>(false);
        var primary = GpuOutputSelection.CurrentPrimary();
        int adapterCount = 0, outputCount = 0;
        for (uint a = 0; ; a++)
        {
            var adapterResult = _factory.EnumAdapters1(a, out var adapter);
            if (adapterResult == ResultCode.NotFound) break;
            adapterResult.CheckError();
            adapterCount++;
            using (adapter)
            {
                for (uint o = 0; ; o++)
                {
                    var outputResult = adapter.EnumOutputs(o, out var output);
                    if (outputResult == ResultCode.NotFound) break;
                    outputResult.CheckError();
                    outputCount++;
                    using (output)
                    {
                        var description = output.Description;
                        var bounds = description.DesktopCoordinates;
                        if (!GpuOutputSelection.MatchesCurrent(primary, description.Monitor, description.DeviceName, description.AttachedToDesktop,
                            bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)) continue;
                        D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                        _device = device;
                        _context = context;
                        _output = output.QueryInterface<IDXGIOutput1>();
                        _sourceWidth = primary.Right - primary.Left;
                        _sourceHeight = primary.Bottom - primary.Top;
                        break;
                    }
                }
            }
            if (_device is not null) break;
        }
        if (_device is null) throw new InvalidOperationException($"DXGI could not match primary display {primary.DeviceName} ({adapterCount} adapters, {outputCount} outputs); retrying display discovery.");
        _hdrActive = !_hdrPipelineRejected && _forceHdr;
        _sourceHdr = _forceHdr || HdrColor.IsHdr(_output!);
        if (_enableHdr && !_hdrPipelineRejected) _hdrActive |= AnyHdrOutput();
        if (_hdrActive) _hdrProbe?.Invoke("processing.fp16");
        var shaders = Shaders.Value;
        _vertex = _device.CreateVertexShader(shaders.Vertex.Span);
        _cropShader = _device.CreatePixelShader(shaders.Crop.Span);
        _blurShader = _device.CreatePixelShader(shaders.Blur.Span);
        _compositeShader = _device.CreatePixelShader(shaders.Composite.Span);
        _constants = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Parameters>(), BindFlags.ConstantBuffer));
        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
            TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
    }
    private bool AnyHdrOutput()
    {
        for (uint a = 0; _factory!.EnumAdapters1(a, out var adapter).Success; a++)
            using (adapter)
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    using (output) if (HdrColor.IsHdr(output)) return true;
        return false;
    }

    private static ShaderBytecode CompileShaders()
    {
        using var stream = typeof(GpuRenderService).Assembly.GetManifestResourceStream("AmbientMonitor.Shaders.Ambient.hlsl")
            ?? throw new InvalidOperationException("The GPU shader resource is missing.");
        using var reader = new StreamReader(stream);
        string shader = reader.ReadToEnd();
        ReadOnlyMemory<byte> Compile(string entry, string profile) =>
            Compiler.Compile(shader, entry, "Ambient.hlsl", profile, ShaderFlags.OptimizationLevel3);
        // Compile once per process, including across overlay restarts/device recovery.
        return new ShaderBytecode(Compile("FullscreenVS", "vs_5_0"), Compile("CropPS", "ps_5_0"),
            Compile("BlurPS", "ps_5_0"), Compile("CompositePS", "ps_5_0"));
    }

    private void Capture()
    {
        if (_duplication is null)
        {
            _duplication = _captureCompatibility.Select(_hdrActive,
                () =>
                {
                    using var advanced = _output!.QueryInterfaceOrNull<IDXGIOutput5>() ?? throw new NotSupportedException("HDR desktop duplication interface is unavailable.");
                    return advanced.DuplicateOutput1(_device!, new[] { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm });
                },
                () => _output!.DuplicateOutput(_device!),
                ex => { _captureFallback = ex.Message; Trace.TraceWarning("HDR capture fell back to SDR: {0}", ex); });
            _rotation = _duplication.Description.Rotation;
        }
        var result = _duplication.AcquireNextFrame(0, out var info, out var resource);
        if (result == ResultCode.WaitTimeout) return;
        result.CheckError();
        try
        {
            using (resource)
            {
                // A newly opened duplication stream can first deliver a pointer-only
                // update. It must not seed the image cache with an undefined/black
                // surface and then reuse that image during animated-noise frames.
                if (info.LastPresentTime == 0) return;
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                CopyDesktopImage(texture);
            }
        }
        finally { _duplication.ReleaseFrame().CheckError(); }
    }

    private void CopyDesktopImage(ID3D11Texture2D texture)
    {
        var desc = texture.Description;
        if (_desktop is not null && (_desktop.Description.Format != desc.Format || _desktop.Description.Width != desc.Width || _desktop.Description.Height != desc.Height))
        { _desktopView?.Dispose(); _desktopView = null; _desktop.Dispose(); _desktop = null; }
        if (_desktop is null)
        {
            desc.BindFlags = BindFlags.ShaderResource;
            desc.Usage = ResourceUsage.Default;
            desc.CPUAccessFlags = CpuAccessFlags.None;
            desc.MiscFlags = ResourceOptionFlags.None;
            _desktop = _device!.CreateTexture2D(desc);
            _desktopView = _device.CreateShaderResourceView(_desktop);
        }
        _context!.CopyResource(_desktop, texture);
        _frameVersion++;
    }

    private void Render(Target target, bool imageChanged, float noiseFrame)
    {
        var settings = target.Settings;
        var gradientStops = CanvasEffects.GradientStops(settings);
        var parameters = new Parameters
        {
            Effects = new Vector4((float)Math.Clamp(settings.Strength, 0, 1), (float)Math.Clamp(settings.Brightness, 0.2, 1.5),
                settings.NoiseEnabled ? (float)Math.Clamp(settings.Noise, 0, 0.3) : 0, settings.Mode == OverlayMode.Veil ? 1 : 0),
            Animation = new Vector4(noiseFrame, 0, (float)DitherGrain.Size(settings.NoiseGrainPixels), 0),
            VeilColor = VeilPalette.Color(settings.VeilColor),
            Repeat = new Vector4(Math.Clamp(settings.RepeatX, 1, 16), Math.Clamp(settings.RepeatY, 1, 16), (float)settings.RepeatMode, 0),
            Gradient = new Vector4(settings.GradientEnabled ? (float)settings.GradientMode : 0, (float)Math.Clamp(settings.GradientOpacity, 0, 1), (float)gradientStops.Start, (float)(settings.GradientAngle * Math.PI / 180)),
            GradientTint = VeilPalette.Color(settings.GradientColor),
            GradientStartTint = VeilPalette.Color(settings.GradientStartColor),
            GradientOptions = new Vector4((float)gradientStops.End, (float)Math.Clamp(settings.GradientFalloff, 0.25, 4), (float)Math.Clamp(settings.GradientStartOpacity, 0, 1), (float)Math.Clamp(settings.GradientIntensity, 0, 1)),
            EdgeWidths = new Vector4((float)Math.Clamp(settings.GradientLeftWidth, 0, 0.5), (float)Math.Clamp(settings.GradientRightWidth, 0, 0.5), (float)Math.Clamp(settings.GradientTopWidth, 0, 0.5), (float)Math.Clamp(settings.GradientBottomWidth, 0, 0.5)),
            EdgeStrengths = new Vector4((float)Math.Clamp(settings.GradientLeftOpacity, 0, 1), (float)Math.Clamp(settings.GradientRightOpacity, 0, 1), (float)Math.Clamp(settings.GradientTopOpacity, 0, 1), (float)Math.Clamp(settings.GradientBottomOpacity, 0, 1)),
            GradientEdges = new Vector4((settings.GradientEdges & 1) != 0 ? 1 : 0, (settings.GradientEdges & 2) != 0 ? 1 : 0, (settings.GradientEdges & 4) != 0 ? 1 : 0, (settings.GradientEdges & 8) != 0 ? 1 : 0),
            GradientControl = new Vector4(settings.GradientIndependentEdges ? 1 : 0, 0, 0, 0),
            ColorPipeline = new Vector4(_hdrActive ? 1 : 0, _desktop?.Description.Format == Format.R16G16B16A16_Float ? _sourceHdr ? 2 : 1 : 0, target.HdrOutput ? 1 : 0, target.FloatOutput ? 1 : 0)
        };
        ID3D11ShaderResourceView? image = null;
        if (settings.Mode == OverlayMode.Ambient)
        {
            var framing = SourceFraming.Calculate(_sourceWidth, _sourceHeight, target.Width, target.Height, settings);
            double width = framing.View.Width, height = framing.View.Height;
            double left = framing.View.Left, top = framing.View.Top;
            parameters.Retained = new Vector4((float)(framing.Retained.Left / _sourceWidth), (float)(framing.Retained.Top / _sourceHeight),
                (float)(framing.Retained.Right / _sourceWidth), (float)(framing.Retained.Bottom / _sourceHeight));
            parameters.Animation.Y = framing.Mask ? 1 : 0;
            parameters.Crop = new Vector4((float)(left / _sourceWidth), (float)(top / _sourceHeight), (float)(width / _sourceWidth), (float)(height / _sourceHeight));
            parameters.Transform = new Vector4(settings.FlipHorizontal ? 1 : 0, settings.FlipVertical ? 1 : 0, (float)_rotation, 0);
            // Match the old default blur's visual scale, but make it independent of
            // the resolution control. Large blur uses smaller GPU working surfaces.
            double sourceRadius = Math.Clamp(settings.Blur, 0, 1000) * 0.18 / 0.35;
            // A Gaussian needs a wider support than a box for comparable softness.
            if (settings.BlurType == BlurType.Gaussian) sourceRadius *= Math.Sqrt(3);
            double scale = Math.Clamp(settings.CaptureScalePercent, 10, 100) / 100d;
            if (sourceRadius > 0) scale = Math.Min(scale, 16 / sourceRadius);
            int workWidth = Math.Max(1, (int)Math.Round(width * scale));
            int workHeight = Math.Max(1, (int)Math.Round(height * scale));
            if (settings.RepeatMode != SourceRepeatMode.None) { workWidth = Math.Min(target.Width, workWidth * Math.Clamp(settings.RepeatX, 1, 16)); workHeight = Math.Min(target.Height, workHeight * Math.Clamp(settings.RepeatY, 1, 16)); }
            bool resized = target.EnsureSurfaces(_device!, workWidth, workHeight, _hdrActive);
            var imageKey = settings.ImageKey;
            if (target.ImageDirty || target.RenderedImageKey != imageKey || imageChanged || resized)
            {
                Draw(target.First!.RenderView, workWidth, workHeight, _cropShader!, _desktopView, parameters);
                if (sourceRadius * scale >= 0.5)
                {
                    parameters.Blur = new Vector4((float)(sourceRadius / width), 0, settings.BlurType == BlurType.Gaussian ? 1 : 0, 0);
                    Draw(target.Second!.RenderView, workWidth, workHeight, _blurShader!, target.First.ShaderView, parameters);
                    parameters.Blur.X = 0;
                    parameters.Blur.Y = (float)(sourceRadius / height);
                    Draw(target.First.RenderView, workWidth, workHeight, _blurShader!, target.Second.ShaderView, parameters);
                }
                target.FrameVersion = _frameVersion;
                target.ImageDirty = false;
                target.RenderedImageKey = imageKey;
            }
            image = target.First!.ShaderView;
        }
        else target.ReleaseSurfaces();

        Draw(target.BackBuffer!, target.Width, target.Height, _compositeShader!, image, parameters);
        // Non-blocking presentation avoids one slow monitor stalling every overlay.
        target.PresentationAttempts++;
        var presented = target.SwapChain!.Present(0, PresentFlags.DoNotWait);
        if (presented == ResultCode.WasStillDrawing) { target.PresentationRetries++; target.Dirty = true; return; }
        presented.CheckError();
        target.Dirty = presented.Code == 0x087A0001; // DXGI_STATUS_OCCLUDED
        if (!target.Dirty)
        {
            target.PresentedFrames++;
            target.LastPresentedFrameVersion = target.FrameVersion;
        }
        if (settings.Mode == OverlayMode.Veil) target.ImageDirty = false;
    }

    private void Draw(ID3D11RenderTargetView destination, int width, int height, ID3D11PixelShader shader,
        ID3D11ShaderResourceView? source, Parameters parameters)
    {
        _context!.PSUnsetShaderResource(0);
        // Bind the complete pass state here, after HWND/swap-chain creation and on
        // every device generation. Noise does not sample a texture, so an absent
        // sampler could otherwise leave apparently-working noise over a black image.
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertex);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetSampler(0, _sampler);
        _context.OMSetRenderTargets(destination);
        _context.RSSetViewport(0, 0, width, height);
        _context.UpdateSubresource(parameters, _constants!);
        _context.PSSetShader(shader);
        if (source is not null) _context.PSSetShaderResource(0, source);
        _context.Draw(3, 0);
        _context.PSUnsetShaderResource(0);
        _context.UnsetRenderTargets();
    }

    private void ReleaseCapture()
    {
        _desktopView?.Dispose(); _desktopView = null;
        _desktop?.Dispose(); _desktop = null;
        _duplication?.Dispose(); _duplication = null;
    }

    private void ReleaseDevice()
    {
        _context?.ClearState();
        foreach (var target in _targets.Values) target.Dispose();
        ReleaseCapture();
        _sampler?.Dispose(); _sampler = null;
        _constants?.Dispose(); _constants = null;
        _compositeShader?.Dispose(); _compositeShader = null;
        _blurShader?.Dispose(); _blurShader = null;
        _cropShader?.Dispose(); _cropShader = null;
        _vertex?.Dispose(); _vertex = null;
        _output?.Dispose(); _output = null;
        _context?.Flush();
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _factory?.Dispose(); _factory = null;
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join();
        _wake.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public Vector4 Crop, Transform, Blur, Effects, Animation, Retained, VeilColor, Repeat, Gradient, GradientTint, GradientEdges, ColorPipeline;
        public Vector4 GradientStartTint, GradientOptions, EdgeWidths, EdgeStrengths, GradientControl;
    }

    private sealed record ShaderBytecode(ReadOnlyMemory<byte> Vertex, ReadOnlyMemory<byte> Crop,
        ReadOnlyMemory<byte> Blur, ReadOnlyMemory<byte> Composite);

    private sealed class Surface : IDisposable
    {
        public ID3D11Texture2D Texture { get; }
        public ID3D11ShaderResourceView ShaderView { get; }
        public ID3D11RenderTargetView RenderView { get; }
        public Surface(ID3D11Device device, int width, int height, bool hdr)
        {
            Texture = device.CreateTexture2D(new Texture2DDescription(hdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm,
                (uint)width, (uint)height, 1, 1, bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource));
            ShaderView = device.CreateShaderResourceView(Texture);
            RenderView = device.CreateRenderTargetView(Texture);
        }
        public void Dispose() { RenderView.Dispose(); ShaderView.Dispose(); Texture.Dispose(); }
    }

    private sealed class Target(nint window, int width, int height, OverlaySettings settings) : IDisposable
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public OverlaySettings Settings = settings;
        public bool Dirty = true;
        public bool ImageDirty = true;
        public ImageSettingsKey? RenderedImageKey;
        public bool HasAnimatedNoise => Settings.NoiseEnabled && Settings.Noise > 0;
        public long FrameVersion = -1;
        public long PresentationAttempts, PresentationRetries, PresentedFrames;
        public long LastPresentedFrameVersion = -1;
        public IDXGISwapChain1? SwapChain;
        public ID3D11RenderTargetView? BackBuffer;
        public Surface? First, Second;
        private int _workWidth, _workHeight;
        public bool FloatOutput, HdrOutput;
        private readonly HdrCompatibility _presentationCompatibility = new();
        public bool HdrPresentationRejected => _presentationCompatibility.Rejected;

        public void Initialize(IDXGIFactory2 factory, ID3D11Device device, bool hdr, Action<string>? probe = null)
        {
            _presentationCompatibility.Select(hdr,
                () => { try { probe?.Invoke("presentation.fp16"); InitializeCore(factory, device, true); return true; } catch { ReleasePresentation(); throw; } },
                () => { InitializeCore(factory, device, false); return true; },
                ex => Trace.TraceWarning("HDR canvas output fell back to SDR: {0}", ex));
        }
        private void InitializeCore(IDXGIFactory2 factory, ID3D11Device device, bool hdr)
        {
            FloatOutput = hdr;
            HdrOutput = false;
            SwapChain = factory.CreateSwapChainForHwnd(device, window, new SwapChainDescription1
            {
                Width = (uint)Width, Height = (uint)Height, Format = hdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm,
                BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0), SwapEffect = SwapEffect.FlipSequential,
                Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.FrameLatencyWaitableObject
            });
            if (hdr)
            {
                using var color = SwapChain.QueryInterface<IDXGISwapChain3>();
                if ((color.CheckColorSpaceSupport(HdrColor.ScRgb) & SwapChainColorSpaceSupportFlags.Present) == 0)
                {
                    throw new NotSupportedException("This canvas does not support scRGB presentation.");
                }
                color.SetColorSpace1(HdrColor.ScRgb);
                RefreshOutputColor();
            }
            // The old device-wide limit of one let the first monitor consume the
            // entire shared queue. Waitable flip chains have independent budgets.
            using var latency = SwapChain.QueryInterface<IDXGISwapChain2>();
            latency.MaximumFrameLatency = 1;
            using var buffer = SwapChain.GetBuffer<ID3D11Texture2D>(0);
            BackBuffer = device.CreateRenderTargetView(buffer);
            Dirty = true;
        }
        public void RefreshOutputColor()
        {
            if (!FloatOutput || SwapChain is null) { HdrOutput = false; return; }
            try { using var containing = SwapChain.GetContainingOutput(); HdrOutput = HdrColor.IsHdr(containing); }
            catch { HdrOutput = false; }
        }

        public bool EnsureSurfaces(ID3D11Device device, int width, int height, bool hdr)
        {
            if (First is not null && _workWidth == width && _workHeight == height) return false;
            ReleaseSurfaces();
            First = new Surface(device, width, height, hdr);
            Second = new Surface(device, width, height, hdr);
            _workWidth = width; _workHeight = height;
            return true;
        }
        public void ReleaseSurfaces()
        {
            First?.Dispose(); First = null;
            Second?.Dispose(); Second = null;
            RenderedImageKey = null;
        }
        public void Dispose()
        {
            ReleaseSurfaces();
            ReleasePresentation();
            Dirty = true; ImageDirty = true; FrameVersion = -1;
        }
        private void ReleasePresentation()
        {
            BackBuffer?.Dispose(); BackBuffer = null;
            SwapChain?.Dispose(); SwapChain = null;
            FloatOutput = HdrOutput = false;
        }
    }
}
