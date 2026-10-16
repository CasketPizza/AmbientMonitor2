Back by singular demand

# Ambient Monitor

**Turn the space around your game or video into part of the atmosphere.**

Ambient Monitor is a Windows app for making a multi-monitor setup feel more seamless and visually appealing. It takes the picture from your primary display, blurs and adjusts it, and shows that ambient reflection on your other screens—or in rectangles you place anywhere on the desktop.

Instead of leaving your extra monitors black or showing an unrelated desktop while you play or watch something, you can fill them with matching colors and movement. You can also create a quiet colored backdrop or add your own transparent image borders around an emulator or game.

This is the new native Windows version of the original Ambient Monitor project, rebuilt with a settings interface, individual display/canvas controls, saved profiles and game-triggered automation.

![Ambient Monitor settings window with display tabs, saved profiles, acrylic styling and GPU controls](Screenshot%202026-10-17%20013015.png)

## What can I use it for?

- **Ambient light on extra monitors:** spread the colors of a game or video across the rest of your setup.
- **A less distracting backdrop:** use a colored focus veil when you want your other screens to stay quiet.
- **Emulator and game borders:** load transparent images, fit them around the content, and give each canvas its own artwork.
- **Custom layouts:** use entire displays or draw your own desktop rectangles, with independent settings for each one.
- **Different setups for different games:** save profiles, switch them with hotkeys or the tray, and activate them when selected executables run.

**The primary display is the ambient capture source.** The app follows what is shown there; it does not select a game or video on another monitor automatically.

## Download and get started

1. Open this repository's **Releases** page and download **`AmbientMonitor-v2.0.25-win-x64.zip`**.
2. Extract the **whole ZIP** into a folder.
3. Run **`AmbientMonitor.exe`**. Keep its included files together.
4. Choose a display, or select **Custom canvases → Draw box** to place an overlay yourself.
5. Choose a mode, adjust the appearance, and click **Start overlay**.
6. Use **Save as…** in the top bar to save the setup as a named profile.

The packaged release is self-contained for **64-bit Windows**: no Python or separate .NET installation is needed. Close any older running version before starting the new one. Use **Exit** from the tray menu if the old version is set to hide when closed.

## Make it look how you want

### Three overlay modes

| Mode | What it does |
|---|---|
| **Ambient reflection** | Shows an adjustable live reflection of the primary display, with blur, brightness and strength controls. |
| **Focus veil** | Gives a display or canvas a quiet colored backdrop. |
| **Image border** | Displays your own image, preserving transparent areas so the game or desktop can show through. |

### Framing, texture and fading

- Crop any side of the source, zoom, position, stretch, fit or flip it.
- Repeat or mirror a cropped section for a different ambient pattern.
- Add **independent gradient overlays**: two-color gradients or fading edges, with individual edge widths and strengths.
- Adjust **dither strength and grain size** to soften banding or add a finer/coarser texture.
- Use the **live preview, alignment guides and snapping** to place image borders more easily. Ambient previews use a sample image; the actual overlay uses the live primary display.

Each display or canvas has its own settings and can be enabled separately. Click-through and window-layer choices let you decide how an overlay sits around other applications and widgets.

## Profiles and game automation

The top profile bar keeps switching and saving close at hand. You can search, rename, duplicate and reorder saved setups, assign direct or previous/next hotkeys, and select profiles from the tray.

For automatic switching, open **Manage**, choose a saved profile, add the game or emulator executable(s), and enable its trigger. Ambient Monitor starts that setup while a matching program is running, then restores your previous setup when it closes. An exit delay handles launcher/game handoffs, and **Pause auto** is available in the top bar and tray.

Profiles can be exported as **`.ambientprofile`** bundles with their images included, making them easier to move or share. Unsaved setup changes are checked before closing or manually switching profiles. App preferences save automatically as you change them.

## Previous and web versions

- **[Previous Ambient Monitor project](https://github.com/CasketPizza/AmbientMonitor)** — the original repository, including the earlier downloadable version and project history.
- **[Working web-based version](https://casketpizza.github.io/AmbientMonitor/)** — the browser version is still available if you prefer to try it without downloading the Windows app.

## Technical details

This version is written in **C# / WPF on .NET 10**. It offers GPU and CPU rendering engines, with separate renderer processes so switching engines keeps the settings window open.

- **GPU:** Direct3D 11 desktop capture and effects, with HDR-aware processing where supported and SDR compatibility fallbacks.
- **CPU:** a software-processing option when you prefer it or GPU capture is unavailable.
- **Capture:** local to your PC, using the primary display as the source.
- **Appearance:** light/dark themes and an acrylic settings window, subject to Windows support and transparency settings.
- **HDR:** depends on Windows HDR, the display and the graphics path. CPU/image-border rendering and previews are SDR. Synthetic HDR precision/fallback tests have passed; live HDR-game calibration has not been exercised by the automated checks.

For detailed controls, rendering architecture, performance behavior, settings locations, HDR limitations and testing notes, see the **[technical guide](docs/TECHNICAL.md)**.

### Build from source

Install the **.NET 10 SDK** on Windows, then run this from the project folder:

```cmd
dotnet run --configuration Release
```

For the non-interactive feature checks:

```cmd
dotnet run --configuration Release --project Tests/Features/FeatureRegression.csproj
```

## License

**GPL-3.0**, following the original project. See [LICENSE](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.md).

Current version: **v2.0.25 · build 25**.
