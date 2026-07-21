<#
.SYNOPSIS
    Builds the complete Obstruo release layout into .\publish.

.DESCRIPTION
    Produces the exact structure the installer expects (InstallPayload.Locate):

        publish\
            Obstruo.Installer.exe  (+ runtime)      ← what the user runs
            Obstruo.Watchdog.exe                     ← single-file, launched by the
                                                       installer BEFORE files are copied
            PRIVACY-POLICY.txt / THIRD-PARTY-NOTICES.txt
            payload\service\   ← Obstruo.Service publish output
            payload\ui\        ← Obstruo.UI publish output
            payload\watchdog\  ← Obstruo.Watchdog publish output (installed copy)

    Always publishes from source — never assemble this folder by hand, or a
    stale payload ships silently. PDBs are stripped from the output.

    Then bundles the matching G.1 verifier into the payload root, zips the whole
    thing to Obstruo-v<version>-<runtime>.zip at the repo root, and writes a
    <zip>.sha256 next to it — so the output is ready to attach to the GitHub
    release as-is (unsigned builds are fine for testing; SmartScreen will warn).

    For a signed public release, sign the payload BEFORE zipping:
        pwsh .\scripts\sign-selfsigned.ps1 -Path .\publish   # or a real cert
    then re-run the zip/hash step (or pass -SkipBuild to only repackage).
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutDir = "publish",
    # Repackage an already-built (e.g. freshly signed) .\publish without rebuilding.
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root $OutDir

# Version drives the zip/verifier filenames — single source of truth is the
# ObstruoVersion constant the binaries are stamped with.
$versionFile = Join-Path $root 'Obstruo.Shared\ObstruoVersion.cs'
$version = ([regex]::Match((Get-Content $versionFile -Raw), 'Current\s*=\s*"([^"]+)"')).Groups[1].Value
if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read Current version from $versionFile" }
Write-Host "Obstruo version: $version" -ForegroundColor Cyan

if (-not $SkipBuild -and (Test-Path $out)) {
    Write-Host "Cleaning $out"
    Remove-Item -Recurse -Force $out
}

# Stamp the exact build commit into every binary's InformationalVersion so the
# shipped release self-identifies (finding L3). Directory.Build.props also does
# this per-project, but capturing it once here guarantees a definite value even
# from a CI/shallow checkout, and records it in the output.
$commit = (& git -C $root rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) { $commit = "" }
else { Write-Host "Build commit: $commit" -ForegroundColor Cyan }

function Publish-Project {
    param([string]$Project, [string]$Output, [string[]]$ExtraArgs = @())

    if ($commit) { $ExtraArgs += "-p:SourceRevisionId=$commit" }

    Write-Host "== dotnet publish $Project -> $Output" -ForegroundColor Cyan
    dotnet publish (Join-Path $root $Project) `
        -c $Configuration -r $Runtime --self-contained true `
        -o $Output @ExtraArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Project" }
}

if (-not $SkipBuild) {
    # ── Installer (publish root) ──────────────────────────────────────────────
    Publish-Project "Obstruo.Installer" $out

    # ── Payload: service / ui / watchdog ──────────────────────────────────────
    Publish-Project "Obstruo.Service"  (Join-Path $out "payload\service")
    Publish-Project "Obstruo.UI"       (Join-Path $out "payload\ui")
    Publish-Project "Obstruo.Watchdog" (Join-Path $out "payload\watchdog")

    # ── Standalone Watchdog next to the installer ─────────────────────────────
    # The installer launches this copy before any files exist in Program Files,
    # so it must run with zero sibling dependencies — publish it single-file.
    $wdTemp = Join-Path $out "_watchdog-singlefile"
    Publish-Project "Obstruo.Watchdog" $wdTemp @("-p:PublishSingleFile=true")
    Copy-Item (Join-Path $wdTemp "Obstruo.Watchdog.exe") $out -Force
    Remove-Item -Recurse -Force $wdTemp

    # ── Strip debug symbols ───────────────────────────────────────────────────
    Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item -Force

    Write-Host ""
    Write-Host "Publish complete: $out" -ForegroundColor Green
} else {
    if (-not (Test-Path $out)) { throw "-SkipBuild set but $out does not exist — build first." }
    Write-Host "Skipping build — repackaging existing $out" -ForegroundColor Yellow
}

# ── Bundle the G.1 verifier so it ships inside the download ────────────────────
# Makes the release notes' "run the bundled verifier" literally true: a tester
# extracts the zip and the script is right there at the root.
$verifier = Join-Path $PSScriptRoot ("Verify-Obstruo-v{0}-G1.ps1" -f $version)
if (Test-Path $verifier) {
    Copy-Item $verifier $out -Force
    Write-Host "Bundled verifier: $(Split-Path $verifier -Leaf)" -ForegroundColor Green
} else {
    Write-Warning "Verifier not found ($verifier) — release will ship without it."
}

# ── Package: zip + SHA-256 (ready to attach to the GitHub release) ─────────────
$zipName = "Obstruo-v$version-$Runtime.zip"
$zipPath = Join-Path $root $zipName
if ([System.IO.File]::Exists($zipPath)) { [System.IO.File]::Delete($zipPath) }

# Use the .NET streaming zipper, not Compress-Archive: on a ~650 MB self-contained
# payload Compress-Archive buffers in memory and effectively hangs, whereas
# ZipFile.CreateFromDirectory streams (seconds, low memory). includeBaseDirectory
# = false so the archive root holds Installer.exe / payload\ directly.
Write-Host "Zipping -> $zipName ..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $out, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
$shaPath = "$zipPath.sha256"
# sha256sum-style: "<hash>  <filename>" — matches the v1.0.0 asset format.
Set-Content -Path $shaPath -Value "$hash  $zipName" -Encoding ASCII

Write-Host ""
Write-Host "Package ready:" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "  $shaPath"
Write-Host "  SHA-256: $hash" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. (public release only) sign the payload, then re-run with -SkipBuild to repackage."
Write-Host "  2. Attach the zip + .sha256 to the v$version release and put the SHA-256 in the notes."
Write-Host "  3. Install on the test machine, then run the bundled Verify-Obstruo-v$version-G1.ps1 (elevated)."
