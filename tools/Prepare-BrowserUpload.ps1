param(
    [string]$StagingRoot = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'AmbientMonitor-Packaging'),
    [switch]$RefreshGeneratedFolders
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..'))
[xml]$project = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($root, 'AmbientMonitor.csproj'))
$version = "$($project.Project.PropertyGroup.VersionPrefix).$($project.Project.PropertyGroup.BuildNumber)"
$output = [System.IO.Path]::Combine($root, 'Upload-Ready')
$repo = [System.IO.Path]::Combine($output, '01-New-Repository')
$release = [System.IO.Path]::Combine($output, "02-Release-v$version")
if ([System.IO.Directory]::Exists($repo) -or [System.IO.Directory]::Exists($release)) {
    if (-not $RefreshGeneratedFolders -or -not [System.IO.File]::Exists([System.IO.Path]::Combine($output, 'START-HERE.md'))) {
        throw 'Upload folders already exist. Move them aside or explicitly refresh this generated snapshot.'
    }
    foreach ($generated in @($repo, $release)) { if ([System.IO.Directory]::Exists($generated)) { [System.IO.Directory]::Delete($generated, $true) } }
}
foreach ($directory in @($repo, $release, $StagingRoot)) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
$stage = [System.IO.Path]::Combine($StagingRoot, [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($stage) | Out-Null
$publish = [System.IO.Path]::Combine($stage, 'application')
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Write-Document([string]$path, [string]$text) { [System.IO.File]::WriteAllText($path, $text, $utf8) }
function Copy-PackageFile([string]$source, [string]$destination) {
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
    [System.IO.File]::Copy($source, $destination, $false)
}

# An explicit source allowlist keeps generated test images, isolated settings,
# previous binaries and package staging out of the browser repository upload.
$extensions = @('.cs', '.xaml', '.csproj', '.md', '.png')
foreach ($file in [System.IO.Directory]::EnumerateFiles($root)) {
    $name = [System.IO.Path]::GetFileName($file)
    if ($extensions -contains [System.IO.Path]::GetExtension($file).ToLowerInvariant() -or $name -in @('.gitignore', '.gitattributes', 'LICENSE')) {
        Copy-PackageFile $file ([System.IO.Path]::Combine($repo, $name))
    }
}
foreach ($directory in @('Shaders', 'Tests', 'tools')) {
    $source = [System.IO.Path]::Combine($root, $directory)
    foreach ($file in [System.IO.Directory]::EnumerateFiles($source, '*', [System.IO.SearchOption]::AllDirectories)) {
        $relative = $file.Substring($root.Length + 1)
        if ($relative -match '(^|\\)(bin|obj)(\\|$)') { continue }
        if ([System.IO.Path]::GetExtension($file).ToLowerInvariant() -notin @('.cs', '.csproj', '.hlsl', '.ps1')) { continue }
        Copy-PackageFile $file ([System.IO.Path]::Combine($repo, $relative))
    }
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$web = New-Object System.Net.WebClient
try {
    if (-not [System.IO.File]::Exists([System.IO.Path]::Combine($repo, 'LICENSE'))) {
        $web.DownloadFile('https://raw.githubusercontent.com/CasketPizza/AmbientMonitor/main/LICENSE', [System.IO.Path]::Combine($repo, 'LICENSE'))
    }
    $licenses = [System.IO.Path]::Combine($repo, 'licenses')
    [System.IO.Directory]::CreateDirectory($licenses) | Out-Null
    $upstreams = @{
        'Vortice.Windows-MIT.txt' = 'https://raw.githubusercontent.com/amerkoleci/Vortice.Windows/main/LICENSE'
        'Vortice.Mathematics-MIT.txt' = 'https://raw.githubusercontent.com/amerkoleci/Vortice.Mathematics/main/LICENSE'
        'SharpGenTools-MIT.txt' = 'https://raw.githubusercontent.com/SharpGenTools/SharpGenTools/6990bcafe124a4c22515ad19cee5a081da8db67b/LICENSE.txt'
        'WPF-UI-MIT.txt' = 'https://raw.githubusercontent.com/lepoco/wpfui/4.3.0/LICENSE'
        'dotnet-runtime-MIT.txt' = 'https://raw.githubusercontent.com/dotnet/runtime/main/LICENSE.TXT'
        'dotnet-wpf-MIT.txt' = 'https://raw.githubusercontent.com/dotnet/wpf/main/LICENSE.TXT'
        'dotnet-runtime-THIRD-PARTY-NOTICES.txt' = 'https://raw.githubusercontent.com/dotnet/runtime/main/THIRD-PARTY-NOTICES.TXT'
        'dotnet-wpf-THIRD-PARTY-NOTICES.txt' = 'https://raw.githubusercontent.com/dotnet/wpf/main/THIRD-PARTY-NOTICES.TXT'
    }
    foreach ($entry in $upstreams.GetEnumerator()) {
        $web.DownloadFile($entry.Value, [System.IO.Path]::Combine($licenses, $entry.Key))
    }
}
finally { $web.Dispose() }

# Publish from the clean upload source itself, with intermediates outside it.
# Folder deployment is intentional: the renderer launches the same executable
# as a child process, and assembly locations must remain real file paths.
$intermediate = [System.IO.Path]::Combine($stage, 'obj') + [System.IO.Path]::DirectorySeparatorChar
$buildOutput = [System.IO.Path]::Combine($stage, 'bin') + [System.IO.Path]::DirectorySeparatorChar
& dotnet publish ([System.IO.Path]::Combine($repo, 'AmbientMonitor.csproj')) --configuration Release --runtime win-x64 --self-contained true --output $publish "-p:BaseIntermediateOutputPath=$intermediate" "-p:BaseOutputPath=$buildOutput" '-p:PublishSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false'
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish from repository upload failed.' }
if ([System.IO.Directory]::Exists([System.IO.Path]::Combine($repo, 'bin')) -or [System.IO.Directory]::Exists([System.IO.Path]::Combine($repo, 'obj'))) { throw 'Clean upload source was contaminated by build outputs.' }

foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md')) {
    Copy-PackageFile ([System.IO.Path]::Combine($repo, $name)) ([System.IO.Path]::Combine($publish, $name))
}
foreach ($file in [System.IO.Directory]::EnumerateFiles([System.IO.Path]::Combine($repo, 'licenses'))) {
    Copy-PackageFile $file ([System.IO.Path]::Combine($publish, 'licenses', [System.IO.Path]::GetFileName($file)))
}
Write-Document ([System.IO.Path]::Combine($publish, 'START-HERE.txt')) @"
Ambient Monitor v$version - Windows x64, self-contained

Extract the complete ZIP to a folder, then run AmbientMonitor.exe.
Keep all runtime/DLL files beside the executable. No Python or separate .NET
installation is required. Acrylic and full HDR depend on Windows/display support.

Settings are stored in %LOCALAPPDATA%\AmbientMonitor.
Close the old version before launching this one. If close-to-tray is enabled,
use Exit from its tray menu when you want to fully exit.

Source: AmbientMonitor-v$version-source.zip attached to the same release.
License: GPL-3.0; see LICENSE and THIRD-PARTY-NOTICES.md.
"@

# Smoke-test the packaged EXE as a real CPU and GPU worker, entirely off-screen.
function Test-PackagedWorker([int]$backend) {
    $name = 'AmbientMonitor-Package-' + [guid]::NewGuid().ToString('N')
    $pipe = New-Object System.IO.Pipes.NamedPipeServerStream($name, [System.IO.Pipes.PipeDirection]::InOut, 1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = [System.IO.Path]::Combine($publish, 'AmbientMonitor.exe')
    $info.Arguments = "--renderer-worker $name --worker-offscreen-test"
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WorkingDirectory = $publish
    $process = [System.Diagnostics.Process]::Start($info)
    $reader = $null; $writer = $null
    try {
        if (-not $pipe.WaitForConnectionAsync().Wait(20000)) { throw 'Packaged worker connection timed out.' }
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $ready = $reader.ReadLineAsync()
        if (-not $ready.Wait(15000) -or ($ready.Result | ConvertFrom-Json).Kind -ne 'ready') { throw 'Packaged worker did not report ready.' }
        $command = @{ Backend = $backend; Running = $true; EnableHdr = $false; Profiles = @(@{DeviceName='package-smoke'; DisplayName='Package smoke test'; Enabled=$true; Mode=1; NoiseEnabled=$false; Strength=0.5; Layer=2}) } | ConvertTo-Json -Depth 8 -Compress
        $writer.WriteLine($command)
        $running = $false
        for ($reply = 0; $reply -lt 20 -and -not $running; $reply++) {
            $line = $reader.ReadLineAsync()
            if (-not $line.Wait(15000)) { throw 'Packaged worker render status timed out.' }
            $message = $line.Result | ConvertFrom-Json
            if ($message.Kind -eq 'error' -or $message.Text -like '*paused;*') { throw $message.Text }
            $running = $message.Text -like '* running *'
        }
        if (-not $running) { throw 'Packaged worker did not render.' }
        $writer.WriteLine('{"Operation":"shutdown"}')
        if (-not $process.WaitForExit(15000)) { throw 'Packaged worker shutdown timed out.' }
        if ($process.ExitCode -ne 0) { throw "Packaged worker exited with $($process.ExitCode)." }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        if ($writer) { $writer.Dispose() }; if ($reader) { $reader.Dispose() }
        $pipe.Dispose(); $process.Dispose()
    }
}
Test-PackagedWorker 1
Test-PackagedWorker 0

$runtimeZip = [System.IO.Path]::Combine($release, "AmbientMonitor-v$version-win-x64.zip")
$sourceZip = [System.IO.Path]::Combine($release, "AmbientMonitor-v$version-source.zip")
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $runtimeZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
[System.IO.Compression.ZipFile]::CreateFromDirectory($repo, $sourceZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
function Sha256([string]$path) {
    $stream = [System.IO.File]::OpenRead($path); $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}
$checksums = @($runtimeZip, $sourceZip) | ForEach-Object { (Sha256 $_) + '  ' + [System.IO.Path]::GetFileName($_) }
Write-Document ([System.IO.Path]::Combine($release, 'SHA256SUMS.txt')) (($checksums -join "`n") + "`n")
Write-Document ([System.IO.Path]::Combine($release, 'RELEASE-NOTES.md')) @"
# Ambient Monitor v$version

Native Windows x64 release - self-contained; no Python or separate .NET install.

Download **AmbientMonitor-v$version-win-x64.zip**, extract the entire archive,
and run **AmbientMonitor.exe**. Keep its companion files together.

## Features
- Ambient reflections, colored focus veils and transparent image borders.
- Per-display/custom-canvas settings, crop/zoom/flips, repetition and mirroring.
- Independent gradient overlays with two-color stops and per-edge fades.
- Fine/coarse dither grain, GPU/CPU engines, HDR-aware GPU processing/fallback.
- Profiles, search/reordering, portable image bundles and global hotkeys.
- Executable-triggered setups, handoff delays and pause/resume automation.
- Light/dark appearance and adjustable acrylic settings-window tint.

The primary display remains the capture source. Full HDR depends on Windows,
the display and the supported graphics path; unsupported HDR falls back to SDR.

**AmbientMonitor-v$version-source.zip** contains the matching source.
The original project is https://github.com/CasketPizza/AmbientMonitor.
License: GPL-3.0; third-party notices are included in the archives.

Verification: clean-source self-contained Release publish; packaged CPU and GPU
workers rendered and shut down off-screen without desktop capture or tray icons.
"@
Write-Document ([System.IO.Path]::Combine($release, 'UPLOAD-INSTRUCTIONS.md')) @"
# Browser release upload

1. In the NEW repository, open Releases and choose Draft a new release.
2. Create tag **v$version** on the branch containing the uploaded source.
3. Title: **Ambient Monitor v$version**.
4. Paste RELEASE-NOTES.md into the release description.
5. Attach these three files from this folder:
   - AmbientMonitor-v$version-win-x64.zip
   - AmbientMonitor-v$version-source.zip
   - SHA256SUMS.txt
6. Publish the release.

Use 01-New-Repository's CONTENTS for Add file > Upload files in the new repo.
Do not upload this release folder as repository source or upload the source ZIP
as a replacement for the actual source tree.
"@
Write-Document ([System.IO.Path]::Combine($output, 'START-HERE.md')) @"
# Ready for browser upload - Ambient Monitor v$version

## 01-New-Repository
Open the new GitHub repository, choose Add file > Upload files, and drag the
CONTENTS of 01-New-Repository into the upload area. Keep Shaders, Tests, tools
and licenses as folders. Include .gitignore and .gitattributes. Commit the upload.
The project file must be at the repository root, not inside 01-New-Repository.

## 02-Release-v$version
Follow UPLOAD-INSTRUCTIONS.md in that folder to publish the ZIP assets and
checksums as a GitHub Release, using RELEASE-NOTES.md as its description.

The original GitHub repository has not been changed by this packaging operation.
If you archive it later, first put a link to the new repository in its README.
"@
"Prepared repository: $repo"
"Prepared release: $release"
"Published worker smoke checks: CPU PASS, GPU PASS"
"Staging (not an upload folder): $stage"
