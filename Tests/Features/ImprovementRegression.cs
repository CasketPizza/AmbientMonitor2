using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmbientMonitor;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;

internal static partial class Program
{
    private static void RepeatAndGradientPixels()
    {
        using var source = new Bitmap(64, 64);
        using (var paint = Graphics.FromImage(source)) { paint.Clear(System.Drawing.Color.Red); paint.FillRectangle(System.Drawing.Brushes.Blue, 32, 0, 32, 64); }
        using var cpu = new CpuFrameProcessor();
        var settings = new OverlaySettings { Blur = 0, NoiseEnabled = false, Strength = 1, Brightness = 1, CaptureScalePercent = 100, ScaleMode = ImageScaleMode.Fit, RepeatMode = SourceRepeatMode.Repeat, RepeatX = 2 };
        var repeat = cpu.Process(source, settings, 128, 64, 0);
        Require(Pixel(repeat, 8, 32, 2) > 230 && Pixel(repeat, 72, 32, 2) > 230 && Pixel(repeat, 56, 32, 0) > 230, "CPU source repetition did not duplicate a cropped cell.");
        settings.RepeatMode = SourceRepeatMode.Mirror;
        var mirror = cpu.Process(source, settings, 128, 64, 0);
        Require(Pixel(mirror, 72, 32, 0) > 230 && Pixel(mirror, 120, 32, 2) > 230, "CPU mirrored repetition did not alternate cells.");
        settings.RepeatMode = SourceRepeatMode.None; settings.Mode = OverlayMode.Veil; settings.VeilColor = "#FFFFFF";
        settings.GradientMode = CanvasGradientMode.EdgeFade; settings.GradientColor = "#000000"; settings.GradientOpacity = 1; settings.GradientStart = 0.5;
        var faded = cpu.Process(null, settings, 128, 128, 0);
        Require(Pixel(faded, 64, 64, 2) == 255 && Pixel(faded, 1, 64, 2) < 5, "CPU edge gradient failed to preserve center/fade outer edges.");
        settings.GradientEdges = 1;
        Require(Pixel(cpu.Process(null, settings, 128, 128, 0), 126, 64, 2) == 255, "Directional gradient faded an unselected edge.");
        Require(Math.Abs(CanvasEffects.RepeatCoordinate(0.75, 2, SourceRepeatMode.Mirror) - 0.5) < 0.0001 &&
            Math.Abs(CanvasPreview.SnapAxis(199, 200, 600, 4) - 1) < 0.0001, "Repeat mapping or preview guide snapping failed.");
        Console.WriteLine("PASS: repeated/mirrored crop pixels, directional edge gradient and preview guide snapping.");
    }

    private static void HdrFloatPipeline()
    {
        using var renderer = new GpuRenderService(captureDesktop: false, enableHdr: true, forceHdr: true);
        using var layers = new OverlayLayerService(Dispatcher.CurrentDispatcher, new PassiveLayerPlatform());
        var settings = new OverlaySettings { Mode = OverlayMode.Veil, NoiseEnabled = false, Blur = 0, Strength = 1, Brightness = 1, CaptureScalePercent = 100, ScaleMode = ImageScaleMode.Fit };
        var overlay = new NativeGpuOverlay("hdr-test", new Rectangle(-30000, -30000, 64, 64), settings, renderer, layers);
        try
        {
            overlay.Show();
            bool initialized = false;
            for (int attempt = 0; attempt < 100 && !initialized; attempt++)
            {
                Thread.Sleep(50);
                OnGpu(renderer, () => initialized = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().Any(t => t.GetType().GetField("SwapChain")!.GetValue(t) is not null));
            }
            Require(initialized, "HDR verification swap chain did not initialize.");
            OnGpu(renderer, () =>
            {
                var device = (ID3D11Device)GpuField(renderer, "_device"); var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
                var target = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().Single();
                Require((bool)GpuField(target, "FloatOutput"), "HDR path did not create an FP16 scRGB swap chain.");
                using var fixture = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, 64, 64, 1, 1, bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource));
                using var fixtureView = device.CreateRenderTargetView(fixture);
                context.ClearRenderTargetView(fixtureView, new Vortice.Mathematics.Color4(4, 2, 1, 1));
                typeof(GpuRenderService).GetField("_sourceWidth", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(renderer, 64);
                typeof(GpuRenderService).GetField("_sourceHeight", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(renderer, 64);
                typeof(GpuRenderService).GetMethod("CopyDesktopImage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(renderer, [fixture]);
                settings.Mode = OverlayMode.Ambient;
                target.GetType().GetField("Settings")!.SetValue(target, settings);
                using var output = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, 64, 64, 1, 1, bindFlags: BindFlags.RenderTarget));
                using var outputView = device.CreateRenderTargetView(output);
                var bufferField = target.GetType().GetField("BackBuffer")!; var old = bufferField.GetValue(target); bufferField.SetValue(target, outputView);
                float ReadRed()
                {
                    var desc = output.Description; desc.BindFlags = BindFlags.None; desc.Usage = ResourceUsage.Staging; desc.CPUAccessFlags = CpuAccessFlags.Read;
                    using var staging = device.CreateTexture2D(desc); context.CopyResource(staging, output); var mapped = context.Map(staging, 0, MapMode.Read);
                    try { return (float)BitConverter.UInt16BitsToHalf(unchecked((ushort)Marshal.ReadInt16(mapped.DataPointer))); }
                    finally { context.Unmap(staging, 0); }
                }
                void Render() => typeof(GpuRenderService).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(renderer, [target, true, 0f]);
                try
                {
                    target.GetType().GetField("HdrOutput")!.SetValue(target, true); Render();
                    Require(ReadRed() > 3, "HDR highlights were clipped to SDR in the floating-point pipeline.");
                    var work = GpuField(target, "First");
                    Require(((ID3D11Texture2D)work.GetType().GetProperty("Texture")!.GetValue(work)!).Description.Format == Format.R16G16B16A16_Float, "HDR blur/crop cache was not FP16.");
                    target.GetType().GetField("HdrOutput")!.SetValue(target, false); Render();
                    Require(ReadRed() is > 0 and < 1, "HDR source was not tone-mapped for an SDR destination.");
                    settings.GradientMode = CanvasGradientMode.EdgeFade; settings.GradientOpacity = 1; settings.GradientStart = 0.5;
                    Render(); Require(ReadRed() < 0.01, "GPU canvas edge fade was not applied after HDR composite.");
                }
                finally { bufferField.SetValue(target, old); }
            });
            Require(Math.Abs(HdrColor.ToSrgb(HdrColor.ToLinear(0.6)) - 0.6) < 0.000001, "HDR SDR/linear transfer roundtrip failed.");
            Console.WriteLine("PASS: actual FP16/scRGB swap chain and crop cache, synthetic HDR highlight preservation, SDR tone mapping and GPU edge fade (no desktop capture).");
        }
        finally { overlay.Close(); }
    }
    private static void CheckGpuRepeat(GpuRenderService renderer, NativeGpuOverlay overlay)
    {
        OnGpu(renderer, () =>
        {
            var device = (ID3D11Device)GpuField(renderer, "_device"); var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
            using var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 64, 1, 1, bindFlags: BindFlags.ShaderResource));
            var data = new byte[64 * 64 * 4];
            for (int i = 0; i < 64 * 64; i++) { data[i * 4 + (i % 64 < 32 ? 2 : 0)] = 255; data[i * 4 + 3] = 255; }
            context.UpdateSubresource(data, texture, 0, 256);
            typeof(GpuRenderService).GetMethod("CopyDesktopImage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(renderer, [texture]);
        });
        var settings = new OverlaySettings { Mode = OverlayMode.Ambient, Strength = 1, Brightness = 1, Blur = 0, NoiseEnabled = false, CaptureScalePercent = 100,
            ScaleMode = ImageScaleMode.Fit, CropLeft = 0.1, CropRight = 0.25, RepeatMode = SourceRepeatMode.Repeat, RepeatX = 2 };
        foreach (var mode in new[] { SourceRepeatMode.Repeat, SourceRepeatMode.Mirror })
        {
            settings.RepeatMode = mode; overlay.UpdateSettings(settings);
            OnGpu(renderer, () =>
            {
                var device = (ID3D11Device)GpuField(renderer, "_device"); var context = (ID3D11DeviceContext)GpuField(renderer, "_context");
                var target = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().Single(t => ((OverlaySettings)GpuField(t, "Settings")).RepeatX == 2);
                typeof(GpuRenderService).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(renderer, [target, true, 0f]);
                var surface = GpuField(target, "First"); var texture = (ID3D11Texture2D)surface.GetType().GetProperty("Texture")!.GetValue(surface)!;
                var desc = texture.Description; desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read;
                using var staging = device.CreateTexture2D(desc); context.CopyResource(staging, texture); var mapped = context.Map(staging, 0, MapMode.Read);
                try
                {
                    int secondStart = (int)desc.Width / 2 + 4;
                    var pixel = new byte[4]; Marshal.Copy(mapped.DataPointer + (int)(desc.Height / 2 * mapped.RowPitch + secondStart * 4), pixel, 0, 4);
                    Require(mode == SourceRepeatMode.Repeat ? pixel[2] > 240 : pixel[0] > 240, "GPU retained-crop repeat/mirror cell mapping failed.");
                }
                finally { context.Unmap(staging, 0); }
            });
        }
        Console.WriteLine("PASS: GPU cropped-source repeat/mirror pixels and immediate cache invalidation.");
    }
    private static void HdrCompatibilityFallbacks()
    {
        var unsupported = new COMException("Simulated DXGI_ERROR_UNSUPPORTED", unchecked((int)0x887A0004));
        var capture = new HdrCompatibility(); int attempts = 0, fallbackFrames = 0, notices = 0;
        for (int frame = 0; frame < 3; frame++)
        {
            int value = capture.Select<int>(true, () => { attempts++; throw unsupported; }, () => ++fallbackFrames, _ => notices++);
            Require(value == frame + 1, "SDR capture compatibility path did not produce subsequent frames.");
        }
        Require(capture.Rejected && attempts == 1 && notices == 1, "Unsupported HDR capture was retried every frame instead of being latched.");
        var transient = new HdrCompatibility(); bool preserved = false;
        try { transient.Select<int>(true, () => throw new COMException("Access lost", unchecked((int)0x887A0026)), () => -1, _ => { }); }
        catch (COMException) { preserved = true; }
        Require(preserved && !transient.Rejected, "Recoverable access loss was incorrectly treated as unsupported HDR.");

        foreach (string failedStage in new[] { "presentation.fp16", "processing.fp16" })
        {
            int hdrAttempts = 0;
            using var renderer = new GpuRenderService(captureDesktop: false, enableHdr: true, forceHdr: true,
                hdrProbe: stage => { if (stage == failedStage && Interlocked.Increment(ref hdrAttempts) == 1) throw unsupported; });
            using var layers = new OverlayLayerService(Dispatcher.CurrentDispatcher, new PassiveLayerPlatform());
            var settings = new OverlaySettings { Mode = OverlayMode.Veil, VeilColor = "#E04080", Strength = 1, NoiseEnabled = false };
            var first = new NativeGpuOverlay("hdr-rejected", new Rectangle(-30000, -30000, 64, 64), settings, renderer, layers);
            var second = new NativeGpuOverlay("hdr-supported", new Rectangle(-29900, -30000, 64, 64), settings, renderer, layers);
            try
            {
                first.Show(); second.Show(); bool ready = false;
                for (int retry = 0; retry < 100 && !ready; retry++)
                {
                    Thread.Sleep(50);
                    OnGpu(renderer, () =>
                    {
                        var targets = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().ToArray();
                        ready = targets.Length == 2 && targets.All(t => (long)GpuField(t, "PresentedFrames") > 0);
                    });
                }
                Require(ready, $"Unsupported {failedStage} prevented the canvases from presenting.");
                OnGpu(renderer, () =>
                {
                    var targets = ((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().ToArray();
                    Require(((string)GpuField(renderer, "_lastStatus")).Contains("GPU running") && ((string)GpuField(renderer, "_lastStatus")).Contains("fallback", StringComparison.OrdinalIgnoreCase) ||
                        ((string)GpuField(renderer, "_lastStatus")).Contains("SDR compatibility"), "Fallback was not reported as a running renderer.");
                    if (failedStage == "presentation.fp16")
                        Require(targets.Count(t => (bool)GpuField(t, "FloatOutput")) == 1 && targets.Count(t => (bool)t.GetType().GetProperty("HdrPresentationRejected")!.GetValue(t)!) == 1,
                            "Unsupported HDR canvas took a supported second canvas out of HDR.");
                    else Require(targets.All(t => !(bool)GpuField(t, "FloatOutput")) && hdrAttempts == 1, "Whole-pipeline fallback repeated the rejected HDR setup.");
                    settings.Strength = 0.5;
                });
                first.UpdateSettings(settings); second.UpdateSettings(settings);
                OnGpu(renderer, () => Require(((System.Collections.IDictionary)GpuField(renderer, "_targets")).Values.Cast<object>().All(t => ((OverlaySettings)GpuField(t, "Settings")).Strength == 0.5),
                    "Fallback canvases did not accept live settings updates."));
            }
            finally { first.Close(); second.Close(); }
        }
        Console.WriteLine("PASS: unsupported HDR capture selection, per-canvas FP16/scRGB presentation fallback, global FP16 fallback, latching, live updates and access-loss preservation.");
    }

    private static void ImprovementUi(MainWindow window, string directory)
    {
        var selected = (DisplayProfile)Find<ListBox>(window, "DisplayTabs").SelectedItem;
        Find<System.Windows.Controls.RadioButton>(window, "AppearanceNav").IsChecked = true;
        Find<Expander>(window, "PreviewExpander").IsExpanded = true; window.RefreshPreview(); Pump();
        Require(Find<TextBlock>(window, "PreviewStatusText").Text.Contains("preview"), "Canvas preview did not update for the selected target.");
        Find<ComboBox>(window, "GradientModeBox").SelectedIndex = 2;
        Find<Slider>(window, "GradientOpacitySlider").Value = 0.9;
        Find<ComboBox>(window, "RepeatModeBox").SelectedIndex = 2; Find<Slider>(window, "RepeatXSlider").Value = 3;
        Require(selected.GradientMode == CanvasGradientMode.EdgeFade && selected.RepeatMode == SourceRepeatMode.Mirror && selected.RepeatX == 3, "New canvas controls did not update independent profile settings.");
        window.RefreshPreview(); Capture(window, "live-preview-guides.png");
        Call(window, "SaveButton_OnClick", window, new RoutedEventArgs());
        var original = (SetupPreset)Find<ComboBox>(window, "PresetBox").SelectedItem;
        var duplicate = window.DuplicateProfile(original.Id) ?? throw new InvalidOperationException("Could not duplicate profile.");
        Require(duplicate.Id != original.Id && duplicate.Setup.Fingerprint() == original.Setup.Fingerprint() && !duplicate.Shortcut.IsConfigured && !duplicate.Automation.Enabled, "Duplicated profile shared mutable state or active assignments.");
        Require(window.RenameProfile(duplicate.Id, "Portable emulator") && window.SearchProfiles("EMULATOR").Single().Id == duplicate.Id && window.MoveProfile(duplicate.Id, -1), "Profile rename/search/order failed.");
        Find<System.Windows.Controls.TextBox>(window, "ProfileSearchBox").Text = "portable";
        Require(Find<ComboBox>(window, "PresetBox").Items.Count == 1, "Manage search did not filter profiles.");
        Find<System.Windows.Controls.TextBox>(window, "ProfileSearchBox").Text = "";
        string sourceFolder = Path.Combine(directory, "export-source"); Directory.CreateDirectory(sourceFolder);
        string image = Path.Combine(sourceFolder, "border.png"); File.Copy(TestBorderPath, image, true);
        duplicate.Setup.Canvases[0].Mode = OverlayMode.ImageBorder; duplicate.Setup.Canvases[0].ImagePath = image;
        duplicate.Setup.Canvases[1].ImagePath = image;
        string package = Path.Combine(directory, "emulator.ambientprofile"); ProfilePackage.Export(duplicate, package);
        File.Delete(image);
        var imported = window.ImportProfile(package) ?? throw new InvalidOperationException("Could not import portable profile.");
        Require(imported.Id != duplicate.Id && !imported.Shortcut.IsConfigured && !imported.Automation.Enabled && File.Exists(imported.Setup.Canvases[0].ImagePath) && imported.Setup.Canvases[0].ImagePath == imported.Setup.Canvases[1].ImagePath,
            "Portable import did not restore/de-duplicate image assets independently of their original paths.");
        Require(ImageBorderDrawing.Load(imported.Setup.Canvases[0].ImagePath).PixelWidth == 100, "Imported image was unreadable.");
        string malicious = Path.Combine(directory, "bad.ambientprofile");
        using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create)) zip.CreateEntry("../outside.txt");
        Require(window.ImportProfile(malicious) is null && !File.Exists(Path.Combine(directory, "outside.txt")), "Import accepted an escaping archive entry.");
        string exe = Path.Combine(AppContext.BaseDirectory, "FeatureRegression.exe"), exe2 = Path.Combine(AppContext.BaseDirectory, "AmbientMonitor.exe");
        foreach (var preset in Find<ComboBox>(window, "PresetBox").Items.Cast<SetupPreset>().Where(p => p.Id != imported.Id).ToArray())
            window.SetExecutableAutomation(preset.Id, preset.Automation.ExecutablePath, false);
        Require(window.SetExecutableTriggers(imported.Id, new[] { exe, exe2 }, true, 2), "Multiple executable triggers or delay did not persist.");
        long clock = 10000; window.AutomationClock = () => clock;
        window.SetAutomationPaused(true); AwaitUi(window.EvaluateAutomationAsync([new RunningExecutable(301, exe2)]));
        Require(window.AutomatedPresetId is null && (string)Find<Wpf.Ui.Controls.Button>(window, "PauseAutomationButton").Content == "Resume auto", "Automation pause did not prevent activation.");
        window.SetAutomationPaused(false);
        AwaitUi(window.EvaluateAutomationAsync([new RunningExecutable(301, exe2)]));
        Require(window.AutomatedPresetId == imported.Id, "Alternate executable did not activate the multi-trigger profile.");
        AwaitUi(window.EvaluateAutomationAsync([])); clock += 1000;
        AwaitUi(window.EvaluateAutomationAsync([new RunningExecutable(302, exe)]));
        Require(window.AutomatedPresetId == imported.Id, "Launcher/game handoff lost the automated profile.");
        AwaitUi(window.EvaluateAutomationAsync([])); clock += 1999; AwaitUi(window.EvaluateAutomationAsync([]));
        Require(window.AutomatedPresetId == imported.Id, "Automation restored before its exit delay elapsed.");
        clock += 1; AwaitUi(window.EvaluateAutomationAsync([]));
        Require(window.AutomatedPresetId is null, "Automation did not restore after all executables/delay expired.");
        window.AutomationClock = () => Environment.TickCount64;
        Find<Expander>(window, "PreviewExpander").IsExpanded = false;
        Find<System.Windows.Controls.RadioButton>(window, "ApplicationNav").IsChecked = true; Capture(window, "portable-profiles-management.png");
        Console.WriteLine("PASS: live preview/guides, repeat/fade UI, profile rename/duplicate/order/search, portable image bundling/path rejection, multiple executables, handoff delay and saved automation pause.");
    }
}
