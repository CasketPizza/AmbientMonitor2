# Third-party components

Ambient Monitor's project license is GPL-3.0, inherited from the original
https://github.com/CasketPizza/AmbientMonitor repository. See `LICENSE` in
the source/release upload package for its full text.

The application uses these separately licensed components, restored through NuGet:

| Component | Version | License | Upstream |
|---|---|---|---|
| Vortice.Windows (Direct3D11, DXGI, DirectX, D3DCompiler) | 3.8.3 | MIT | https://github.com/amerkoleci/Vortice.Windows |
| Vortice.Mathematics | 2.1.0 | MIT | https://github.com/amerkoleci/Vortice.Mathematics |
| SharpGen.Runtime / SharpGen.Runtime.COM | 2.4.2-beta | MIT | https://github.com/SharpGenTools/SharpGenTools |
| WPF UI / WPF UI Abstractions | 4.3.0 | MIT | https://github.com/lepoco/wpfui |
| .NET / Windows Desktop runtime (self-contained release) | See `AmbientMonitor.runtimeconfig.json` / runtime files | MIT, with included component notices | https://github.com/dotnet/runtime and https://github.com/dotnet/wpf |

Full dependency license texts are included under `licenses/` in the prepared
source tree and release archive. Runtime-supplied license/notice files, when
present in the runtime distribution, retain their original names and contents.
No third-party trademarks are claimed by this project.
