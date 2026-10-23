#!/bin/sh
# Installs or upgrades the worker of this package as a systemd service. Run as root from the folder of the package:
#   sudo sh ./install.sh
set -eu

# The files are made readable by the worker's user whatever the umask of root; the private ones are restricted below.
umask 022

prefix=/opt/jellyfin-hls-worker
config_dir=/etc/jellyfin-hls-worker
config="$config_dir/worker.json"
work=/var/lib/jellyfin-hls-worker
user=jellyfin-hls
unit=/etc/systemd/system/jellyfin-hls-worker.service
package=$(cd "$(dirname "$0")" && pwd)

if [ "$(id -u)" -ne 0 ]; then
    echo "请以 root 运行：sudo sh $0" >&2
    exit 1
fi

# The package is framework-dependent: nothing is installed until the worker starts on the runtime of this machine.
chmod 755 "$package/jellyfin-hls-worker"
if ! "$package/jellyfin-hls-worker" --version >/dev/null 2>&1; then
    echo "无法启动 Worker：需要安装 .NET 10 运行时和 ASP.NET Core 10 运行时（例如 aspnetcore-runtime-10.0 包），且安装在系统位置。" >&2
    exit 1
fi

# The group of the same name is made first: useradd does not make it on every system.
if ! getent group "$user" >/dev/null; then
    groupadd --system "$user"
fi
if ! id "$user" >/dev/null 2>&1; then
    useradd --system --gid "$user" --home-dir "$work" --no-create-home --shell /usr/sbin/nologin "$user"
fi

# Hardware encoders open the render nodes, which belong to these groups where they exist.
for group in video render; do
    if getent group "$group" >/dev/null; then
        usermod -aG "$group" "$user"
    fi
done

# The new files are put beside the old ones and swapped in, so a running worker keeps its files until it restarts.
rm -rf "$prefix.new"
mkdir -p "$prefix.new"
cp -R "$package/." "$prefix.new/"
# The package may have been extracted with modes that the worker's user cannot read.
chmod -R u=rwX,go=rX "$prefix.new"
chmod 755 "$prefix.new/jellyfin-hls-worker" "$prefix.new/install.sh"
[ -f "$prefix.new/jellyfin-hls-worker-gui" ] && chmod 755 "$prefix.new/jellyfin-hls-worker-gui"
[ -f "$prefix.new/truecache/Jellyfin.TrueCache.Tool" ] && chmod 755 "$prefix.new/truecache/Jellyfin.TrueCache.Tool"
rm -rf "$prefix.old"
if [ -d "$prefix" ]; then
    mv "$prefix" "$prefix.old"
fi
mv "$prefix.new" "$prefix"
rm -rf "$prefix.old"

mkdir -p "$work"
chown "$user:$user" "$work"
chmod 750 "$work"

# The settings hold the API key and the keys of the connections: readable by the worker only.
mkdir -p "$config_dir"
if [ ! -f "$config" ]; then
    "$prefix/jellyfin-hls-worker" init --config "$config" >/dev/null
    sed -i "s#\"WorkDirectory\": \"work\"#\"WorkDirectory\": \"$work\"#" "$config"
    created=1
else
    created=0
fi
chown "root:$user" "$config_dir" "$config"
chmod 750 "$config_dir"
chmod 640 "$config"

cp "$package/jellyfin-hls-worker.service" "$unit"
systemctl daemon-reload
systemctl try-restart jellyfin-hls-worker.service

echo "已安装到 $prefix。"
if [ "$created" -eq 1 ]; then
    echo "已创建配置 $config：填写 Server、ApiKey、连接和 Hls 后检查并启动："
else
    echo "沿用已有的配置 $config。检查并启动："
fi
echo "  sudo -u $user $prefix/jellyfin-hls-worker check --config $config"
echo "  sudo systemctl enable --now jellyfin-hls-worker"
