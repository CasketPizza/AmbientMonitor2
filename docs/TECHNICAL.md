# Ambient Monitor — Technical guide

[Back to the project overview](../README.md)

Rendering architecture, detailed controls, source layout and verification notes.
Run the build/test commands below from the repository root.

Native Windows WPF app for seamless, visually appealing display setups: live ambient reflections, colored focus veils and transparent image borders on selected monitors or custom desktop canvases. Secondary displays are enabled by default, with independent settings for every detected display or custom canvas.

## Download and launch

Download `AmbientMonitor-v2.0.25-win-x64.zip` from this repository's **Releases** page, extract the whole ZIP to a folder, and run `AmbientMonitor.exe`. Keep the included DLL/runtime files with the executable. The self-contained Windows x64 release does not need a separate .NET or Python installation. Windows HDR/acrylic support still depends on the operating system and graphics path.

To build from source instead, install the **.NET 10 SDK** on Windows and run `dotnet run --configuration Release` from the folder containing `AmbientMonitor.csproj`.

This is the native C#/WPF rewrite of [CasketPizza's original AmbientMonitor project](https://github.com/CasketPizza/AmbientMonitor), which contained the earlier Python/browser versions. The upload package preserves the original GPL-3.0 license in `LICENSE`; dependency notices are in `THIRD-PARTY-NOTICES.md`. The current rewrite is v2.0.25/build 25 (October 2026).

## GPU rendering

The default GPU engine uses **Direct3D 11**, through Vortice.Windows:

1. **DXGI Desktop Duplication** captures the primary monitor as a GPU texture, using the hardware adapter that drives that monitor.
2. A shared GPU texture supplies all enabled ambient overlays. In GPU mode, frames stay in GPU memory: there is no GDI screenshot, CPU pixel processing, bitmap conversion, or GPU-to-CPU readback in the application.
3. GPU shaders crop, zoom, flip and downsample the image, apply separable box or Gaussian blur, then composite brightness, strength and animated dithering.
4. Each GPU overlay has a native Win32 parent and DXGI presentation child, using a double-buffered flip-model swap chain. WPF provides the settings UI; it does not create a compositor surface for GPU overlay windows. Restart teardown releases GPU presentation before destroying either HWND.

### Keeping overhead low

- One capture stream, one hardware device and one render thread are shared across overlays.
- Updates are capped at approximately 30 Hz, with non-blocking capture and presentation. Each display has its own one-frame swap-chain latency budget; submission order rotates so a small/faster display cannot monopolize a device-wide queue and starve a larger display.
- Desktop frames are copied and processed only when the desktop image changes; pointer-only changes are ignored.
- Cropped/blurred results are cached. Animated noise only reruns the final composite, and brightness/strength adjustments do not rerun the blur.
- Working textures are reused. The **Processing resolution** control sets the processing scale; strong blur automatically lowers the working resolution further to keep the blur's sample count bounded.
- Veil-only mode suspends desktop capture. Static veils sleep until settings change.
- GPU resources are released when all overlays are disabled or stopped. Shader bytecode is compiled once per process and reused on restarts.
- Capture/device resources are recreated after access loss, such as desktop lock/unlock or display changes. The control panel reports GPU errors while retrying.

## Controls

The control panel uses a DSX-inspired Windows 11 Fluent-style interface with WPF UI: a compact, unbranded caption with standard window buttons, rounded cards and toggle switches. It follows Windows light/dark by default, with explicit Dark and Light choices in App settings. **Acrylic blur is on by default**, with a saved **Acrylic transparency** slider (default 70%). Higher values reduce grey tint for a clearer, terminal-like blurred backdrop; 0% is opaque tint and 100% is the clearest supported acrylic. The title bar shares the same backdrop instead of adding a solid or second tinted surface. Windows transparency settings and platform support affect the result. App branding lives in the sidebar with the tagline **“A seamless backdrop for your setup.”** Highlights follow the **Windows-selected accent color**. A compact profile bar stays at the top of every page, with a profile flyout, Previous/Next, Save, Save as and Manage. Each target has its own tab; precise canvas bounds are tucked into an expandable editor. A sidebar separates **Appearance**, **Image framing**, **Performance** and global **App settings**. Target controls collapse on App settings to leave room for global preferences; start/stop/save remain accessible across pages. The footer indicates unsaved profile changes.

- Enable/disable each display's overlay.
- **Ambient reflection**, **Focus veil**, or **Image border** mode.
- Strength, blur amount/type, brightness and animated dither strength.
- Processing resolution, fill/stretch-to-fit scaling, four-sided source cropping, zoom and focus position.
- Horizontal/vertical flips and click-through operation.
- A single **Start overlay / Stop overlay** toggle, plus save and reset.
- **Apply to all displays/canvases** copies the selected target's effects, crop/framing, processing resolution, click-through and layer into other profiles. Each target retains its nickname, identity, bounds, enabled state and individual image path. Changes apply to existing overlays immediately; Save profiles persists them.
- **Overlay layer** under Performance selects a separate window-order policy for each display (details below).
- **GPU / CPU selector** in the lower-left processing bubble. Hover over it for a comparison. Every engine selection shuts down the previous renderer process and launches a fresh worker. The UI stays open with its selected tabs, nicknames and unsaved settings; overlays resume only if they were already running.
- **Display nicknames**: choose a display, click Rename, and Apply or Clear. A nickname appears above the original `Display #` in smaller text. Save profiles persists nicknames; clearing restores the original label.
- **Global shortcut** under App settings: click Record and press a modifier plus a key. Escape cancels; Clear removes it. The shortcut toggles all enabled overlays without bringing the window forward and works from the tray. Registration conflicts are reported in the shortcut card.
- **Close to system tray** under App settings: closing hides the window while keeping overlays and shortcuts available. Double-click the tray icon to reopen. Its light/dark-themed menu has Open, toggle overlays, a checked **Profiles / themes** submenu and Exit; App settings also has an Exit app button.
- Unsaved setup changes prompt **Save / continue without saving / Cancel** before closing (including hiding to the tray) or switching named profiles. Cancel keeps the current setup open. App preferences save immediately on change.

Overlays are borderless and do not activate. The default layer is Topmost, with other layer choices available per target. Click-through lets applications underneath receive mouse input. Ambient reflection and Focus veil use opaque backdrops. Image border preserves image alpha, so transparent image areas reveal the emulator, game or desktop underneath. **Stretch** stretches the retained source image to the target bounds.

Display profiles and nicknames are saved manually to `%LOCALAPPDATA%\AmbientMonitor\settings.json`. Custom canvas bounds and profiles use `canvases.json` in the same directory and are also saved with **Save profiles**. Named whole-setup profiles and their direct hotkeys use `presets.json`. Existing per-display profiles remain compatible. Global engine, target mode, hotkeys, tray, interface theme and acrylic preferences save immediately to `app-settings.json`. The default engine is GPU, with no shortcuts configured and close-to-tray disabled. Capture is local to this PC.

The acrylic backend uses the supported Windows 11 DWM Desktop Acrylic material. The transparency slider controls the extra client tint over that material; Windows controls the base material's tint and blur. Even at 100% slider transparency, the native backdrop stays enabled. Unsupported systems use an opaque, readable client surface.

### Named profiles / themes

Use the top **Save as…** flyout to give the complete setup a unique name: display settings, both sets of targets, canvas bounds, nicknames, enabled states, effects, veil colors, image paths and framing. **Save** or **Save profiles** updates the active named setup; Save as creates an independent copy. Global engine and interface preferences remain app-wide.

Choose a profile from the top flyout, use Previous/Next, or select it from the tray submenu. **Manage → Profile shortcuts & automation** opens advanced configuration. Profiles can have direct hotkeys; separate Previous/Next hotkeys cycle through the saved list. Record a modifier plus a key, or Clear an assignment. All overlay/profile shortcuts work with the settings window focused, open in the background, or hidden in the tray. Focused WPF input and global native delivery are deduplicated so one press causes one action. Shortcut conflicts are reported and assignments save immediately. Unsaved setup changes prompt before a manual switch. A manually loaded setup becomes the next startup state.

Search is available in both the top profile flyout and Manage. Manage also provides Rename, Duplicate, Move up/down and Delete. Order is shared by the flyout, tray, Previous/Next cycling and automation priority. Duplicate makes an independent copy of the saved setup with a new identity; its direct hotkey is cleared and automation disabled.

### Portable profiles

**Manage → Export with images…** creates an `.ambientprofile` ZIP containing the selected saved setup and referenced image assets. Identical assets are deduplicated. **Import profile…** installs images into `%LOCALAPPDATA%\AmbientMonitor\assets\<profile-id>`, gives the imported profile a new identity/unique name and remaps its image references. The original image folders are no longer needed. Import does not activate a profile; choose Load afterward. Direct hotkeys are cleared and executable automation disabled on import, because those assignments are machine-specific. Review display/canvas geometry when moving a setup to another PC.

Portable limits: 64 MB per image, 256 MB total images, 256 images, and at most 16384 pixels per side/64 million pixels per image. Missing source images fail export rather than producing an incomplete bundle. Invalid manifest paths or archive traversal entries fail import.

### Executable automation

With Ambient Monitor running (open or in the tray), use **Manage**, select a saved profile, **Add executables…**, and enable **Activate when an executable runs**. Choose one or several actual game/emulator `.exe` files; Remove selected/Clear all edit the list. Any listed executable can activate the profile. The watcher checks once per second and matches full paths, so another app with the same filename does not trigger it. Trigger lists and the 0–30 second exit/handoff delay save immediately. Newly chosen trigger lists start with a 2-second delay; older single-executable rules retain immediate exit behavior until edited.

The matching profile is selected and started automatically. When all its matching processes exit and its delay expires, the previous setup and running/stopped state are restored. A launcher/game handoff or matching process restart cancels the countdown. If several profiles match, the current automated profile is retained while its executable runs or handoff delay is active; otherwise the first matching profile in saved order wins. Chained automatic profiles retain the original return setup. The top bar marks automatic activation.

**Pause auto / Resume auto** is accessible in the top bar and tray. Pausing holds the current overlay/setup as-is and suspends automatic switching/restoration; it does not stop a manually running overlay. The preference saves immediately. Resuming re-evaluates current processes and starts a fresh exit countdown where appropriate.

Manual Start/Stop or profile selection takes priority: current executable triggers remain suppressed until those executables close and run again. Automation waits while a manual setup has unsaved edits or a settings dialog/Save as flyout is open. If a game closes while its profile has unsaved edits, overlays stop immediately, edits remain available, and saving them completes restoration. Automatic activation is temporary and does not replace the normal startup setup; saving an automated profile updates its named effects without overwriting the return setup.

### Dither grain

**Appearance → Gradient dithering → Dither grain size** adjusts fine/coarse animated texture independently of dither strength. Size is 1–32 output/canvas pixels (fractional steps supported), with 1 px as the finest/default grain. The setting is per target and copies/saves with its effects, named profiles and portable exports. Grain size changes only the final GPU composite, preserving the crop/blur cache. CPU dithering composites at final target resolution so a small veil/blur buffer does not enlarge the texture; CPU output-resolution work increases when animated dithering is enabled. The live preview uses the same CPU grain mapping.

### Focus veil

Select **Focus veil** and choose a color using the color picker or a `#RRGGBB` value. Strength adjusts color intensity; optional dithering remains available. Blur, brightness, capture quality and image-framing controls are hidden because they do not affect a solid veil. Click-through and window layers still apply. GPU and CPU engines use the same veil color.

### Image borders

Select **Image border → Load image** for the current target. PNG, JPEG, BMP, GIF (first frame) and TIFF are supported; transparent PNGs are ideal for emulator borders. Every canvas can reference a different image. Images load without retaining file locks; saved profiles reference the original file path.

Under **Image framing**, choose **Fit** (whole image, preserved proportions), **Fill** (cover the canvas, crop excess), or **Stretch** (match both edges). Four-sided source cropping, 0.05×–20× scale, focus/alignment, horizontal/vertical flips and pixel offsets allow precise placement. Exact scale and offset inputs are available alongside sliders. Strength controls image opacity. Transparent source pixels and uncovered canvas areas remain transparent.

Static borders use transparent WPF composition in either processing engine. They are redrawn on settings changes and do not capture the desktop or run the ambient blur/dither loop. The same per-target window layers and click-through behavior apply.

### Source cropping

Open **Image framing → Source crop** to expand the four percentage sliders for Left, Right, Top and Bottom. Opposing sides retain at least 1% of the source. **Reset crop** clears all four sides.

- **Stretch** extracts the retained source rectangle and stretches it across the canvas.
- **Fill screen · crop edges** blacks out removed source regions while keeping the existing zoom/focus framing. Blacked-out regions remain black after blur and other effects.

Cropping applies to the captured primary-display image and works in both GPU and CPU engines. Each target has its own crop settings; Apply to all copies them along with the other effects.

### Repetition and edge fades

**Image framing → Repeat cropped selection** offers Off, Repeat and Mirrored repeat, with 1–16 copies horizontally/vertically. Repetition uses the retained crop as each cell; mirrored cells alternate direction to soften seams. Source flips, zoom/focus and blur still apply. Ambient GPU/CPU paths and image borders support repetition. It is different from stretching a narrow crop: Stretch creates one continuous color wash, while repetition produces multiple copies.

**Appearance → Independent gradient overlay · edge fade** adds a separately enabled overlay on top of any reflection, veil or image canvas. Its overall opacity is independent of canvas strength and dither/blur settings. Linear gradients have start/end colors (`#RRGGBB` or color pickers), independent start/end opacities, direction, start/end stops and a falloff curve. Stops are kept at least 1% apart. Edge fade has selectable left/right/top/bottom edges; enable **Independent width & strength for each edge** for separate widths (0–50% of that canvas dimension) and strengths. Width 0 disables that edge. Shared-stop mode retains the simpler center-to-edge fade. Lower falloff values broaden the transition; higher values concentrate it. Black edge fade keeps the center clear and darkens the outer region.

Gradients are off by default and save per target, copy with effects, and travel with named/portable profiles. Disabling the gradient keeps its settings for later. CPU, GPU, live preview and image borders share the fade formula; transparent image-border centers remain transparent where gradient alpha is zero. Gradient edits do not invalidate the GPU crop/blur cache. These controls approximate ambient-light color spread/fade without browser/video-page integration.

### Live preview and alignment

Expand **Live canvas preview & alignment** on a target settings page. Veils and image borders preview the actual selected settings and image; ambient reflection uses an in-memory sample with the primary display's aspect ratio, never a second desktop capture. Changes update the preview with a short debounce. The preview is an SDR layout/effects preview, not an HDR luminance reference.

Guides show thirds and center lines. Drag an image border to adjust its pixel offsets, optionally snapping its edges/center to guides; Center/Left/Right/Top/Bottom buttons provide quick alignment. Guide/snap preferences save immediately. The collapsed preview performs no ongoing processing.

## GPU HDR

Unsupported HDR components now fall back without stopping every canvas. If floating-point duplication is rejected, capture uses the existing SDR duplication path. If an individual FP16/scRGB swap chain is rejected, that canvas uses SDR presentation while other canvases can retain HDR output. Unsupported failures in the broader HDR processing pipeline downgrade the worker to SDR compatibility. Rejections are latched for the renderer session rather than retried every frame; restart/reselect the engine to retry HDR. The status line reports SDR capture/output/global fallback explicitly. Access loss and device removal still use normal recovery rather than permanently disabling HDR.

**App settings → GPU HDR · automatic** is enabled by default. When Windows reports HDR/Advanced Color on a connected display, the GPU path uses HDR-capable Desktop Duplication (`IDXGIOutput5.DuplicateOutput1`), floating-point crop/blur surfaces and FP16 scRGB swap chains where supported. Floating-point source highlights above SDR white survive rendering to HDR destinations; HDR-to-SDR output is tone-mapped. SDR input is decoded to linear color for the floating-point path. The worker rechecks display color configuration and recovers its capture/device after changes.

This facilitates HDR games without injection or process-specific graphics hooks. Windows HDR must be active, and the game must be on the captured **primary display** to supply its pixels. Games on other monitors do not change the capture source. Protected/exclusive capture availability remains subject to Desktop Duplication. CPU rendering and static WPF image borders are SDR; transparent border areas still reveal the underlying game normally. The UI preview uses SDR sample/reference colors.

Verification covers actual FP16/scRGB swap-chain creation and in-memory HDR values, preservation above SDR white, SDR conversion and gradient compositing. Live HDR-game capture/visual calibration was not exercised while this desktop was in use.

### Custom desktop canvases

Choose **Custom canvases**, then **Draw box** and drag anywhere on the desktop to create a canvas. Escape cancels selection. Existing overlays pause while selecting and resume afterward if they were running.

Each canvas has an independent effects profile and can be renamed, enabled or disabled. **Redraw** changes its rectangle; **Delete** removes it. The Left / X, Top / Y, Width and Height fields allow precise pixel bounds with **Apply bounds**. Negative desktop coordinates are supported, and each dimension must be 16–16384 pixels. **Apply to all canvases** copies effects while preserving each canvas's bounds, identity, nickname and enabled state. Save profiles persists additions, edits and deletions.

The Displays and Custom canvases modes select which set of targets is rendered. Switching modes preserves both sets of profiles.

## Window layers

Select **Performance → Overlay layer** for the current display:

| Layer | Behavior |
|---|---|
| Stay topmost | Reasserts the overlay above other topmost windows when window order changes. |
| Topmost | Above ordinary application windows, behind the contiguous front group of topmost windows/widgets. Low desktop skins with a retained topmost style flag are not used as anchors. |
| Normal | Ordinary non-activating window ordering; other apps can move in front. |
| Bottom | Behind application windows and just above the desktop host. Show desktop can cover it. |
| On desktop | Desktop-level behind applications, following the shell's desktop ordering when Show desktop is used. |

To keep Rainmeter-style widgets visible over an overlay, put the widgets in their **Topmost** layer and choose **Topmost** here. A widget in the normal/desktop band can still be covered by a topmost overlay; Windows does not provide a separate universal widget band. Stay topmost intentionally prioritizes the overlay instead.

Desktop mode remains a top-level window and positions relative to Explorer's desktop host, preserving the GPU presentation handles and DPI behavior. It is an opaque overlay rather than a wallpaper replacement, so it can cover desktop icons. If the shell host is unavailable it temporarily uses Bottom and reports the fallback in the layer description.

A shared, coalesced window-event watcher maintains the selected layers without per-frame z-order polling. Normal-only or disabled overlays do not keep monitoring hooks active. Layer changes do not invalidate GPU crop/blur caches or move other applications' windows.

## CPU engine

The optional CPU engine shares one primary-display `Graphics.CopyFromScreen` capture across overlays and applies crop, flip, downsampling, box/Gaussian-approximation blur, brightness, strength and noise in software. Buffers are reused and published frames are immutable; updates are limited to approximately 30 Hz. Veil-only mode stops capturing and static veils sleep until settings change.

GPU mode is generally smoother with lower CPU overhead and fewer frame copies. CPU mode is useful for moving effect processing off a busy GPU or when Desktop Duplication is unavailable, but generally uses more CPU and memory bandwidth and may run more slowly at high resolutions. CPU mode still uses WPF/Windows composition to display the final image, so it does not eliminate all GPU use.

## Run

Requires Windows and the .NET 10 SDK. GPU mode requires a Direct3D 11 hardware adapter with Desktop Duplication support for the primary display.

```powershell
dotnet run
```

For an optimized build:

```powershell
dotnet run --configuration Release
```

The engine is selected explicitly. If GPU capture is unavailable, the app reports the error and retries; choose CPU in the processing bubble to switch engines.

## Main files

- `MainWindow.xaml` / `.cs`: settings UI, display profiles, persistence and overlay lifecycle.
- `GpuRenderService.cs`: shared GPU device, desktop capture, effect passes, presentation and recovery.
- `GpuSurface.cs`: native HWND hosting for direct GPU presentation.
- `NativeGpuOverlay.cs`: pure Win32 GPU overlay hosting, layer application after showing, and ordered native/GPU teardown.
- `GpuOutputSelection.cs`: current native primary-monitor/output matching for GPU initialization and restart, without relying on cached WinForms device names.
- `CpuRenderService.cs` / `CpuFrameProcessor.cs` / `CpuSurface.cs`: shared GDI capture, CPU effects and bounded WPF image presentation.
- `Shaders/Ambient.hlsl`: crop/rotation/flip, separable blur and final composite shaders.
- `OverlayWindow.xaml` / `.cs`: monitor placement, click-through styles and capture exclusion.
- `OverlaySettings.cs`: per-display settings models.
- `SourceFraming.cs`: shared source crop, zoom and focus calculations for GPU and CPU rendering.
- `CanvasBounds.cs` / `DesktopSelectionWindow.cs`: custom canvas geometry and user-invoked desktop selection.
- `MainWindow.Presets.cs` / `SetupPreset.cs`: named setup snapshots, persistence, dirty tracking and switch/close prompts.
- `MainWindow.Appearance.cs` / `MainWindow.Shortcuts.cs`: theme/acrylic controls, veil/image editing and profile hotkey recording.
- `SettingsWindowRendering.cs` / `MainWindow.Movement.cs`: software control-panel composition and deferred/coalesced appearance updates during window movement.
- `NativeAcrylic.cs`: supported DWM Desktop Acrylic activation/readback and client-tint opacity calculation.
- `MainWindow.QuickProfiles.cs`: always-accessible profile flyout and quick save/manage actions.
- `MainWindow.Automation.cs` / `ExecutableProcessReader.cs`: exact-path executable rules, background polling, automatic setup restoration and manual overrides.
- `CanvasPreview.cs` / `MainWindow.Preview.cs`: live sample/image previews, drag positioning, guides and snapping.
- `CanvasEffects.cs` / `MainWindow.CanvasEffects.cs`: repeat mapping, gradient/edge fade formulas and editing controls.
- `MainWindow.ProfileLibrary.cs` / `ProfilePackage.cs`: search, rename/duplicate/order and portable image bundles.
- `HdrColor.cs`: output HDR detection and SDR/linear color helpers.
- `ImageBorderWindow.cs` / `ImageBorderDrawing.cs`: static transparent image hosting and crop/fit/fill/stretch placement.
- `VeilPalette.cs`: shared veil color parsing for CPU and GPU.
- `ApplicationSettings.cs`: global engine, shortcut and tray preferences.
- `GlobalHotkey.cs` / `TrayIcon.cs`: Windows shortcut registration and notification-area integration.
- `WindowChromeHelper.cs`: matching the native caption surface to the custom Fluent header.
- `RendererProcessHost.cs` / `RendererWorker.cs` / `RendererProtocol.cs`: isolated backend processes, private settings/status IPC and full renderer replacement without closing the UI.
- `OverlayLayerService.cs` / `LayerPlacementPolicy.cs` / `WindowLayerPlatform.cs`: per-display z-order policy, shared window-event monitoring and native positioning.

Possible future additions include active-window capture.

## Renderer process isolation

The control panel does not create GPU devices or CPU capture pipelines. It launches the same executable with a private renderer-worker argument and exchanges settings/status messages over a current-user-only named pipe. Screen pixels remain entirely inside the worker.

The settings UI uses WPF software rendering to avoid hardware render-target migration during rapid monitor/DPI changes. This policy is set in the control-panel process before its window is initialized; renderer workers use their selected GPU/CPU engine. Acrylic remains a Windows compositor backdrop. Windows preference notifications are coalesced, unchanged theme settings are skipped, and appearance updates wait until a move/resize operation finishes.

Changing CPU/GPU or restarting overlays fully exits the old worker before starting a new one. This resets backend-specific process state, DXGI factories, capture objects, devices, windows and buffers while preserving the control panel. Rapid settings edits are coalesced, and old-worker replies are ignored. Workers exit when the UI connection closes; an unresponsive owned worker is terminated after a bounded shutdown timeout.

Hotkeys, tray behavior, profile editing and preference storage stay in the UI process. Normal Stop shuts down the rendering worker; selecting an engine while stopped prepares a fresh worker without turning overlays on.

## Version and build identity

The sidebar shows the version and build number from the compiled assembly. The current build is **v2.0.25 · build 25**, expanding independent canvas gradient controls with two-color stops, overall opacity, falloff and separate per-edge widths/strengths. Hover over the label to see the build configuration.

`VersionPrefix` and `BuildNumber` in `AmbientMonitor.csproj` are the source of truth for the UI label, executable file version and informational version. Increment `BuildNumber` for each user-visible build; do not hard-code a separate UI version string.

## Non-interactive feature checks

Gradient checks compare CPU/GPU pixels for two-color/two-opacity linear gradients and independent edge fades; they cover separate enable/overall opacity, widths/strengths/falloff, transparent image-border centers, cache-key preservation, copied/serialized settings, old gradient defaults and UI controls.

Dither-size checks compare actual CPU/GPU output pixels at 1, 8 and 6.5 px grain size, verify cell size/animation, independence from blur processing resolution, cache-key preservation, legacy defaults, serialization and the visible slider/value label.

HDR compatibility regressions inject `DXGI_ERROR_UNSUPPORTED` into FP16 presentation/processing and verify real off-screen canvases present through fallback, supported canvases retain FP16, rejected setups are not retried repeatedly, and live updates remain accepted. Capture negotiation is tested with synthetic provider delegates; the desktop is not captured. Access-loss errors are checked separately to retain their normal recovery behavior.

The shutdown regression runs in an isolated child process and checks one-close completion while idle and with CPU/GPU workers running. It verifies that the window is not re-shown, stale tray-open callbacks are ignored, and the renderer process has exited. Idle windows close synchronously; windows with pending renderer shutdown hide after WPF finishes its original close event and complete shutdown once.

Acrylic checks read the actual `DWMWA_SYSTEMBACKDROP_TYPE` from the off-screen settings HWND, verifying Desktop Acrylic (3), None (1) when disabled, and preservation of the material at 100% tint transparency.

Startup/restore checks load saved dark/light acrylic settings without touching the slider, model late opaque caption initialization, then verify post-render surface repair and repeated tray-style hide/show restoration. Surface-only repairs leave preferences and theme resources unchanged. Native chrome is refreshed without moving, resizing, activating or changing the window's z-order.

The settings-window movement regression performs 192 rapid moves at off-screen coordinates, with synthetic 96–240 DPI transitions, move-loop messages, theme/acrylic changes and repeated preference notifications. It checks deferred/coalesced appearance updates, software UI rendering and unchanged setup profiles. These simulated transitions supplement manual testing of dragging between actual monitors.

Profile/UI checks also verify client-tint opacity and persistence, the unified title bar, quick profile flyout contents, local/global shortcut deduplication, exact executable matching, multiple running instances, automatic start/exit/restore, overlapping rules, manual override/re-arming, startup-state isolation and preservation of unsaved edits. Process discovery uses an owned hidden fixture process. Settings saves are checked against brief reader contention. Off-screen screenshots use an in-memory backdrop plate for readable layout previews; they do not capture the desktop or native acrylic imagery.

These checks use in-memory CPU/GPU images, off-screen native GPU restart cycles, simulated window ordering and an off-screen UI with isolated settings. They also launch real off-screen renderer workers to verify fresh process IDs on GPU/CPU changes, complete old-worker shutdown, resumed rendering and preserved UI-owned unsaved settings. They cover unequal-size display animation and independent presentation budgets, actual native startup topmost flags/order (moving only the off-screen test window), primary-output topology matching, immediate horizontal/vertical updates across multiple cached GPU targets, all five layer policies, initial-show reapplication, widget ordering, desktop fallback/recovery, apply-to-all preservation, persistence, visible build identity and compact layout. They do not capture the desktop, register global shortcuts, create tray icons, reposition other applications or show on-screen capture fixtures:

```powershell
dotnet run --configuration Release --project Tests/Features/FeatureRegression.csproj
```

## GPU restart regression checks

While this PC is in use, agent verification must stay off-screen. Do not run the interactive `--ui-restarts` checks or show colored capture-fixture windows during normal development.

These opt-in hardware checks verify actual image pixels after repeated GPU starts/stops and invalidated shader bindings:

```powershell
dotnet run --configuration Release --project Tests/GpuRegression.csproj
```

To additionally exercise the real Start/Stop buttons for 20 cycles on secondary displays:

```powershell
dotnet run --configuration Release --project Tests/GpuRegression.csproj -- --ui-restarts
```

The UI check temporarily shows a colored capture-source window and the overlays. It does not save or modify stored settings. GPU-to-CPU readback is confined to the test executable.
