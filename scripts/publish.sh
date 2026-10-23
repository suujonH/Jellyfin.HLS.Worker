#!/bin/sh
# Publishes framework-dependent packages of the worker, its desktop app and the TrueCache tool into artifacts/;
# they run on an installed .NET 10 runtime and ASP.NET Core 10 runtime.
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

version=$(dotnet msbuild "$root/src/Jellyfin.Hls.Worker/Jellyfin.Hls.Worker.csproj" -getProperty:Version | tr -d '\r\n ')
artifacts="$root/artifacts"

publish() {
    dotnet publish "$1" -c Release -r "$2" --self-contained false -o "$3" -p:PublishSingleFile="$4" -p:DebugType=none
}

for rid in "$@"; do
    name="jellyfin-hls-worker-$version-$rid"
    package="$artifacts/$name"
    rm -rf "$package"

    # The worker and the app are single files that share the native libraries beside them; the tool keeps its own folder,
    # as the server code it runs loads its assemblies from there.
    publish "$root/src/Jellyfin.Hls.Worker/Jellyfin.Hls.Worker.csproj" "$rid" "$package" true
    publish "$root/src/Jellyfin.Hls.Worker.Gui/Jellyfin.Hls.Worker.Gui.csproj" "$rid" "$package" true
    publish "$tool" "$rid" "$package/truecache" false

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
            cp "$root/deploy/linux/"* "$package/"
            chmod 755 "$package/jellyfin-hls-worker" "$package/jellyfin-hls-worker-gui" "$package/install.sh" \
                "$package/truecache/Jellyfin.TrueCache.Tool"
            archive="$artifacts/$name.tar.gz"
            tar -czf "$archive" -C "$artifacts" "$name"
            ;;
    esac

    echo "已生成 $archive"
done
