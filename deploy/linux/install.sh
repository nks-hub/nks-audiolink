#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Run as root." >&2
    exit 1
fi

binary=${1:-}
if [ "$#" -ne 1 ] || [ ! -f "$binary" ] || [ ! -r "$binary" ]; then
    echo "Usage: install.sh /path/to/NksAudioLink.Server" >&2
    exit 1
fi

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ -L /etc/nks-audiolink/server.json ] ||
   { [ -e /etc/nks-audiolink/server.json ] && [ ! -f /etc/nks-audiolink/server.json ]; }; then
    echo "server.json must be a regular file, not a symlink." >&2
    exit 1
fi
if ! getent group audio >/dev/null; then
    echo "The audio group is missing." >&2
    exit 1
fi
if ! getent group nks-audiolink >/dev/null; then
    groupadd --system nks-audiolink
fi
if ! id nks-audiolink >/dev/null 2>&1; then
    useradd --system --gid nks-audiolink --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin --groups audio nks-audiolink
elif ! id -nG nks-audiolink | tr ' ' '\n' | grep -qx audio; then
    usermod --append --groups audio nks-audiolink
fi
if [ "$(id -u nks-audiolink)" -eq 0 ]; then
    echo "Refusing to use a root account for the service." >&2
    exit 1
fi

install -d -m 0755 /opt/nks-audiolink
# Atomic rename works when upgrading while the old executable is mapped by a running service.
staged=$(mktemp /opt/nks-audiolink/.NksAudioLink.Server.XXXXXX)
trap 'rm -f -- "$staged"' EXIT HUP INT TERM
install -m 0755 "$binary" "$staged"
mv -f -- "$staged" /opt/nks-audiolink/NksAudioLink.Server
trap - EXIT HUP INT TERM
install -d -o root -g nks-audiolink -m 0750 /etc/nks-audiolink
if [ ! -e /etc/nks-audiolink/server.json ]; then
    install -o root -g nks-audiolink -m 0640 "$script_dir/server.json.example" /etc/nks-audiolink/server.json
else
    chown root:nks-audiolink /etc/nks-audiolink/server.json
    chmod 0640 /etc/nks-audiolink/server.json
fi
install -m 0644 "$script_dir/nks-audiolink.service" /etc/systemd/system/nks-audiolink.service
systemctl daemon-reload
echo "Installed. Check /etc/nks-audiolink/server.json, then run: systemctl enable --now nks-audiolink"
echo "If the service was already running, restart it when ready: systemctl restart nks-audiolink"
