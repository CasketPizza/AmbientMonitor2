using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmbientMonitor;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Vortice.Direct3D11;
using Vortice.DXGI;
using System.Runtime.InteropServices;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--automation-fixture")) { Thread.Sleep(10000); return 0; }
            if (args.Contains("--close-lifecycle")) { CloseLifecycleRegression(); return 0; }
            if (args.Contains("--ui-features")) { ImageBorderPixels(); OffscreenUi(); return 0; }
            if (args.Contains("--native-layer")) { NativeTopmostStartup(); return 0; }
            if (args.Contains("--backend-switch")) { BackendSwitchDiscovery(); return 0; }
            if (args.Contains("--worker-switch")) { WorkerProcessSwitches().GetAwaiter().GetResult(); return 0; }
            CpuEffects();
            DitherSizeRegression();
            IndependentGradientRegression();
            FourSidedCropAndCanvasGeometry();
            RepeatAndGradientPixels();
            ImageBorderPixels();
            CpuWorkerLifecycle();
            ProfileSerialization();
            ShortcutValidation();
            LayerPolicyAndLifecycle();
            PrimaryOutputSelection();
            NativeGpuRestartAndFlip();
            HdrFloatPipeline();
            HdrCompatibilityFallbacks();
            MultiSizeGpuAnimation();
            NativeTopmostStartup();
            BackendSwitchDiscovery();
            WorkerProcessSwitches().GetAwaiter().GetResult();
            RunCloseLifecycleChild();
            OffscreenUi();
            Console.WriteLine("All feature checks passed. No desktop capture, global hotkey registration, tray icon, or on-screen fixtures were used.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static byte[] Pixels(BitmapSource frame)
    {
        var bytes = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(bytes, frame.PixelWidth * 4, 0);
        return bytes;
    }
    private static int Pixel(BitmapSource frame, int x, int y, int channel) => Pixels(frame)[(y * frame.PixelWidth + x) * 4 + channel];

    private static void CpuEffects()
    {
        using var source = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(source);
        graphics.Clear(System.Drawing.Color.White);
        using var processor = new CpuFrameProcessor();
        var settings = new OverlaySettings { Blur = 0, Strength = 1, Brightness = 1, NoiseEnabled = false, CaptureScalePercent = 100 };
        var full = processor.Process(source, settings, 64, 64, 0);
        Require(full.IsFrozen && Pixel(full, 32, 32, 2) > 235, "CPU frame was not frozen/bright.");
        settings.Brightness = 0.5;
        var dark = processor.Process(source, settings, 64, 64, 0);
        Require(Pixel(dark, 32, 32, 2) is > 115 and < 130, "CPU brightness failed.");
        Require(Pixel(full, 32, 32, 2) > 235, "Reused CPU buffers modified an earlier published frame.");
        settings.Brightness = 1; settings.Strength = 0.5;
        Require(Math.Abs(Pixel(processor.Process(source, settings, 64, 64, 0), 32, 32, 2) - Pixel(dark, 32, 32, 2)) <= 1, "CPU strength failed.");
        settings.Strength = 1;
        graphics.Clear(System.Drawing.Color.Black);
        graphics.FillRectangle(System.Drawing.Brushes.White, 32, 0, 32, 64);
        var edge = processor.Process(source, settings, 64, 64, 0);
        Require(Pixel(edge, 8, 32, 2) < 5 && Pixel(edge, 55, 32, 2) > 235, "CPU capture transform failed.");
        settings.FlipHorizontal = true;
        Require(Pixel(processor.Process(source, settings, 64, 64, 0), 8, 32, 2) > 235, "CPU horizontal flip failed.");
        settings.FlipHorizontal = false;
        foreach (var type in new[] { BlurType.Box, BlurType.Gaussian })
        {
            settings.Blur = 12; settings.BlurType = type;
            var blurred = processor.Process(source, settings, 64, 64, 0);
            Require(Pixel(blurred, 30, 32, 2) > 5 && Pixel(blurred, 33, 32, 2) < 235, $"CPU {type} blur failed to soften the edge.");
        }
        settings.Blur = 0; settings.CropZoom = 2; settings.FocusX = 1;
        Require(Pixel(processor.Process(source, settings, 64, 64, 0), 8, 16, 2) > 235, "CPU crop/focus failed.");
        settings.CropZoom = 1; settings.FocusX = 0.5; settings.NoiseEnabled = true; settings.Noise = 0.2;
        var noise1 = Pixels(processor.Process(source, settings, 64, 64, 1));
        Require(!noise1.SequenceEqual(Pixels(processor.Process(source, settings, 64, 64, 2))), "CPU grain did not animate.");
        settings.Mode = OverlayMode.Veil; settings.NoiseEnabled = false;
        var veil = processor.Process(null, settings, 64, 64, 0);
        Require(Pixel(veil, 32, 32, 2) == 16 && Pixel(veil, 32, 32, 1) == 21 && Pixel(veil, 32, 32, 0) == 31, "CPU veil failed.");
        settings.VeilColor = "#E04080"; settings.Strength = 0.5;
        var coloredVeil = processor.Process(null, settings, 64, 64, 0);
        Require(Pixel(coloredVeil, 32, 32, 2) == 112 && Pixel(coloredVeil, 32, 32, 1) == 32 && Pixel(coloredVeil, 32, 32, 0) == 64,
            "CPU custom veil color/strength failed.");
        Console.WriteLine("PASS: CPU blur, brightness, strength, crop, flip, grain, veil and immutable frame publication.");
    }

    private static void CpuWorkerLifecycle()
    {
        using var ready = new AutoResetEvent(false);
        using var service = new CpuRenderService();
        BitmapSource? last = null;
        // Veil mode never captures the desktop and only publishes in-memory frames.
        var settings = new OverlaySettings { Mode = OverlayMode.Veil, NoiseEnabled = false, Strength = 1 };
        var id = service.Add(64, 64, settings, frame => { last = frame; ready.Set(); });
        Require(ready.WaitOne(5000) && last is { IsFrozen: true }, "CPU worker did not publish a veil.");
        settings.Strength = 0.5; service.Update(id, settings);
        Require(ready.WaitOne(5000) && Pixel(last!, 32, 32, 2) == 8, "Sleeping CPU worker did not respond to settings.");
        service.Remove(id);
        Console.WriteLine("PASS: CPU worker idle wake-up, settings update, target removal and shutdown without screen capture.");
    }

    private static void FourSidedCropAndCanvasGeometry()
    {
        using var source = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var paint = Graphics.FromImage(source)) paint.Clear(System.Drawing.Color.White);
        using var cpu = new CpuFrameProcessor();
        var settings = new OverlaySettings { Blur = 0, NoiseEnabled = false, Strength = 1, Brightness = 1, CaptureScalePercent = 100,
            CropLeft = 0.25, CropRight = 0.25, CropTop = 0.125, CropBottom = 0.125 };
        var fill = cpu.Process(source, settings, 64, 64, 0);
        Require(Pixel(fill, 2, 32, 2) == 0 && Pixel(fill, 62, 32, 2) == 0 && Pixel(fill, 32, 2, 2) == 0 && Pixel(fill, 32, 62, 2) == 0,
            "Fill mode did not black out all four removed source sides.");
        Require(Pixel(fill, 32, 32, 2) > 235, "Fill crop removed the retained center.");
        settings.Blur = 12; settings.NoiseEnabled = true; settings.Noise = 0.2;
        var blurred = cpu.Process(source, settings, 64, 64, 1);
        Require(Pixel(blurred, 2, blurred.PixelHeight / 2, 2) == 0, "Blur/noise leaked into a masked crop edge.");
        settings.Blur = 0; settings.NoiseEnabled = false; settings.ScaleMode = ImageScaleMode.Fit;
        var stretch = cpu.Process(source, settings, 64, 64, 0);
        Require(stretch.PixelWidth == 32 && stretch.PixelHeight == 48 && Pixel(stretch, 4, 24, 2) > 235,
            "Stretch did not extract the retained rectangle without black bars.");
        // Decimal crop values can make Right - Width round just below Left at 1x zoom.
        foreach (int sourceWidth in new[] { 64, 1920, 2560, 3840 })
        foreach (double crop in new[] { 0.0675, 0.1, 0.2, 0.333, 0.49, 0.98 })
        foreach (double zoom in new[] { 1.0, 1.000001, 2.0 })
        {
            var decimalCrop = new OverlaySettings { ScaleMode = ImageScaleMode.Fit, CropLeft = crop,
                CropRight = 0.01, CropTop = crop, CropBottom = 0.01, CropZoom = zoom, FocusX = 1, FocusY = 0 };
            var layout = SourceFraming.Calculate(sourceWidth, 1080, 400, 250, decimalCrop);
            Require(double.IsFinite(layout.View.Left) && double.IsFinite(layout.View.Top) &&
                layout.View.Left >= layout.Retained.Left - 1e-9 && layout.View.Top >= layout.Retained.Top - 1e-9 &&
                layout.View.Right <= layout.Retained.Right + 1e-9 && layout.View.Bottom <= layout.Retained.Bottom + 1e-9,
                "Decimal Stretch crop escaped its retained source bounds.");
        }
        settings.CropLeft = settings.CropTop = 0.1;
        var decimalStretch = cpu.Process(source, settings, 400, 250, 0);
        Require(Pixel(decimalStretch, 2, 2, 2) > 235, "CPU decimal Stretch crop failed to render.");
        settings.ScaleMode = ImageScaleMode.Fill; settings.CropZoom = 2;
        var zoomed = cpu.Process(source, settings, 64, 64, 0);
        Require(Pixel(zoomed, 2, 16, 2) > 230, "Fill zoom/focus could not frame the retained region.");
        var normalized = SourceFraming.Normalize(0.8, 0.8);
        Require(normalized.First + normalized.Second <= 0.990001, "Opposite source crop sides eliminated the whole source.");
        var box = CanvasBounds.FromPoints(400, 300, -200, -100);
        Require(box == new CanvasBounds(-200, -100, 600, 400) && box.IsValid, "Canvas drag geometry failed for reversed/negative desktop coordinates.");
        Require(!new CanvasBounds(0, 0, 2, 10).IsValid, "Tiny canvas was accepted.");
        var profile = new DisplayProfile { Canvas = box, CropLeft = 0.25, DeviceName = "canvas-test" };
        var reloaded = JsonSerializer.Deserialize<DisplayProfile>(JsonSerializer.Serialize(profile))!;
        Require(reloaded.Canvas == box && reloaded.CropLeft == 0.25, "Canvas/crop metadata did not survive IPC/settings serialization.");
        Console.WriteLine("PASS: four-sided Fill masking, Stretch extraction, zoom/focus, black mask after effects, crop limits and cross-monitor canvas coordinates.");
    }

    private static void ProfileSerialization()
    {
        var profile = new DisplayProfile { DeviceName = @"\\.\DISPLAY7", DisplayName = "Display 7" };
        var changes = new List<string>();
        profile.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);
        profile.Nickname = "  Gaming  ";
        Require(profile.TabTitle == "Gaming" && profile.TabSubtitle == "Display 7" && profile.HasTabSubtitle, "Nickname did not retain the display label.");
        Require(changes.Contains(nameof(DisplayProfile.TabTitle)) && changes.Contains(nameof(DisplayProfile.TabSubtitle)), "Nickname bindings did not receive change notifications.");
        var json = JsonSerializer.Serialize(profile);
        Require(!json.Contains("TabTitle") && JsonSerializer.Deserialize<DisplayProfile>(json)!.Nickname == "Gaming", "Nickname serialization failed.");
        profile.Nickname = "";
        Require(profile.TabTitle == "Display 7" && !profile.HasTabSubtitle, "Clearing nickname did not restore the display label.");
        Require(JsonSerializer.Deserialize<DisplayProfile>("{\"DeviceName\":\"legacy\",\"Enabled\":true}")!.Nickname == "", "Legacy profiles failed to load.");
        Console.WriteLine("PASS: nickname display labels, binding notifications, persistence and legacy settings.");
    }

    private static void ShortcutValidation()
    {
        var key = new ShortcutDefinition { Modifiers = 3, VirtualKey = KeyInterop.VirtualKeyFromKey(Key.F10) };
        Require(key.IsValid && key.ToString() == "Ctrl + Alt + F10", "Shortcut formatting/validation failed.");
        Require(!new ShortcutDefinition { VirtualKey = 65 }.IsValid, "Bare letter shortcut was accepted.");
        Require(!new ShortcutDefinition { Modifiers = 2, VirtualKey = 0x11 }.IsValid, "Modifier-only shortcut was accepted.");
        Require(new ShortcutDefinition().IsValid && !new ShortcutDefinition().IsConfigured, "Shortcut clearing failed.");
        Console.WriteLine("PASS: shortcut validation, formatting and clearing (no global registration).");
    }

    private static void OnGpu(GpuRenderService service, Action action)
    {
        using var complete = new ManualResetEventSlim();
        Exception? error = null;
        typeof(GpuRenderService).GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [(Action)(() => { try { action(); } catch (Exception ex) { error = ex; } finally { complete.Set(); } })]);
        Require(complete.Wait(10000), "GPU regression command timed out.");
        if (error is not null) throw error;
    }
    private static object GpuField(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(value)!;
    private static void NativeGpuRestartAndFlip()
    {
        for (int cycle = 0; cycle < 6; cycle++)
        {
            using var renderer = new GpuRenderService(captureDesktop: false);
            using var layers = new OverlayLayerService(Dispatcher.CurrentDispatcher, new PassiveLayerPlatform());
            var settings = new OverlaySettings { Mode = OverlayMode.Veil, NoiseEnabled = false, Blur = 0, CaptureScalePercent = 100 };
            var first = new NativeGpuOverlay("test-first", new Rectangle(-30000, -30000, 64, 64), settings, renderer, layers);
            var second = new NativeGpuOverlay("test-second", new Rectangle(-29900, -30000, 64, 64), settings, renderer, layers);
            try
            {
                first.Show(); second.Show();
                var startup = System.Diagnostics.Stopwatch.StartNew();
                bool initialized = false;
                while (!initialized && startup.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(50);
                    OnGpu(renderer, () => initialized = typeof(GpuRenderService).GetField("_device", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer) is not null &&
                        ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().All(t => t.GetType().GetField("SwapChain")!.GetValue(t) is not null));
                }
                OnGpu(renderer, () =>
                {
                    Require(GpuField(renderer, "_device") is not null, "Offscreen GPU device did not initialize: " + GpuField(renderer, "_lastStatus"));
                    Require(typeof(GpuRenderService).GetField("_duplication", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer) is null,
                        "Regression test unexpectedly captured the desktop.");
                });
                if (cycle == 0) { CheckGpuFlip(renderer, first, second, settings); CheckGpuSourceCrop(renderer, first, second, settings); CheckGpuRepeat(renderer, first); CheckGpuVeilColor(renderer); }
            }
            finally { first.Close(); second.Close(); }
        }
        Console.WriteLine("PASS: six native-GPU restart/teardown cycles and horizontal/vertical updates across two offscreen targets (synthetic frames, no desktop capture).");
    }
    private static void CheckGpuFlip(GpuRenderService service, NativeGpuOverlay first, NativeGpuOverlay second, OverlaySettings settings)
    {
        settings.Mode = OverlayMode.Ambient;
        first.UpdateSettings(settings); second.UpdateSettings(settings);
        OnGpu(service, () =>
        {
            var device = (ID3D11Device)GpuField(service, "_device");
            var context = (ID3D11DeviceContext)GpuField(service, "_context");
            using var fixture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 64, 1, 1, bindFlags: BindFlags.ShaderResource));
            var pixels = new byte[64 * 64 * 4];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            {
                int index = (y * 64 + x) * 4;
                pixels[index] = (byte)(y * 4); pixels[index + 2] = (byte)(x * 4); pixels[index + 3] = 255;
            }
            context.UpdateSubresource(pixels, fixture, 0, 256);
            typeof(GpuRenderService).GetField("_sourceWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, 64);
            typeof(GpuRenderService).GetField("_sourceHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, 64);
            typeof(GpuRenderService).GetMethod("CopyDesktopImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [fixture]);
        });
        Thread.Sleep(100);
        settings.FlipHorizontal = true; second.UpdateSettings(settings);
        Thread.Sleep(100);
        settings.FlipVertical = true; first.UpdateSettings(settings);
        Thread.Sleep(100);
        OnGpu(service, () =>
        {
            var device = (ID3D11Device)GpuField(service, "_device");
            var context = (ID3D11DeviceContext)GpuField(service, "_context");
            var targets = ((System.Collections.IDictionary)GpuField(service, "_targets")).Values.Cast<object>().ToArray();
            Require(targets.Length == 2, "Missing GPU target during multi-display updates.");
            foreach (var target in targets)
            {
                var requested = (OverlaySettings)GpuField(target, "Settings");
                Require((ImageSettingsKey)GpuField(target, "RenderedImageKey") == requested.ImageKey, "GPU used an outdated rendered image key.");
                var surface = GpuField(target, "First");
                var image = (ID3D11Texture2D)surface.GetType().GetProperty("Texture")!.GetValue(surface)!;
                var description = image.Description;
                description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None;
                description.CPUAccessFlags = CpuAccessFlags.Read;
                using var readback = device.CreateTexture2D(description);
                context.CopyResource(readback, image);
                var mapped = context.Map(readback, 0, MapMode.Read);
                try
                {
                    var pixel = new byte[4];
                    Marshal.Copy(mapped.DataPointer + (int)(8 * mapped.RowPitch + 8 * 4), pixel, 0, 4);
                    Require(pixel[2] > 210, "Horizontal flip stayed cached until another setting changed.");
                    Require(requested.FlipVertical ? pixel[0] > 210 : pixel[0] < 40, "Vertical flip did not immediately reach its target.");
                }
                finally { context.Unmap(readback, 0); }
            }
        });
    }
    private sealed class PassiveLayerPlatform : IWindowLayerPlatform
    {
        private readonly HashSet<nint> _topmost = new();
        public LayerSnapshot Snapshot() => new([], 0);
        public bool IsValid(nint window) => true;
        public bool IsTopmost(nint window) => _topmost.Contains(window);
        public bool IsHiddenOrMinimized(nint window) => false;
        public void ShowWithoutActivation(nint window) { }
        public void SetOrder(nint window, nint after) { if (after == -1) _topmost.Add(window); else if (after == -2) _topmost.Remove(window); }
        public IDisposable Watch(Action changed) => new EmptyLease();
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    }

    private static void CheckGpuSourceCrop(GpuRenderService renderer, NativeGpuOverlay fillTarget, NativeGpuOverlay stretchTarget, OverlaySettings settings)
    {
        OnGpu(renderer, () =>
        {
            var device = (ID3D11Device)GpuField(renderer, "_device");
            var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
            using var fixture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 64, 1, 1, bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource));
            using var view = device.CreateRenderTargetView(fixture);
            context.ClearRenderTargetView(view, new Vortice.Mathematics.Color4(1, 1, 1, 1));
            typeof(GpuRenderService).GetMethod("CopyDesktopImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(renderer, [fixture]);
        });
        settings.FlipHorizontal = settings.FlipVertical = false;
        settings.CropLeft = settings.CropTop = 0.1; settings.CropRight = 0.25; settings.CropBottom = 0.125;
        settings.ScaleMode = ImageScaleMode.Fill; fillTarget.UpdateSettings(settings);
        settings.ScaleMode = ImageScaleMode.Fit; stretchTarget.UpdateSettings(settings);
        Thread.Sleep(150);
        OnGpu(renderer, () =>
        {
            var device = (ID3D11Device)GpuField(renderer, "_device");
            var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
            foreach (var target in ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values)
            {
                var requested = (OverlaySettings)GpuField(target!, "Settings");
                var surface = GpuField(target!, "First");
                var texture = (ID3D11Texture2D)surface.GetType().GetProperty("Texture")!.GetValue(surface)!;
                var description = texture.Description;
                description.BindFlags = BindFlags.None; description.Usage = ResourceUsage.Staging; description.CPUAccessFlags = CpuAccessFlags.Read;
                using var readback = device.CreateTexture2D(description);
                context.CopyResource(readback, texture);
                var mapped = context.Map(readback, 0, MapMode.Read);
                try
                {
                    var edge = new byte[4];
                    Marshal.Copy(mapped.DataPointer + (int)(description.Height / 2 * mapped.RowPitch + 2 * 4), edge, 0, 4);
                    Require(requested.ScaleMode == ImageScaleMode.Fill ? edge[2] == 0 : edge[2] > 245, "GPU crop did not distinguish Fill masking from Stretch extraction.");
                }
                finally { context.Unmap(readback, 0); }
            }
        });
        Console.WriteLine("PASS: GPU source-crop shader masking and retained-region Stretch extraction.");
    }

    private static void CheckGpuVeilColor(GpuRenderService renderer)
    {
        OnGpu(renderer, () =>
        {
            var device = (ID3D11Device)GpuField(renderer, "_device");
            var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
            var target = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().First();
            using var output = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 64, 1, 1, bindFlags: BindFlags.RenderTarget));
            using var view = device.CreateRenderTargetView(output);
            var field = target.GetType().GetField("BackBuffer")!;
            var oldView = field.GetValue(target);
            var settingsField = target.GetType().GetField("Settings")!;
            var oldSettings = settingsField.GetValue(target);
            try
            {
                field.SetValue(target, view);
                settingsField.SetValue(target, new OverlaySettings { Mode = OverlayMode.Veil, VeilColor = "#E04080", Strength = 0.5, NoiseEnabled = false });
                typeof(GpuRenderService).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(renderer, [target, true, 0f]);
                var desc = output.Description; desc.BindFlags = BindFlags.None; desc.Usage = ResourceUsage.Staging; desc.CPUAccessFlags = CpuAccessFlags.Read;
                using var readback = device.CreateTexture2D(desc);
                context.CopyResource(readback, output);
                var mapped = context.Map(readback, 0, MapMode.Read);
                try
                {
                    var pixel = new byte[4]; Marshal.Copy(mapped.DataPointer, pixel, 0, 4);
                    Require(Math.Abs(pixel[2] - 112) <= 1 && Math.Abs(pixel[1] - 32) <= 1 && Math.Abs(pixel[0] - 64) <= 1, "GPU custom veil color/strength failed.");
                }
                finally { context.Unmap(readback, 0); }
            }
            finally { field.SetValue(target, oldView); settingsField.SetValue(target, oldSettings); }
        });
        Console.WriteLine("PASS: GPU veil color and intensity, checked using in-memory output pixels.");
    }

    private static string TestBorderPath => Path.Combine(AppContext.BaseDirectory, "transparent-border.png");
    private static void ImageBorderPixels()
    {
        var bytes = new byte[100 * 50 * 4];
        for (int y = 0; y < 50; y++) for (int x = 0; x < 100; x++)
        {
            if (x is >= 10 and < 90 && y is >= 10 and < 40) continue;
            int i = (y * 100 + x) * 4; bytes[i + 2] = 255; bytes[i + 3] = 255;
        }
        var source = BitmapSource.Create(100, 50, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bytes, 400); source.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = File.Create(TestBorderPath)) encoder.Save(stream);
        source = ImageBorderDrawing.Load(TestBorderPath);
        var settings = new OverlaySettings { Mode = OverlayMode.ImageBorder, Strength = 1 };
        BitmapSource Render()
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawDrawing(ImageBorderDrawing.Create(source, settings, 100, 100));
            var frame = new RenderTargetBitmap(100, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); frame.Render(visual); return frame;
        }
        var fit = Render();
        Require(Pixel(fit, 50, 50, 3) == 0 && Pixel(fit, 5, 30, 2) == 255 && Pixel(fit, 5, 5, 3) == 0, "Image Fit lost its transparent center or aspect ratio.");
        settings.BorderScale = BorderScaleMode.Fill;
        var fill = Render();
        Require(Pixel(fill, 50, 5, 2) == 255 && Pixel(fill, 50, 50, 3) == 0, "Image Fill framing failed.");
        settings.BorderScale = BorderScaleMode.Stretch;
        var stretch = Render();
        Require(Pixel(stretch, 5, 5, 2) == 255 && Pixel(stretch, 50, 50, 3) == 0, "Image Stretch failed to preserve alpha.");
        settings.ImageZoom = 0.5; settings.ImageOffsetX = 20; settings.ImageOffsetY = 10;
        var positioned = Render();
        Require(Pixel(positioned, 47, 37, 2) > 240 && Pixel(positioned, 5, 5, 3) == 0, "Image zoom/pixel offsets failed.");
        settings.ImageZoom = 1; settings.ImageOffsetX = settings.ImageOffsetY = 0; settings.CropLeft = 0.2;
        var cropped = Render();
        Require(Pixel(cropped, 2, 50, 3) == 0 && Pixel(cropped, 98, 50, 2) > 240, "Image source crop failed.");
        settings.FlipHorizontal = true;
        Require(Pixel(Render(), 2, 50, 2) > 240, "Image flip failed.");
        settings.Strength = 0.5;
        Require(Pixel(Render(), 2, 50, 3) is >= 126 and <= 129, "Image opacity failed.");
        Console.WriteLine("PASS: transparent image borders: Fit/Fill/Stretch, crop, zoom, pixel offsets, flip and opacity.");
    }

    private static void MultiSizeGpuAnimation()
    {
        using var renderer = new GpuRenderService(captureDesktop: false);
        using var layers = new OverlayLayerService(Dispatcher.CurrentDispatcher, new PassiveLayerPlatform());
        var settings = new OverlaySettings { Mode = OverlayMode.Veil, NoiseEnabled = true, Noise = 0.1, Blur = 0, CaptureScalePercent = 100 };
        var small = new NativeGpuOverlay("animation-small", new Rectangle(-30000, -30000, 64, 64), settings, renderer, layers);
        var large = new NativeGpuOverlay("animation-large", new Rectangle(-29000, -30000, 512, 288), settings, renderer, layers);
        try
        {
            small.Show(); large.Show();
            Thread.Sleep(500);
            OnGpu(renderer, () =>
            {
                var targets = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().ToArray();
                Require(targets.Length == 2, "Multi-size animation target missing.");
                foreach (var target in targets)
                {
                    var chain = (IDXGISwapChain1)GpuField(target, "SwapChain");
                    Require((chain.Description1.Flags & SwapChainFlags.FrameLatencyWaitableObject) != 0, "Display retained a shared presentation budget.");
                    using var latency = chain.QueryInterface<IDXGISwapChain2>();
                    Require(latency.MaximumFrameLatency == 1, "Display-specific frame latency was not configured.");
                    Require((long)GpuField(target, "PresentationAttempts") >= 3, "One display stopped animating without a settings edit.");
                }
                using var device = ((ID3D11Device)GpuField(renderer, "_device")).QueryInterface<IDXGIDevice1>();
                Require(device.MaximumFrameLatency > 1, "Device-wide latency of one still starves secondary displays.");
            });
            settings.Mode = OverlayMode.Ambient; settings.NoiseEnabled = false;
            small.UpdateSettings(settings); large.UpdateSettings(settings);
            for (int frame = 0; frame < 8; frame++)
            {
                int expected = 30 + frame * 25;
                OnGpu(renderer, () =>
                {
                    var device = (ID3D11Device)GpuField(renderer, "_device");
                    var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
                    using var image = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 64, 1, 1, bindFlags: BindFlags.ShaderResource));
                    var pixels = new byte[64 * 64 * 4];
                    for (int p = 0; p < pixels.Length; p += 4) { pixels[p + 2] = (byte)expected; pixels[p + 3] = 255; }
                    context.UpdateSubresource(pixels, image, 0, 256);
                    typeof(GpuRenderService).GetField("_sourceWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(renderer, 64);
                    typeof(GpuRenderService).GetField("_sourceHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(renderer, 64);
                    typeof(GpuRenderService).GetMethod("CopyDesktopImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(renderer, [image]);
                });
                Thread.Sleep(100); // source changes only: no slider/settings calls
                OnGpu(renderer, () =>
                {
                    var device = (ID3D11Device)GpuField(renderer, "_device");
                    var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
                    long sourceVersion = (long)GpuField(renderer, "_frameVersion");
                    foreach (var target in ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values)
                    {
                        Require((long)GpuField(target!, "FrameVersion") == sourceVersion, "Large display did not consume the next shared frame.");
                        var surface = GpuField(target!, "First");
                        var texture = (ID3D11Texture2D)surface.GetType().GetProperty("Texture")!.GetValue(surface)!;
                        var description = texture.Description;
                        description.BindFlags = BindFlags.None; description.Usage = ResourceUsage.Staging; description.CPUAccessFlags = CpuAccessFlags.Read;
                        using var readback = device.CreateTexture2D(description);
                        context.CopyResource(readback, texture);
                        var mapped = context.Map(readback, 0, MapMode.Read);
                        try
                        {
                            var pixel = new byte[4];
                            Marshal.Copy(mapped.DataPointer + (int)(description.Height / 2 * mapped.RowPitch + description.Width / 2 * 4), pixel, 0, 4);
                            Require(Math.Abs(pixel[2] - expected) <= 2, "Cached image froze on one display until a slider changed.");
                        }
                        finally { context.Unmap(readback, 0); }
                    }
                });
            }
            Console.WriteLine("PASS: separate swap-chain latency budgets, continuous animation attempts and eight shared-frame updates across unequal display sizes without slider edits.");
        }
        finally { small.Close(); large.Close(); }
    }

    private static void NativeTopmostStartup()
    {
        using var renderer = new GpuRenderService(captureDesktop: false);
        var platform = new OwnedWindowLayerPlatform();
        using var layers = new OverlayLayerService(Dispatcher.CurrentDispatcher, platform);
        var settings = new OverlaySettings { Mode = OverlayMode.Veil, NoiseEnabled = false, Layer = OverlayLayer.Topmost };
        var overlay = new NativeGpuOverlay("native-order-test", new Rectangle(-30000, -30000, 64, 64), settings, renderer, layers);
        try
        {
            overlay.Show();
            var snapshot = platform.Snapshot();
            int position = snapshot.Windows.ToList().FindIndex(w => w.Handle == overlay.LayerHandle);
            Require(platform.IsTopmost(overlay.LayerHandle), "Actual native startup lost WS_EX_TOPMOST.");
            Require(!snapshot.Windows.Take(position).Any(w => w.Visible && !w.Topmost), "Actual native Topmost started below an ordinary application window.");
            Console.WriteLine("PASS: actual native Topmost flag and z-order after startup, with only the test window positioned off-screen.");
        }
        finally { overlay.Close(); }
    }
    private sealed class OwnedWindowLayerPlatform : IWindowLayerPlatform
    {
        private readonly WindowLayerPlatform _native = new();
        public LayerSnapshot Snapshot() => _native.Snapshot();
        public bool IsValid(nint window) => _native.IsValid(window);
        public bool IsTopmost(nint window) => _native.IsTopmost(window);
        public bool IsHiddenOrMinimized(nint window) => _native.IsHiddenOrMinimized(window);
        public void ShowWithoutActivation(nint window) => _native.ShowWithoutActivation(window);
        public void SetOrder(nint window, nint after) => _native.SetOrder(window, after);
        // No global hooks: this check only reads the global list and moves its own HWND.
        public IDisposable Watch(Action changed) => new EmptyLease();
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    }

    private static void BackendSwitchDiscovery()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            var testApp = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            testApp.InitializeComponent();
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
        }
        var settings = new OverlaySettings { Mode = OverlayMode.Veil, NoiseEnabled = false, Layer = OverlayLayer.Normal };
        for (int cycle = 0; cycle < 6; cycle++)
        {
            using (var cpu = new CpuRenderService())
            using (var layers = new OverlayLayerService(Dispatcher.CurrentDispatcher, new PassiveLayerPlatform()))
            {
                var window = new OverlayWindow("switch-cpu", new Rectangle(-30000, -30000, 64, 64), settings, null, cpu, layers);
                try { window.Show(); Pump(); }
                finally { window.Close(); }
            }
            using var gpu = new GpuRenderService(captureDesktop: false);
            using var gpuLayers = new OverlayLayerService(Dispatcher.CurrentDispatcher, new PassiveLayerPlatform());
            var gpuWindow = new NativeGpuOverlay("switch-gpu", new Rectangle(-30000, -30000, 64, 64), settings, gpu, gpuLayers);
            try
            {
                gpuWindow.Show();
                bool initialized = false;
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (!initialized && timeout.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(100);
                    OnGpu(gpu, () => initialized = typeof(GpuRenderService).GetField("_device", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gpu) is not null);
                }
                if (!initialized)
                {
                    OnGpu(gpu, () => Console.WriteLine("GPU switch status: " + GpuField(gpu, "_lastStatus")));
                    throw new InvalidOperationException("GPU discovery failed after the WPF CPU overlay was closed.");
                }
            }
            finally { gpuWindow.Close(); }
        }
        Console.WriteLine("PASS: six WPF-CPU to native-GPU backend switches off-screen without desktop capture.");
    }

    private static async Task WorkerProcessSwitches()
    {
        await using var host = new RendererProcessHost(offscreen: true);
        var profiles = new List<DisplayProfile>
        {
            new() { DeviceName = "worker-test-display", Nickname = "Unsaved test name", Enabled = true, Mode = OverlayMode.Veil, NoiseEnabled = false, Layer = OverlayLayer.Normal, Strength = 0.43 }
        };
        var replies = new System.Collections.Concurrent.ConcurrentQueue<RendererReply>();
        host.Message += reply => replies.Enqueue(reply);
        var pids = new HashSet<int>();
        int? previous = null;
        foreach (var backend in new[] { RenderingBackend.Gpu, RenderingBackend.Cpu, RenderingBackend.Gpu, RenderingBackend.Cpu, RenderingBackend.Gpu })
        {
            await host.ReplaceAsync(backend, profiles, true);
            int pid = host.ProcessId!.Value;
            Require(pids.Add(pid), "Backend switch reused the previous renderer process.");
            if (previous is { } oldPid)
            {
                bool gone;
                try { using var old = System.Diagnostics.Process.GetProcessById(oldPid); gone = old.HasExited; }
                catch (ArgumentException) { gone = true; }
                Require(gone, "Previous backend process remained alive after the switch.");
            }
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!replies.Any(r => r.ProcessId == pid && r.Kind == "status" && r.Text.Contains(" running ·")) && timeout.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(50);
            Require(replies.Any(r => r.ProcessId == pid && r.Kind == "status" && r.Text.Contains(" running ·")),
                $"Fresh {backend} process did not render: " + string.Join("; ", replies.Where(r => r.ProcessId == pid).Select(r => r.Text)));
            previous = pid;
        }
        Require(profiles[0].Nickname == "Unsaved test name" && profiles[0].Strength == 0.43, "Worker replacement modified UI-owned unsaved settings.");
        profiles[0].Canvas = new CanvasBounds(-1920, -100, 400, 300);
        await host.UpdateAsync(RenderingBackend.Gpu, profiles, true);
        profiles.Add(new DisplayProfile { DeviceName = "worker-canvas-two", Canvas = new CanvasBounds(100, 50, 500, 400), Enabled = true, Mode = OverlayMode.Veil, NoiseEnabled = false });
        await host.UpdateAsync(RenderingBackend.Gpu, profiles, true);
        profiles.RemoveAt(0);
        await host.UpdateAsync(RenderingBackend.Gpu, profiles, true);
        await Task.Delay(200);
        Require(!replies.Any(r => r.Kind == "error"), "Canvas resize/add/remove stopped the worker.");
        profiles[0].Mode = OverlayMode.ImageBorder; profiles[0].ImagePath = TestBorderPath;
        await host.UpdateAsync(RenderingBackend.Gpu, profiles, true);
        await Task.Delay(200);
        profiles[0].Mode = OverlayMode.Veil; profiles[0].VeilColor = "#E04080";
        await host.UpdateAsync(RenderingBackend.Gpu, profiles, true);
        await Task.Delay(200);
        Require(host.ProcessId == previous && !replies.Any(r => r.Kind == "error"), "Live image/veil mode changes lost the worker.");
        profiles.Add(new DisplayProfile { DeviceName = "missing-image-test", DisplayName = "Missing image test", Mode = OverlayMode.ImageBorder, ImagePath = Path.Combine(AppContext.BaseDirectory, "does-not-exist.png"), Enabled = true });
        await host.UpdateAsync(RenderingBackend.Gpu, profiles, true);
        var noticeTimeout = System.Diagnostics.Stopwatch.StartNew();
        while (!replies.Any(r => r.Kind == "status" && r.Text.Contains("Missing image test")) && !replies.Any(r => r.Kind == "error") && noticeTimeout.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(50);
        Require(host.ProcessId == previous && replies.Any(r => r.Kind == "status" && r.Text.Contains("Missing image test")) && !replies.Any(r => r.Kind == "error"),
            "An unavailable image was not reported independently of other canvases: " + string.Join("; ", replies.Select(r => r.Text)));
        await host.StopAsync();
        Require(host.ProcessId is null, "Stopped renderer process remained attached to the UI.");
        Console.WriteLine("PASS: GPU→CPU→GPU switches each use a new process, old workers exit, rendering resumes off-screen and UI-owned unsaved settings survive.");
    }

    private static void LayerPolicyAndLifecycle()
    {
        var platform = new FakeLayerPlatform();
        platform.Windows.AddRange([
            new(20, true, true), // topmost widget
            new(21, false, true), // application
            new(10, false, true), // our overlay
            new(22, false, true, true) // desktop icons host
        ]);
        var owned = new HashSet<nint> { 10 };
        var snapshot = platform.Snapshot();
        var misleadingSkin = new LayerSnapshot([new(20, true, true), new(21, false, true), new(23, true, true), new(22, false, true, true)], 21);
        Require(LayerPlacementPolicy.Decide(OverlayLayer.Topmost, misleadingSkin, owned).InsertAfter == 20,
            "A low desktop skin with a topmost style flag dragged the overlay behind ordinary apps.");
        Require(LayerPlacementPolicy.Decide(OverlayLayer.Topmost, snapshot, owned).InsertAfter == 20, "Topmost covered another topmost widget.");
        Require(LayerPlacementPolicy.Decide(OverlayLayer.StayTopmost, snapshot, owned).InsertAfter == -1, "Stay-topmost did not choose the front of its band.");
        Require(LayerPlacementPolicy.Decide(OverlayLayer.Bottom, snapshot, owned).InsertAfter == 21, "Bottom was placed underneath the wallpaper host.");
        Require(LayerPlacementPolicy.Decide(OverlayLayer.OnDesktop, snapshot, owned).InsertAfter == 21, "Desktop layer was not above the desktop host.");
        platform.Foreground = 22;
        Require(LayerPlacementPolicy.Decide(OverlayLayer.Bottom, platform.Snapshot(), owned).InsertAfter == 1, "Bottom did not yield to Show desktop.");
        Require(LayerPlacementPolicy.Decide(OverlayLayer.OnDesktop, platform.Snapshot(), owned).RestoreDesktop, "Desktop layer was not retained during Show desktop.");
        platform.Windows[3] = new LayerWindow(22, true, true, true);
        Require(LayerPlacementPolicy.Decide(OverlayLayer.OnDesktop, platform.Snapshot(), owned).Topmost, "Desktop layer failed to follow a promoted shell band.");
        platform.Windows[3] = new LayerWindow(22, false, true, true);
        platform.Foreground = 21;

        using (var layers = new OverlayLayerService(Dispatcher.CurrentDispatcher, platform))
        {
            layers.Register(10, OverlayLayer.Topmost, true);
            Require(platform.IsTopmost(10) && platform.Windows.FindIndex(w => w.Handle == 20) < platform.Windows.FindIndex(w => w.Handle == 10), "Native plan did not preserve widget ordering.");
            platform.SetOrder(10, -2); // simulate a late window-show demotion
            layers.Reapply(10);
            Require(platform.IsTopmost(10) && platform.Windows.FindIndex(w => w.Handle == 20) < platform.Windows.FindIndex(w => w.Handle == 10), "Late initial-show ordering was not corrected.");
            int changes = platform.Orders.Count;
            layers.Refresh();
            Require(platform.Orders.Count == changes, "Stable z-order caused redundant native moves.");
            layers.Update(10, OverlayLayer.StayTopmost, true);
            Require(platform.Windows[0].Handle == 10, "Stay topmost was not placed above the widget.");
            platform.SetOrder(20, -1); // simulate a widget moving itself forward
            layers.Refresh();
            Require(platform.Windows[0].Handle == 10, "Stay topmost did not recover after another window moved.");
            layers.Update(10, OverlayLayer.Normal, true);
            Require(!platform.IsTopmost(10) && platform.ActiveWatches == 0, "Normal mode retained topmost/monitoring state.");
            changes = platform.Orders.Count; layers.Refresh();
            Require(platform.Orders.Count == changes, "Normal mode fought ordinary application ordering.");
            layers.Update(10, OverlayLayer.Bottom, true);
            Require(!platform.IsTopmost(10) && platform.Windows.FindIndex(w => w.Handle == 10) == platform.Windows.FindIndex(w => w.Handle == 22) - 1, "Bottom was not positioned directly above the desktop.");
            layers.Update(10, OverlayLayer.OnDesktop, true);
            platform.Foreground = 22; platform.Hidden.Add(10); layers.Refresh();
            Require(platform.Shown.Contains(10), "Desktop-mode window was not restored without activation.");
            platform.Windows.RemoveAll(w => w.DesktopHost); layers.Refresh();
            Require(layers.GetNotice(10)?.Contains("Desktop host unavailable") == true, "Missing desktop did not report a fallback.");
            platform.Windows.Add(new LayerWindow(22, false, true, true)); layers.Refresh();
            Require(layers.GetNotice(10) is null, "Desktop-layer fallback did not recover when Explorer returned.");
            layers.Update(10, OverlayLayer.OnDesktop, false);
            Require(platform.ActiveWatches == 0, "Disabled overlays retained monitoring hooks.");
            layers.Unregister(10);
        }
        Require(platform.ActiveWatches == 0 && platform.Orders.Where(o => o.Window != 20).All(o => o.Window == 10), "Layering changed unrelated windows or leaked monitoring.");
        var legacy = JsonSerializer.Deserialize<DisplayProfile>("{\"Enabled\":true}")!;
        Require(legacy.Layer == OverlayLayer.Topmost, "Legacy profiles did not preserve topmost behavior.");
        var before = legacy.Snapshot(); legacy.Layer = OverlayLayer.Bottom;
        Require(!legacy.HasDifferentImageSettings(before), "Layer-only changes invalidated image processing.");
        Console.WriteLine("PASS: all five layer policies, widget ordering, Show desktop, shell recovery, no-op ordering and monitoring cleanup (simulated native platform).");
    }

    private static void PrimaryOutputSelection()
    {
        Require(GpuOutputSelection.IsPrimary(true, 101, 101, 0, 0, 3440, 1440), "Current primary-monitor handle was not selected.");
        Require(GpuOutputSelection.IsPrimary(true, 202, 101, 0, 0, 3440, 1440), "Native-origin fallback failed after monitor-handle/topology change.");
        Require(!GpuOutputSelection.IsPrimary(true, 202, 101, -1920, 0, 0, 1080), "Secondary output was mistaken for the primary.");
        Require(!GpuOutputSelection.IsPrimary(false, 101, 101, 0, 0, 3440, 1440), "Detached output was selected.");
        Require(!GpuOutputSelection.IsPrimary(true, 101, 101, 0, 0, 0, 0), "Incomplete output topology was accepted.");
        var primary = new PrimaryDisplay(101, @"\\.\DISPLAY2", 0, 0, 3440, 1440);
        Require(GpuOutputSelection.MatchesCurrent(primary, 101, @"\\.\DISPLAY2", false, 0, 0, 0, 0),
            "A current native monitor was rejected due to stale DXGI attachment/geometry after CPU use.");
        Require(GpuOutputSelection.MatchesCurrent(primary, 0, @"\\.\display2", false, 0, 0, 0, 0),
            "Live device-name fallback did not tolerate stale DXGI monitor handles.");
        Require(!GpuOutputSelection.MatchesCurrent(primary, 202, @"\\.\DISPLAY3", false, 0, 0, 0, 0),
            "Discovery selected an unrelated disconnected output.");
        Console.WriteLine("PASS: native primary-output selection and topology-change fallback without cached display names.");
    }

    private sealed class FakeLayerPlatform : IWindowLayerPlatform
    {
        public readonly List<LayerWindow> Windows = new();
        public readonly List<(nint Window, nint After)> Orders = new();
        public readonly HashSet<nint> Hidden = new(), Shown = new();
        public nint Foreground = 21;
        public int ActiveWatches;
        public LayerSnapshot Snapshot() => new(Windows.ToArray(), Foreground);
        public bool IsValid(nint window) => Windows.Any(w => w.Handle == window);
        public bool IsTopmost(nint window) => Windows.Single(w => w.Handle == window).Topmost;
        public bool IsHiddenOrMinimized(nint window) => Hidden.Contains(window);
        public void ShowWithoutActivation(nint window) { Hidden.Remove(window); Shown.Add(window); }
        public void SetOrder(nint window, nint after)
        {
            Orders.Add((window, after));
            var row = Windows.Single(w => w.Handle == window);
            Windows.Remove(row);
            bool topmost = after == -1 || (after > 1 && Windows.Single(w => w.Handle == after).Topmost);
            if (after == 0) topmost = row.Topmost;
            row = row with { Topmost = topmost };
            int index = after switch
            {
                -1 => 0,
                -2 => Windows.TakeWhile(w => w.Topmost).Count(),
                0 => row.Topmost ? 0 : Windows.TakeWhile(w => w.Topmost).Count(),
                1 => Windows.Count,
                _ => Windows.FindIndex(w => w.Handle == after) + 1
            };
            Windows.Insert(index, row);
        }
        public IDisposable Watch(Action changed) { ActiveWatches++; return new WatchLease(() => ActiveWatches--); }
        private sealed class WatchLease(Action release) : IDisposable { public void Dispose() => release(); }
    }

    private static T Find<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static object? Call(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void OffscreenUi()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = System.Windows.Application.Current as App ?? new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (System.Windows.Application.Current.Resources.MergedDictionaries.Count == 0) app.InitializeComponent();
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
        string directory = Path.Combine(AppContext.BaseDirectory, "isolated-settings", Guid.NewGuid().ToString("N"));
        MainWindow NewWindow() => new(directory, desktopIntegrationEnabled: false)
        {
            Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false
        };
        var window = NewWindow();
        try
        {
            StartupAndRestoreSurface(directory);
            window.Show(); Pump();
            SettingsWindowMovement(window);
            var gradientProfile = (DisplayProfile)Find<ListBox>(window, "DisplayTabs").SelectedItem;
            Find<Wpf.Ui.Controls.ToggleSwitch>(window, "GradientEnabledBox").IsChecked = true;
            Find<System.Windows.Controls.CheckBox>(window, "GradientIndependentEdgesBox").IsChecked = true;
            Find<Slider>(window, "GradientLeftWidthSlider").Value = 0.15;
            Find<Slider>(window, "GradientRightOpacitySlider").Value = 0.4;
            Require(gradientProfile.GradientEnabled && gradientProfile.GradientIndependentEdges && gradientProfile.GradientLeftWidth == 0.15 && gradientProfile.GradientRightOpacity == 0.4,
                "Independent gradient enable/edge controls did not update the selected profile.");
            Find<Wpf.Ui.Controls.ToggleSwitch>(window, "GradientEnabledBox").IsChecked = false;
            Require(!gradientProfile.GradientEnabled, "Gradient toggle did not disable its independent effect.");
            Find<ComboBox>(window, "GradientModeBox").SelectedIndex = 0;
            Find<Slider>(window, "NoiseGrainSlider").Value = 6.5;
            Require(((DisplayProfile)Find<ListBox>(window, "DisplayTabs").SelectedItem).NoiseGrainPixels == 6.5 && Find<TextBlock>(window, "NoiseGrainValue").Text == "6.5 px",
                "Dither grain slider did not update its selected profile/live label.");
            Find<Slider>(window, "NoiseGrainSlider").Value = 1;
            var selected = (DisplayProfile)Find<ListBox>(window, "DisplayTabs").SelectedItem;
            string device = selected.DeviceName;
            Find<System.Windows.Controls.TextBox>(window, "NicknameBox").Text = "Desk screen";
            Call(window, "ApplyNickname_OnClick", window, new RoutedEventArgs());
            Require(selected.TabTitle == "Desk screen" && selected.TabSubtitle.StartsWith("Display "), "UI nickname lost the original number.");
            var profiles = Find<ListBox>(window, "DisplayTabs").Items.Cast<DisplayProfile>().ToList();
            var identities = profiles.Select(p => (p.DeviceName, p.Nickname, p.Enabled)).ToArray();
            Find<Slider>(window, "StrengthSlider").Value = 0.43;
            Find<Slider>(window, "CropZoomSlider").Value = 2.2;
            Find<ComboBox>(window, "LayerBox").SelectedIndex = (int)OverlayLayer.Bottom;
            Call(window, "ApplyAll_OnClick", window, new RoutedEventArgs());
            Require(profiles.All(p => p.Strength == 0.43 && p.CropZoom == 2.2 && p.Layer == OverlayLayer.Bottom), "Apply-to-all did not copy visual/framing/layer settings.");
            Require(profiles.Select(p => (p.DeviceName, p.Nickname, p.Enabled)).SequenceEqual(identities), "Apply-to-all changed identities or enabled states.");
            var tabs = Find<ListBox>(window, "DisplayTabs");
            if (profiles.Count > 1)
            {
                tabs.SelectedItem = profiles[1];
                Find<Wpf.Ui.Controls.ToggleSwitch>(window, "FlipHorizontalBox").IsChecked = true;
                Require(profiles[1].FlipHorizontal && !profiles[0].FlipHorizontal, "Flip toggle affected the wrong display profile.");
                tabs.SelectedItem = profiles[0];
                Find<Wpf.Ui.Controls.ToggleSwitch>(window, "FlipVerticalBox").IsChecked = true;
                Require(profiles[0].FlipVertical && !profiles[1].FlipVertical, "Vertical flip depended on a zoom edit/profile switch.");
            }
            Find<System.Windows.Controls.RadioButton>(window, "CpuBackendButton").IsChecked = true;
            Require(Find<TextBlock>(window, "ProcessingEngineText").Text == "CPU", "CPU selection did not update UI.");
            Find<System.Windows.Controls.RadioButton>(window, "ApplicationNav").IsChecked = true;
            Require(Find<StackPanel>(window, "ApplicationPage").Visibility == Visibility.Visible, "App settings navigation failed.");
            var shortcut = new ShortcutDefinition { Modifiers = 3, VirtualKey = KeyInterop.VirtualKeyFromKey(Key.F10) };
            Require((bool)Call(window, "SetShortcut", shortcut)!, "Isolated shortcut preferences failed.");
            Find<Wpf.Ui.Controls.ToggleSwitch>(window, "CloseToTrayBox").IsChecked = true;
            Require(Find<Wpf.Ui.Controls.ToggleSwitch>(window, "CloseToTrayBox").IsChecked == true, "Tray switch did not turn on.");
            Call(window, "SaveButton_OnClick", window, new RoutedEventArgs());
            Pump();
            Capture(window, "app-settings.png");
            window.Width = 900; window.Height = 650;
            Find<System.Windows.Controls.RadioButton>(window, "AppearanceNav").IsChecked = true; Pump();
            Capture(window, "minimum-size.png");
            var title = Find<Wpf.Ui.Controls.TitleBar>(window, "WindowTitleBar");
            Require(BuildInfo.Version.StartsWith("2.") && BuildInfo.Number != "unknown" && Find<TextBlock>(window, "BuildVersionText").Text == BuildInfo.DisplayLabel,
                "Visible 2.x version/build metadata did not match the compiled assembly.");
            Require(title.Header is null && title.Icon is null && string.IsNullOrEmpty(title.Title) && title.Height == 32, "Compact title bar still showed duplicate branding.");
            Require(((System.Windows.Media.SolidColorBrush)title.Background).Color.A == 0, "Title bar stacks a solid or second tinted surface over the client backdrop.");
            Find<System.Windows.Controls.RadioButton>(window, "PerformanceNav").IsChecked = true; Pump(); Capture(window, "layers.png");
            window.Close();
            window = NewWindow(); window.Show(); Pump();
            var reloaded = Find<ListBox>(window, "DisplayTabs").Items.Cast<DisplayProfile>().Single(p => p.DeviceName == device);
            Require(reloaded.Nickname == "Desk screen", "UI nickname did not persist.");
            Require(reloaded.Layer == OverlayLayer.Bottom && reloaded.Strength == 0.43, "Layer/apply-to-all settings did not persist.");
            Require(Find<System.Windows.Controls.RadioButton>(window, "CpuBackendButton").IsChecked == true, "CPU preference did not persist.");
            Require(Find<Wpf.Ui.Controls.ToggleSwitch>(window, "CloseToTrayBox").IsChecked == true, "Tray preference did not persist.");
            Require(Find<System.Windows.Controls.TextBox>(window, "ShortcutBox").Text == "Ctrl + Alt + F10", "Shortcut preference did not persist.");
            var toggle = Find<Wpf.Ui.Controls.Button>(window, "StartButton");
            Require(window.FindName("StopButton") is null && (string)toggle.Content == "Start overlay", "Overlay controls are not a single Start/Stop toggle.");
            var host = (RendererProcessHost)typeof(MainWindow).GetField("_rendererHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            foreach (var profile in Find<ListBox>(window, "DisplayTabs").Items.Cast<DisplayProfile>())
            {
                profile.Enabled = true;
                profile.Mode = OverlayMode.Veil;
            }
            Call(window, "StartButton_OnClick", toggle, new RoutedEventArgs());
            for (int i = 0; i < 100 && !Find<TextBlock>(window, "StatusText").Text.Contains(" running ·"); i++) Pump();
            Require(host.ProcessId is not null && (string)toggle.Content == "Stop overlay" && Find<TextBlock>(window, "StatusText").Text.Contains(" running ·"), "Start toggle did not start an off-screen worker.");
            Call(window, "StartButton_OnClick", toggle, new RoutedEventArgs());
            for (int i = 0; i < 100 && host.ProcessId is not null; i++) Pump();
            Require(host.ProcessId is null && (string)toggle.Content == "Start overlay", "Second toggle click did not stop the worker.");
            Find<Slider>(window, "CropLeftSlider").Value = 20;
            Require(((DisplayProfile)Find<ListBox>(window, "DisplayTabs").SelectedItem).CropLeft == 0.2, "Expandable crop control did not update its profile.");
            var canvas = window.AddCanvas(new CanvasBounds(-600, 120, 400, 250));
            Require(Find<ListBox>(window, "DisplayTabs").SelectedItem == canvas && canvas.Canvas!.Left == -600, "Custom canvas did not become the selected target.");
            Call(window, "SaveButton_OnClick", window, new RoutedEventArgs());
            Find<System.Windows.Controls.RadioButton>(window, "FramingNav").IsChecked = true;
            var cropCard = Find<System.Windows.Controls.Border>(window, "SourceCropCard");
            ((System.Windows.Controls.Expander)cropCard.Child).IsExpanded = true;
            Pump(); Capture(window, "source-crop-canvases.png");
            window.Close(); window = NewWindow(); window.Show(); Pump();
            var loadedCanvas = Find<ListBox>(window, "DisplayTabs").Items.Cast<DisplayProfile>().Single();
            Require(loadedCanvas.Canvas == new CanvasBounds(-600, 120, 400, 250) && loadedCanvas.CropLeft == 0.2, "Custom canvas geometry/crop/target mode did not persist.");
            Call(window, "DeleteCanvas_OnClick", window, new RoutedEventArgs());
            Require(Find<ListBox>(window, "DisplayTabs").Items.Count == 0, "Deleting the final custom canvas left a stale tab.");
            Require(Find<ScrollViewer>(window, "SettingsScroll").IsEnabled && Find<StackPanel>(window, "ApplicationPage").IsEnabled,
                "Empty custom canvases disabled global settings/profile loading.");
            Require(typeof(MainWindow).GetField("_hotkey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null &&
                typeof(MainWindow).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null, "Offscreen checks unexpectedly enabled desktop integration.");
            AdvancedUi(window, directory);
            window = NewWindow(); window.Show(); Pump();
            Require(!Find<Wpf.Ui.Controls.ToggleSwitch>(window, "AcrylicBox").IsChecked!.Value && Find<ComboBox>(window, "ThemeBox").SelectedIndex == 2,
                "Acrylic/light theme preferences did not survive reopening.");
            Require(Find<ComboBox>(window, "PresetBox").Items.Count == 2 && window.CaptureSetup().Canvases.Count == 2, "Named profiles/canvases did not survive reopening.");
            AcrylicQuickProfilesAndAutomation(window, directory);
            ImprovementUi(window, directory);
            Capture(window, "presets-light.png");
            Console.WriteLine("PASS: offscreen engine selector, nickname editing, app page, shortcut/tray persistence, title bar and minimum-size layout.");
        }
        finally { window.Close(); app.Shutdown(); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
    private static void StartupAndRestoreSurface(string directory)
    {
        foreach (bool dark in new[] { true, false })
        {
            string path = Path.Combine(directory, "startup-surface", dark ? "dark" : "light");
            SettingsStorage.Save(Path.Combine(path, "app-settings.json"), new ApplicationSettings
            {
                Theme = dark ? InterfaceTheme.Dark : InterfaceTheme.Light, Acrylic = true, AcrylicTransparency = 0.85, CloseToTray = true
            });
            string saved = File.ReadAllText(Path.Combine(path, "app-settings.json"));
            var probe = new MainWindow(path, desktopIntegrationEnabled: false)
            {
                Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                var title = Find<Wpf.Ui.Controls.TitleBar>(probe, "WindowTitleBar");
                // Model a late library/chrome initialization replacing the
                // caption brush after OnSourceInitialized has applied acrylic.
                probe.Loaded += (_, _) => title.Background = System.Windows.Media.Brushes.Gray;
                probe.Show(); Pump(); Pump();
                void Check()
                {
                    nint handle = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
                    Require(probe.StartupSurfaceReady && ((System.Windows.Media.SolidColorBrush)title.Background).Color.A == 0 && NativeAcrylic.GetBackdrop(handle) == 3,
                        "Startup/restore left an opaque titlebar or lost the native acrylic backdrop.");
                    Require(Find<Slider>(probe, "AcrylicTransparencySlider").Value == 85 && File.ReadAllText(Path.Combine(path, "app-settings.json")) == saved,
                        "Surface repair changed slider/preferences instead of applying the saved appearance.");
                }
                Check();
                int revision = probe.AppearanceRevision;
                for (int restore = 0; restore < 4; restore++)
                {
                    probe.Hide(); Pump();
                    title.Background = System.Windows.Media.Brushes.Gray;
                    probe.RestoreSettingsWindow(activate: false); Pump(); Pump();
                    Check();
                    Require(probe.AppearanceRevision == revision, "Tray surface restoration unnecessarily rebuilt the theme.");
                }
            }
            finally { probe.Close(); Pump(); }
        }
        Console.WriteLine("PASS: saved dark/light acrylic titlebar after first render and repeated tray-style restoration, without slider edits or theme rebuilds.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MovementRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out MovementRect rectangle);

    private static void SettingsWindowMovement(MainWindow window)
    {
        Require(System.Windows.Media.RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly,
            "Settings window still uses WPF hardware composition during monitor changes.");
        Require(Find<TextBlock>(window, "TaglineText").Text == "A seamless backdrop for your setup", "Tagline is not sentence case.");
        string setup = window.CaptureSetup().Fingerprint();
        var acrylic = Find<Wpf.Ui.Controls.ToggleSwitch>(window, "AcrylicBox");
        var theme = Find<ComboBox>(window, "ThemeBox");
        bool originalAcrylic = acrylic.IsChecked == true; int originalTheme = theme.SelectedIndex;
        nint handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        nint rectangle = Marshal.AllocHGlobal(Marshal.SizeOf<MovementRect>());
        try
        {
            var scales = new[] { 96, 144, 192, 120, 240, 96 };
            for (int cycle = 0; cycle < 12; cycle++)
            {
                int revision = window.AppearanceRevision;
                SendMessage(handle, 0x0231, 0, 0); // enter a move/resize sequence
                Require(window.SettingsWindowMoving, "Window movement hook did not observe move start.");
                acrylic.IsChecked = !originalAcrylic;
                theme.SelectedIndex = cycle % 2 == 0 ? 1 : 2;
                for (int move = 0; move < 16; move++)
                {
                    int dpi = scales[move % scales.Length];
                    int x = -30000 - (move % 2) * 2000, y = -30000 - (cycle % 2) * 2000;
                    int width = 1080 * dpi / 96, height = 850 * dpi / 96;
                    Marshal.StructureToPtr(new MovementRect { Left = x, Top = y, Right = x + width, Bottom = y + height }, rectangle, false);
                    SendMessage(handle, 0x02E0, new nint(dpi | (dpi << 16)), rectangle); // valid synthetic WM_DPICHANGED
                    Require(SetWindowPos(handle, 0, x, y, width, height, 0x0214), "Off-screen move failed.");
                    Call(window, "WindowsPreferenceChanged", window, new Microsoft.Win32.UserPreferenceChangedEventArgs(Microsoft.Win32.UserPreferenceCategory.General));
                    if (move % 4 == 0) Pump();
                    Require(window.AppearanceRevision == revision, "Appearance resources/backdrop were rebuilt while dragging.");
                    Require(GetWindowRect(handle, out var actual) && actual.Right < -10000 && actual.Bottom < -10000, "Movement stress left the off-screen test area.");
                }
                SendMessage(handle, 0x0232, 0, 0);
                for (int i = 0; i < 20 && window.AppearanceRevision == revision; i++) Pump();
                Require(!window.SettingsWindowMoving && window.AppearanceRevision == revision + 1,
                    "Deferred appearance change did not apply exactly once after dragging.");
                Call(window, "WindowsPreferenceChanged", window, new Microsoft.Win32.UserPreferenceChangedEventArgs(Microsoft.Win32.UserPreferenceCategory.General));
                Pump(); Pump(); Pump();
                Require(window.AppearanceRevision == revision + 1, "An unchanged preference notification rebuilt the window theme.");
            }
        }
        finally
        {
            SendMessage(handle, 0x0232, 0, 0);
            Marshal.FreeHGlobal(rectangle);
            theme.SelectedIndex = originalTheme; acrylic.IsChecked = originalAcrylic;
            window.Width = 1080; window.Height = 850;
            SetWindowPos(handle, 0, -30000, -30000, 1080, 850, 0x0214);
            Pump(); Pump();
        }
        Require(window.CaptureSetup().Fingerprint() == setup, "Movement/DPI changes modified setup profiles.");
        Console.WriteLine("PASS: 192 rapid off-screen moves/synthetic DPI changes, deferred/coalesced appearance updates, software UI rendering and sentence-case tagline.");
    }

    private static void AdvancedUi(MainWindow window, string directory)
    {
        Require(Find<Wpf.Ui.Controls.ToggleSwitch>(window, "AcrylicBox").IsChecked == true && window.WindowBackdropType == Wpf.Ui.Controls.WindowBackdropType.Acrylic,
            "Acrylic was not enabled by default.");
        Find<ComboBox>(window, "ThemeBox").SelectedIndex = 2;
        Find<Wpf.Ui.Controls.ToggleSwitch>(window, "AcrylicBox").IsChecked = false;
        var preferences = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(Path.Combine(directory, "app-settings.json")))!;
        Require(preferences.Theme == InterfaceTheme.Light && !preferences.Acrylic && !window.IsDarkTheme && window.WindowBackdropType == Wpf.Ui.Controls.WindowBackdropType.None,
            "Appearance settings did not apply/save immediately.");
        Find<ComboBox>(window, "ThemeBox").SelectedIndex = 1;
        Find<Wpf.Ui.Controls.ToggleSwitch>(window, "AcrylicBox").IsChecked = true;
        Require(window.IsDarkTheme && ((System.Windows.Media.SolidColorBrush)((System.Windows.Controls.Grid)window.Content).Background).Color.A < 255,
            "Dark acrylic did not use a translucent client surface.");
        Pump(); Capture(window, "dark-acrylic.png");
        Find<ComboBox>(window, "ThemeBox").SelectedIndex = 2;
        Find<Wpf.Ui.Controls.ToggleSwitch>(window, "AcrylicBox").IsChecked = false;
        var darkMenu = new TrayMenuColors(true); var lightMenu = new TrayMenuColors(false);
        Require(darkMenu.ToolStripDropDownBackground.GetBrightness() < 0.25 && lightMenu.ToolStripDropDownBackground.GetBrightness() > 0.8,
            "Tray menus do not have distinct readable light/dark surfaces.");
        var veil = window.AddCanvas(new CanvasBounds(-600, 120, 400, 250));
        Find<ComboBox>(window, "ModeBox").SelectedIndex = 1;
        Find<System.Windows.Controls.TextBox>(window, "VeilColorBox").Text = "#204080";
        Call(window, "ApplyVeilColor_OnClick", window, new RoutedEventArgs());
        Require(veil.VeilColor == "#204080" && Find<System.Windows.Controls.Border>(window, "VeilColorCard").Visibility == Visibility.Visible &&
            Find<System.Windows.Controls.Border>(window, "BlurCard").Visibility == Visibility.Collapsed && Find<System.Windows.Controls.StackPanel>(window, "BrightnessControls").Visibility == Visibility.Collapsed &&
            Find<System.Windows.Controls.RadioButton>(window, "FramingNav").Visibility == Visibility.Collapsed && Find<System.Windows.Controls.Border>(window, "ProcessingQualityCard").Visibility == Visibility.Collapsed,
            "Veil mode did not expose color/hide irrelevant controls.");
        var border = window.AddCanvas(new CanvasBounds(100, 50, 640, 480));
        Find<ComboBox>(window, "ModeBox").SelectedIndex = 2;
        Require(window.SetBorderImage(TestBorderPath), "Custom image could not be loaded in the UI.");
        Find<ComboBox>(window, "BorderScaleBox").SelectedIndex = 2;
        Find<Slider>(window, "ImageZoomSlider").Value = 1.25;
        Find<Slider>(window, "ImageOffsetXSlider").Value = 12;
        Require(border.BorderScale == BorderScaleMode.Stretch && border.ImageZoom == 1.25 && border.ImageOffsetX == 12 && string.IsNullOrEmpty(veil.ImagePath),
            "Per-canvas image settings leaked to a different canvas.");
        Find<System.Windows.Controls.TextBox>(window, "ImageZoomBox").Text = "1.25";
        Find<System.Windows.Controls.TextBox>(window, "ImageOffsetXBox").Text = "12.5";
        Find<System.Windows.Controls.TextBox>(window, "ImageOffsetYBox").Text = "-3";
        Call(window, "ApplyImageTransform_OnClick", window, new RoutedEventArgs());
        Require(border.ImageOffsetX == 12.5 && border.ImageOffsetY == -3, "Exact image positioning fields did not apply.");
        Find<System.Windows.Controls.RadioButton>(window, "FramingNav").IsChecked = true; Pump(); Capture(window, "image-border-framing.png");
        Require(window.SavePresetAs("Arcade"), "Could not save named whole-setup preset.");
        var arcade = (SetupPreset)Find<ComboBox>(window, "PresetBox").SelectedItem;
        Require(!window.HasUnsavedChanges && arcade.Setup.Canvases.Count == 2 && arcade.Setup.Canvases[0].VeilColor == "#204080" && arcade.Setup.Canvases[1].ImagePath == TestBorderPath,
            "Named setup omitted colors/images/canvases or stayed dirty.");
        Find<ComboBox>(window, "ModeBox").SelectedIndex = 1;
        Require(window.SavePresetAs("Quiet"), "Could not save second preset.");
        var quiet = (SetupPreset)Find<ComboBox>(window, "PresetBox").SelectedItem;
        Find<Slider>(window, "StrengthSlider").Value = 0.2;
        Require(window.HasUnsavedChanges, "Live edits were not tracked as unsaved.");
        int prompts = 0;
        window.UnsavedPrompt = () => { prompts++; return MessageBoxResult.Cancel; };
        Require(!window.LoadPresetAsync(arcade.Id).GetAwaiter().GetResult() && window.HasUnsavedChanges && Find<Slider>(window, "StrengthSlider").Value == 0.2,
            "Cancel discarded an unsaved preset edit.");
        window.UnsavedPrompt = () => { prompts++; return MessageBoxResult.Yes; };
        Require(window.LoadPresetAsync(arcade.Id).GetAwaiter().GetResult() && !window.HasUnsavedChanges && prompts == 2,
            "Save-before-switch failed.");
        Require(window.CaptureSetup().Canvases[1].Mode == OverlayMode.ImageBorder && window.CaptureSetup().Canvases[1].ImageZoom == 1.25,
            "Loading a profile did not restore image/framing settings.");
        var direct = new ShortcutDefinition { Modifiers = 3, VirtualKey = KeyInterop.VirtualKeyFromKey(Key.F11) };
        var next = new ShortcutDefinition { Modifiers = 3, VirtualKey = KeyInterop.VirtualKeyFromKey(Key.F12) };
        Require(window.SetPresetShortcut(arcade.Id, direct) && window.SetPresetShortcut("Next", next) && !window.SetPresetShortcut("Previous", next),
            "Preset shortcut validation/conflict handling failed.");
        var saved = JsonSerializer.Deserialize<List<SetupPreset>>(File.ReadAllText(Path.Combine(directory, "presets.json")))!;
        Require(saved.Single(p => p.Id == arcade.Id).Shortcut.VirtualKey == direct.VirtualKey && saved.Single(p => p.Id == quiet.Id).Setup.Canvases[1].Strength == 0.2,
            "Direct shortcut or save-before-switch did not persist.");
        window.CyclePresetAsync(1).GetAwaiter().GetResult();
        Require(window.CaptureSetup().Canvases[1].Mode == OverlayMode.Veil && window.CaptureSetup().Canvases[1].Strength == 0.2, "Next preset did not load the saved setup.");
        window.CyclePresetAsync(-1).GetAwaiter().GetResult();
        Require(window.CaptureSetup().Canvases[1].Mode == OverlayMode.ImageBorder, "Previous preset did not load the saved setup.");
        Find<Slider>(window, "StrengthSlider").Value = 0.1;
        window.UnsavedPrompt = () => MessageBoxResult.No;
        Require(window.LoadPresetAsync(quiet.Id).GetAwaiter().GetResult() && !window.HasUnsavedChanges &&
            window.CaptureSetup().Canvases[0].Strength != 0.1, "Continue-without-saving did not discard edits on a profile switch.");
        window.CyclePresetAsync(-1).GetAwaiter().GetResult();
        Find<System.Windows.Controls.RadioButton>(window, "ApplicationNav").IsChecked = true; Pump(); Capture(window, "profiles-acrylic-settings.png");
        Find<Slider>(window, "StrengthSlider").Value = 0.33;
        window.UnsavedPrompt = () => MessageBoxResult.Cancel;
        window.Close(); Require(window.IsVisible && window.HasUnsavedChanges, "Cancel did not prevent closing an unsaved setup.");
        window.UnsavedPrompt = () => MessageBoxResult.Yes;
        window.Close(); Require(!window.IsVisible && !window.HasUnsavedChanges, "Save-before-close did not persist the setup.");
        Console.WriteLine("PASS: whole-setup presets, cycle/direct hotkey persistence/conflicts, unsaved Save/Cancel, instant theme/acrylic preferences, tray colors and mode-specific controls.");
    }

    private sealed class CloseTestApp : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e) { } // no production window, hotkeys or tray
    }
    private static void RunCloseLifecycleChild()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "FeatureRegression.exe");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable, "--close-lifecycle")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        try
        {
            Require(process.WaitForExit(45000), "Single-close lifecycle child did not return to its caller.");
            string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
            Require(process.ExitCode == 0, "Single-close lifecycle regression failed: " + output + error);
            Console.Write(output);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
    }
    private static void CloseLifecycleRegression()
    {
        var app = new CloseTestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            foreach (string backend in new[] { "Idle", "Cpu", "Gpu" })
            {
                string directory = Path.Combine(AppContext.BaseDirectory, "isolated-close", Guid.NewGuid().ToString("N"));
                var window = new MainWindow(directory, desktopIntegrationEnabled: false)
                {
                    Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false
                };
                app.MainWindow = window;
                bool closing = false, reshown = false; int closed = 0, closeEvents = 0;
                window.IsVisibleChanged += (_, e) => { if (closing && e.NewValue is true) reshown = true; };
                window.Closing += (_, _) => closeEvents++;
                window.Closed += (_, _) => closed++;
                window.Show(); Pump();
                Find<Wpf.Ui.Controls.ToggleSwitch>(window, "CloseToTrayBox").IsChecked = false;
                window.AddCanvas(new CanvasBounds(-30000, -30000, 64, 64));
                Find<ComboBox>(window, "ModeBox").SelectedIndex = 1;
                Find<Wpf.Ui.Controls.ToggleSwitch>(window, "NoiseBox").IsChecked = false;
                Find<System.Windows.Controls.RadioButton>(window, backend == "Cpu" ? "CpuBackendButton" : "GpuBackendButton").IsChecked = true;
                Call(window, "SaveButton_OnClick", window, new RoutedEventArgs());
                var host = (RendererProcessHost)typeof(MainWindow).GetField("_rendererHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                int? worker = null;
                if (backend != "Idle")
                {
                    Call(window, "StartButton_OnClick", window, new RoutedEventArgs());
                    for (int i = 0; i < 150 && !Find<TextBlock>(window, "StatusText").Text.Contains(" running ·"); i++) Pump();
                    Require(host.ProcessId is not null && Find<TextBlock>(window, "StatusText").Text.Contains(" running ·"), $"{backend} close fixture did not render off-screen.");
                    worker = host.ProcessId;
                }
                closing = true;
                window.Close(); // one user close only; completion may close internally after shutdown
                Call(window, "ShowFromTray"); // stale queued reopen requests must not revive shutdown
                for (int i = 0; i < 150 && closed == 0; i++) Pump();
                Require(closed == 1 && !window.IsVisible && !reshown && host.ProcessId is null,
                    $"{backend}: one Close did not finish, re-showed the window, or left a renderer attached.");
                Require(backend != "Idle" || closeEvents == 1, "Idle Close was unnecessarily cancelled/reissued.");
                if (worker is { } pid)
                {
                    bool exited;
                    try { using var child = System.Diagnostics.Process.GetProcessById(pid); exited = child.HasExited; }
                    catch (ArgumentException) { exited = true; }
                    Require(exited, "Window closed before the owned renderer process exited.");
                }
                Pump(); Pump(); Require(!reshown && closed == 1, "A deferred callback reopened a closed settings window.");
            }
            Console.WriteLine("PASS: one-close exit for idle, CPU and GPU states; no re-show, stale tray reopen, or surviving renderer worker.");
        }
        finally { app.Shutdown(); }
    }

    private static void AwaitUi(Task task)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(30)) Pump();
        Require(task.IsCompleted, "UI operation timed out."); task.GetAwaiter().GetResult();
    }
    private static void AcrylicQuickProfilesAndAutomation(MainWindow window, string directory)
    {
        string contended = Path.Combine(directory, "contended-settings.json");
        SettingsStorage.Save(contended, new { Value = 1 });
        var reader = new FileStream(contended, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(() => { Thread.Sleep(40); reader.Dispose(); });
        try { SettingsStorage.Save(contended, new { Value = 2 }); }
        finally { release.GetAwaiter().GetResult(); reader.Dispose(); }
        Require(JsonDocument.Parse(File.ReadAllText(contended)).RootElement.GetProperty("Value").GetInt32() == 2, "Atomic settings save did not recover from brief reader contention.");
        Find<System.Windows.Controls.RadioButton>(window, "ApplicationNav").IsChecked = true;
        Require(Find<System.Windows.Controls.Border>(window, "QuickProfileBar").Visibility == Visibility.Visible,
            "Quick profile actions were hidden on global settings.");
        Find<Wpf.Ui.Controls.ToggleSwitch>(window, "AcrylicBox").IsChecked = true;
        Find<Slider>(window, "AcrylicTransparencySlider").Value = 85;
        var preferences = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(Path.Combine(directory, "app-settings.json")))!;
        nint settingsHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Require(Math.Abs(preferences.AcrylicTransparency - 0.85) < 0.00001 && NativeAcrylic.TintAlpha(0.85) is >= 37 and <= 39 && NativeAcrylic.GetBackdrop(settingsHandle) == 3,
            "Acrylic transparency did not save its tint or retain a real DWM acrylic backdrop.");
        Require(NativeAcrylic.TintAlpha(0) == 255 && NativeAcrylic.TintAlpha(1) == 0, "Acrylic tint endpoints failed.");
        Find<Wpf.Ui.Controls.ToggleSwitch>(window, "AcrylicBox").IsChecked = false;
        Require(NativeAcrylic.GetBackdrop(settingsHandle) == 1 && ((System.Windows.Media.SolidColorBrush)((System.Windows.Controls.Grid)window.Content).Background).Color.A == 255,
            "Disabling acrylic did not remove the native material and provide an opaque client surface.");
        Find<Wpf.Ui.Controls.ToggleSwitch>(window, "AcrylicBox").IsChecked = true;
        Find<Slider>(window, "AcrylicTransparencySlider").Value = 100;
        Require(NativeAcrylic.GetBackdrop(settingsHandle) == 3, "Clear tint disabled the underlying acrylic and exposed a black client.");
        Find<Slider>(window, "AcrylicTransparencySlider").Value = 85;
        Require(((System.Windows.Media.SolidColorBrush)Find<Wpf.Ui.Controls.TitleBar>(window, "WindowTitleBar").Background).Color.A == 0,
            "Acrylic titlebar is not unified with the client backdrop.");
        var profiles = Find<ComboBox>(window, "PresetBox").Items.Cast<SetupPreset>().ToList();
        var original = profiles.Single(p => p.Name == "Arcade");
        var quiet = profiles.Single(p => p.Name == "Quiet");
        var quick = window.BuildQuickProfilesMenu();
        Require(quick.Items.OfType<System.Windows.Controls.MenuItem>().Any(item => item.Header is string header && header == original.Name && item.IsChecked), "Quick picker did not show/check the active profile.");
        Require(window.TryDispatchLocalShortcut(original.Shortcut.VirtualKey, original.Shortcut.Modifiers), "Open-window direct shortcut was not recognized.");
        Require(window.TryDispatchLocalShortcut(KeyInterop.VirtualKeyFromKey(Key.F12), 3, inputTimestamp: 1000), "Open-window next-profile shortcut was not recognized.");
        Pump();
        Require(Find<TextBlock>(window, "QuickProfileName").Text == quiet.Name, "Focused-window next-profile shortcut did not switch profiles.");
        Require(!window.InvokeShortcutAsync("Next", local: false, inputTimestamp: 1000).GetAwaiter().GetResult() && Find<TextBlock>(window, "QuickProfileName").Text == quiet.Name,
            "Local/global delivery double-fired one shortcut press.");
        Require(window.TryDispatchLocalShortcut(KeyInterop.VirtualKeyFromKey(Key.F12), 3, repeat: true) && Find<TextBlock>(window, "QuickProfileName").Text == quiet.Name,
            "A held focused-window shortcut repeated profile switching.");
        AwaitUi(window.LoadPresetAsync(original.Id));
        AwaitUi(window.InvokeShortcutAsync("Next", local: true, inputTimestamp: 2000));
        Require(!window.InvokeShortcutAsync("Next", local: false, inputTimestamp: 2000).GetAwaiter().GetResult(), "One timestamped input was delivered twice.");
        AwaitUi(window.InvokeShortcutAsync("Next", local: false, inputTimestamp: 2100));
        Require(Find<TextBlock>(window, "QuickProfileName").Text == original.Name, "A separate fast press on the other shortcut route was incorrectly suppressed.");
        Find<System.Windows.Controls.TextBox>(window, "VeilColorBox").Text = "#112233";
        Call(window, "ApplyVeilColor_OnClick", window, new RoutedEventArgs());
        Require(window.SavePresetAs("Game auto"), "Could not save automated game profile.");
        var game = (SetupPreset)Find<ComboBox>(window, "PresetBox").SelectedItem;
        Find<System.Windows.Controls.TextBox>(window, "VeilColorBox").Text = "#334455";
        Call(window, "ApplyVeilColor_OnClick", window, new RoutedEventArgs());
        Require(window.SavePresetAs("Other auto"), "Could not save second automated profile: " + Find<TextBlock>(window, "ApplicationSettingsStatusText").Text);
        var other = (SetupPreset)Find<ComboBox>(window, "PresetBox").SelectedItem;
        AwaitUi(window.LoadPresetAsync(original.Id));
        string executable = Path.Combine(AppContext.BaseDirectory, "FeatureRegression.exe");
        string otherExecutable = Path.Combine(AppContext.BaseDirectory, "AmbientMonitor.exe");
        Require(window.SetExecutableAutomation(game.Id, executable, true) && window.SetExecutableAutomation(other.Id, otherExecutable, true), "Executable rules did not save.");
        var rules = JsonSerializer.Deserialize<List<SetupPreset>>(File.ReadAllText(Path.Combine(directory, "presets.json")))!;
        Require(rules.Single(p => p.Id == game.Id).Automation.Enabled && rules.Single(p => p.Id == game.Id).Automation.ExecutablePath == executable, "Executable automation did not persist immediately.");
        Require(!ExecutableProcessReader.Matches(executable, Path.Combine(Path.GetDirectoryName(executable)!, "another-folder", "FeatureRegression.exe")) && ExecutableProcessReader.Matches(executable, executable.ToUpperInvariant()),
            "Process matching confused identical filenames in different folders or ignored Windows path casing.");
        string before = window.CaptureSetup().Fingerprint();
        string savedDisplays = File.ReadAllText(Path.Combine(directory, "settings.json"));
        string savedCanvases = File.ReadAllText(Path.Combine(directory, "canvases.json"));
        var first = new RunningExecutable(101, executable); var second = new RunningExecutable(102, executable); var alternate = new RunningExecutable(201, otherExecutable);
        var host = (RendererProcessHost)typeof(MainWindow).GetField("_rendererHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        AwaitUi(window.EvaluateAutomationAsync([first, second]));
        Require(window.AutomatedPresetId == game.Id && host.ProcessId is not null && (string)Find<Wpf.Ui.Controls.Button>(window, "StartButton").Content == "Stop overlay",
            "Game launch did not select/start its profile while the settings window was open.");
        Find<Slider>(window, "AcrylicTransparencySlider").Value = 90;
        Call(window, "SaveButton_OnClick", window, new RoutedEventArgs());
        preferences = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(Path.Combine(directory, "app-settings.json")))!;
        Require(preferences.ActivePresetId == original.Id && File.ReadAllText(Path.Combine(directory, "settings.json")) == savedDisplays && File.ReadAllText(Path.Combine(directory, "canvases.json")) == savedCanvases,
            "Temporary automatic activation overwrote the manual startup setup.");
        AwaitUi(window.EvaluateAutomationAsync([second]));
        Require(window.AutomatedPresetId == game.Id && host.ProcessId is not null, "Closing one instance stopped a profile while another instance was running.");
        AwaitUi(window.EvaluateAutomationAsync([]));
        Require(window.AutomatedPresetId is null && host.ProcessId is null && window.CaptureSetup().Fingerprint() == before,
            "Last game exit did not restore the previous stopped setup.");
        Call(window, "StartButton_OnClick", window, new RoutedEventArgs());
        for (int i = 0; i < 100 && !Find<TextBlock>(window, "StatusText").Text.Contains(" running ·"); i++) Pump();
        AwaitUi(window.EvaluateAutomationAsync([]));
        Require(host.ProcessId is not null, "Could not start the baseline off-screen setup.");
        AwaitUi(window.EvaluateAutomationAsync([first])); AwaitUi(window.EvaluateAutomationAsync([]));
        Require(window.AutomatedPresetId is null && host.ProcessId is not null && window.CaptureSetup().Fingerprint() == before,
            "Game exit did not restore a previously running setup.");
        Call(window, "StartButton_OnClick", window, new RoutedEventArgs());
        for (int i = 0; i < 100 && host.ProcessId is not null; i++) Pump();
        AwaitUi(window.EvaluateAutomationAsync([]));
        AwaitUi(window.EvaluateAutomationAsync([first, alternate]));
        Require(window.AutomatedPresetId == game.Id, "Overlapping game rules did not select the first profile deterministically.");
        AwaitUi(window.EvaluateAutomationAsync([alternate]));
        Require(window.AutomatedPresetId == other.Id, "Second running game's profile did not take over after the first game exited.");
        AwaitUi(window.EvaluateAutomationAsync([]));
        Require(window.AutomatedPresetId is null && window.CaptureSetup().Fingerprint() == before, "Chained automatic profiles lost the original return setup.");
        AwaitUi(window.EvaluateAutomationAsync([first]));
        Call(window, "StartButton_OnClick", window, new RoutedEventArgs());
        for (int i = 0; i < 100 && host.ProcessId is not null; i++) Pump();
        AwaitUi(window.EvaluateAutomationAsync([first]));
        Require(window.AutomatedPresetId is null && host.ProcessId is null, "Automatic activation undid a manual Stop while the game was still running.");
        AwaitUi(window.EvaluateAutomationAsync([])); AwaitUi(window.EvaluateAutomationAsync([first]));
        Require(window.AutomatedPresetId == game.Id, "Automation did not re-arm after the executable exited and restarted.");
        AwaitUi(window.EvaluateAutomationAsync([])); AwaitUi(window.LoadPresetAsync(original.Id));
        Find<Slider>(window, "StrengthSlider").Value = 0.11;
        window.SetExecutableAutomation(game.Id, executable, true);
        AwaitUi(window.EvaluateAutomationAsync([first]));
        Require(window.AutomatedPresetId is null && window.HasUnsavedChanges && Find<Slider>(window, "StrengthSlider").Value == 0.11,
            "Automation discarded unsaved manual setup edits.");
        Call(window, "SaveButton_OnClick", window, new RoutedEventArgs()); AwaitUi(window.EvaluateAutomationAsync([first]));
        Find<Slider>(window, "StrengthSlider").Value = 0.52;
        AwaitUi(window.EvaluateAutomationAsync([]));
        Require(host.ProcessId is null && window.HasUnsavedChanges && window.AutomatedPresetId == game.Id && Find<Slider>(window, "StrengthSlider").Value == 0.52,
            "Game exit failed to stop overlays or discarded unsaved game-profile edits.");
        Call(window, "SaveButton_OnClick", window, new RoutedEventArgs()); AwaitUi(window.EvaluateAutomationAsync([]));
        Require(window.AutomatedPresetId is null && !window.HasUnsavedChanges && Find<Slider>(window, "StrengthSlider").Value == 0.11,
            "Saving game-profile edits did not finish restoring the manual setup.");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable, "--automation-fixture") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            try
            {
                for (int i = 0; i < 20 && ExecutableProcessReader.QueryPath(process.Id) is null; i++) Thread.Sleep(50);
                Require(ExecutableProcessReader.Read([executable]).Any(p => p.ProcessId == process.Id && ExecutableProcessReader.Matches(p.Path, executable)),
                    "Live executable path polling did not detect the owned hidden fixture process.");
                process.Kill(); process.WaitForExit();
                Require(!ExecutableProcessReader.Read([executable]).Any(p => p.ProcessId == process.Id), "Exited fixture process remained in the process snapshot.");
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
        }
        Find<System.Windows.Controls.RadioButton>(window, "AppearanceNav").IsChecked = true; Pump(); Capture(window, "quick-profiles-clear-acrylic.png");
        Find<ComboBox>(window, "PresetBox").SelectedItem = game;
        Call(window, "ManagePresets_OnClick", window, new RoutedEventArgs()); Pump(); Capture(window, "executable-automation.png");
        Console.WriteLine("PASS: adjustable native acrylic/titlebar, accessible profile picker, focused/local and global deduped shortcuts, executable activation/exit, multiple instances, manual override, startup isolation and unsaved edit preservation.");
    }

    private static void Capture(MainWindow window, string name)
    {
        window.UpdateLayout();
        var frame = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        // Native DWM backdrops are not in the WPF visual tree. Composite onto
        // a neutral in-memory plate so screenshots show layout/readability
        // without desktop capture or misleading unpremultiplied transparency.
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var plate = new System.Windows.Media.SolidColorBrush(window.IsDarkTheme ? System.Windows.Media.Color.FromRgb(48, 48, 52) : System.Windows.Media.Color.FromRgb(242, 242, 246));
            var bounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
            dc.DrawRectangle(plate, null, bounds);
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(window) { Stretch = System.Windows.Media.Stretch.Fill }, null, bounds);
        }
        frame.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }
}
