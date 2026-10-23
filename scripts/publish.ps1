# Publishes framework-dependent packages of the worker, its desktop app and the TrueCache tool into artifacts/;
# they run on an installed .NET 10 runtime and ASP.NET Core 10 runtime.
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

$version = (dotnet msbuild (Join-Path $root 'src/Jellyfin.Hls.Worker/Jellyfin.Hls.Worker.csproj') -getProperty:Version).Trim()
$artifacts = Join-Path $root 'artifacts'

function Invoke-Publish([string]$Project, [string]$Rid, [string]$Output, [bool]$SingleFile) {
    dotnet publish $Project -c Release -r $Rid --self-contained false -o $Output "-p:PublishSingleFile=$SingleFile" -p:DebugType=none
    if ($LASTEXITCODE -ne 0) {
        throw "发布 $Project ($Rid) 失败"
    }
}

foreach ($rid in $Runtime) {
    $name = "jellyfin-hls-worker-$version-$rid"
    $package = Join-Path $artifacts $name
    if (Test-Path $package) {
        Remove-Item -Recurse -Force $package
    }

    # The worker and the app are single files that share the native libraries beside them; the tool keeps its own folder,
    # as the server code it runs loads its assemblies from there.
    Invoke-Publish (Join-Path $root 'src/Jellyfin.Hls.Worker/Jellyfin.Hls.Worker.csproj') $rid $package $true
    Invoke-Publish (Join-Path $root 'src/Jellyfin.Hls.Worker.Gui/Jellyfin.Hls.Worker.Gui.csproj') $rid $package $true
    Invoke-Publish $tool $rid (Join-Path $package 'truecache') $false

    # The symbols of the native libraries come with their packages.
    Get-ChildItem -Path $package -Filter '*.pdb' -Recurse | Remove-Item -Force
    Copy-Item (Join-Path $root 'README.md') $package
    if ($rid.StartsWith('linux-')) {
        Copy-Item (Join-Path $root 'deploy/linux/*') $package
    }

    if ($rid.StartsWith('win-')) {
        $archive = Join-Path $artifacts "$name.zip"
        Remove-Item -Force -ErrorAction SilentlyContinue $archive
        Compress-Archive -Path $package -DestinationPath $archive
    }
    else {
        # The archive of Windows has no modes: install.sh sets them, and a manual start needs chmod +x.
        $archive = Join-Path $artifacts "$name.tar.gz"
        tar -czf $archive -C $artifacts $name
        if ($LASTEXITCODE -ne 0) {
            throw "打包 $name 失败"
        }
    }

    Write-Host "已生成 $archive"
}
