# Publishes framework-dependent packages of the desktop worker, the TrueCache tool and jellyfin-ffmpeg into artifacts/;
# they run on an installed .NET 10 runtime and ASP.NET Core 10 runtime. jellyfin-ffmpeg is downloaded once into
# artifacts/jellyfin-ffmpeg and checked against its SHA-256.
#   ./scripts/publish.ps1 -Jellyfin ../jellyfin [-Runtime win-x64,linux-x64,linux-arm64]
param(
    # The jellyfin repository, whose src/Jellyfin.TrueCache.Tool is published with the worker.
    [Parameter(Mandatory = $true)]
    [string]$Jellyfin,
    [string[]]$Runtime = @('win-x64', 'linux-x64', 'linux-arm64')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path (Resolve-Path $Jellyfin) 'src/Jellyfin.TrueCache.Tool/Jellyfin.TrueCache.Tool.csproj'
if (-not (Test-Path $tool)) {
    throw "没有找到 $tool"
}

$app = Join-Path $root 'src/Jellyfin.Hls.Worker.Gui/Jellyfin.Hls.Worker.Gui.csproj'
$version = (dotnet msbuild $app -getProperty:Version).Trim()
$artifacts = Join-Path $root 'artifacts'

function Invoke-Publish([string]$Project, [string]$Rid, [string]$Output, [bool]$SingleFile) {
    dotnet publish $Project -c Release -r $Rid --self-contained false -o $Output "-p:PublishSingleFile=$SingleFile" -p:DebugType=none
    if ($LASTEXITCODE -ne 0) {
        throw "发布 $Project ($Rid) 失败"
    }
}

# Keep in step with scripts/publish.sh.
$ffmpegVersion = '8.1.3-1'
$ffmpegAssets = @{
    'win-x64' = @('portable_win64-clang-gpl.zip', '3691f8b2d4c39c511b5faff1c8c4687bd782dac823e63ba69f0755f7ce1af9ce')
    'linux-x64' = @('portable_linux64-gpl.tar.xz', 'b86dc023c64a5a9a410e6e3a938a970460214fae78f31c1553c9f88f1c289b6a')
    'linux-arm64' = @('portable_linuxarm64-gpl.tar.xz', '7bc8e8d0986f7f4693f63e9728570f671f07b6e21c05d5465dd7ce461d1631dc')
}

# ffmpeg and ffprobe go next to the app, where the worker looks for them before the PATH.
function Add-Ffmpeg([string]$Rid, [string]$Package) {
    if (-not $ffmpegAssets.ContainsKey($Rid)) {
        throw "没有 $Rid 的 jellyfin-ffmpeg"
    }

    $asset, $sha256 = $ffmpegAssets[$Rid]
    $name = "jellyfin-ffmpeg_${ffmpegVersion}_$asset"
    $cache = Join-Path $artifacts 'jellyfin-ffmpeg'
    $file = Join-Path $cache $name
    if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $sha256) {
        New-Item -ItemType Directory -Force $cache | Out-Null
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri "https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v$ffmpegVersion/$name" -OutFile $file
        if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $sha256) {
            throw "$name 的 SHA-256 不符"
        }
    }

    if ($asset.EndsWith('.zip')) {
        Expand-Archive -Path $file -DestinationPath $Package -Force
    }
    else {
        tar -xJf $file -C $Package ffmpeg ffprobe
        if ($LASTEXITCODE -ne 0) {
            throw "解压 $name 失败"
        }
    }
}

foreach ($rid in $Runtime) {
    $name = "jellyfin-hls-worker-$version-$rid"
    $package = Join-Path $artifacts $name
    if (Test-Path $package) {
        Remove-Item -Recurse -Force $package
    }

    # The app is a single file beside the native libraries it loads; the tool keeps its own folder, as the server code it
    # runs loads its assemblies from there.
    Invoke-Publish $app $rid $package $true
    Invoke-Publish $tool $rid (Join-Path $package 'truecache') $false
    Add-Ffmpeg $rid $package

    # The symbols of the native libraries come with their packages.
    Get-ChildItem -Path $package -Filter '*.pdb' -Recurse | Remove-Item -Force
    Copy-Item (Join-Path $root 'README.md') $package

    if ($rid.StartsWith('win-')) {
        $archive = Join-Path $artifacts "$name.zip"
        Remove-Item -Force -ErrorAction SilentlyContinue $archive
        Compress-Archive -Path $package -DestinationPath $archive
    }
    else {
        # The archive of Windows has no modes: a start needs chmod +x.
        $archive = Join-Path $artifacts "$name.tar.gz"
        tar -czf $archive -C $artifacts $name
        if ($LASTEXITCODE -ne 0) {
            throw "打包 $name 失败"
        }
    }

    Write-Host "已生成 $archive"
}
