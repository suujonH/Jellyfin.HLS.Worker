#!/bin/sh
# Publishes framework-dependent packages of the desktop worker, the TrueCache tool and jellyfin-ffmpeg into artifacts/;
# they run on an installed .NET 10 runtime and ASP.NET Core 10 runtime. jellyfin-ffmpeg is downloaded once into
# artifacts/jellyfin-ffmpeg and checked against its SHA-256.
#   sh scripts/publish.sh <jellyfin repository> [runtime...]     runtimes: win-x64 linux-x64 linux-arm64 (default all)
set -eu

if [ $# -lt 1 ]; then
    echo "用法：$0 <jellyfin 仓库> [win-x64 linux-x64 linux-arm64]" >&2
    exit 2
fi

root=$(cd "$(dirname "$0")/.." && pwd)
tool="$(cd "$1" && pwd)/src/Jellyfin.TrueCache.Tool/Jellyfin.TrueCache.Tool.csproj"
shift
if [ ! -f "$tool" ]; then
    echo "没有找到 $tool" >&2
    exit 2
fi

if [ $# -eq 0 ]; then
    set -- win-x64 linux-x64 linux-arm64
fi

app="$root/src/Jellyfin.Hls.Worker.Gui/Jellyfin.Hls.Worker.Gui.csproj"
version=$(dotnet msbuild "$app" -getProperty:Version | tr -d '\r\n ')
artifacts="$root/artifacts"

publish() {
    dotnet publish "$1" -c Release -r "$2" --self-contained false -o "$3" -p:PublishSingleFile="$4" -p:DebugType=none
}

# Keep in step with scripts/publish.ps1.
ffmpeg_version=8.1.3-1

# ffmpeg and ffprobe go next to the app, where the worker looks for them before the PATH.
add_ffmpeg() {
    case "$1" in
        win-x64) asset=portable_win64-clang-gpl.zip; sha256=3691f8b2d4c39c511b5faff1c8c4687bd782dac823e63ba69f0755f7ce1af9ce ;;
        linux-x64) asset=portable_linux64-gpl.tar.xz; sha256=b86dc023c64a5a9a410e6e3a938a970460214fae78f31c1553c9f88f1c289b6a ;;
        linux-arm64) asset=portable_linuxarm64-gpl.tar.xz; sha256=7bc8e8d0986f7f4693f63e9728570f671f07b6e21c05d5465dd7ce461d1631dc ;;
        *) echo "没有 $1 的 jellyfin-ffmpeg" >&2; exit 2 ;;
    esac

    file="$artifacts/jellyfin-ffmpeg/jellyfin-ffmpeg_${ffmpeg_version}_$asset"
    if [ ! -f "$file" ] || ! echo "$sha256  $file" | sha256sum -c --status; then
        mkdir -p "$artifacts/jellyfin-ffmpeg"
        curl -fsSL -o "$file" "https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v$ffmpeg_version/jellyfin-ffmpeg_${ffmpeg_version}_$asset"
        if ! echo "$sha256  $file" | sha256sum -c --status; then
            echo "$file 的 SHA-256 不符" >&2
            exit 1
        fi
    fi

    case "$asset" in
        *.zip) unzip -q -o "$file" ffmpeg.exe ffprobe.exe -d "$2" ;;
        *) tar -xJf "$file" -C "$2" ffmpeg ffprobe ;;
    esac
}

for rid in "$@"; do
    name="jellyfin-hls-worker-$version-$rid"
    package="$artifacts/$name"
    rm -rf "$package"

    # The app is a single file beside the native libraries it loads; the tool keeps its own folder, as the server code it
    # runs loads its assemblies from there.
    publish "$app" "$rid" "$package" true
    publish "$tool" "$rid" "$package/truecache" false
    add_ffmpeg "$rid" "$package"

    # The symbols of the native libraries come with their packages.
    find "$package" -name '*.pdb' -delete
    cp "$root/README.md" "$package/"

    case "$rid" in
        win-*)
            archive="$artifacts/$name.zip"
            rm -f "$archive"
            (cd "$artifacts" && zip -qr "$archive" "$name")
            ;;
        *)
            chmod 755 "$package/jellyfin-hls-worker-gui" "$package/truecache/Jellyfin.TrueCache.Tool" "$package/ffmpeg" "$package/ffprobe"
            archive="$artifacts/$name.tar.gz"
            tar -czf "$archive" -C "$artifacts" "$name"
            ;;
    esac

    echo "已生成 $archive"
done
