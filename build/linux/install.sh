#!/bin/sh
# Installs (or upgrades) the Storix agent as a systemd service.
# Usage: sudo ./install.sh            (from an extracted storix-<version>-linux-x64.tar.gz)
#        sudo ./install.sh --uninstall
set -eu

PREFIX=/opt/storix
UNIT=/etc/systemd/system/storix.service
HERE=$(cd "$(dirname "$0")" && pwd)

if [ "$(id -u)" -ne 0 ]; then
    echo "Run as root (sudo)." >&2
    exit 1
fi

if [ "${1:-}" = "--uninstall" ]; then
    systemctl disable --now storix 2>/dev/null || true
    rm -f "$UNIT" /usr/local/bin/storix
    rm -rf "$PREFIX"
    systemctl daemon-reload
    echo "Storix removed. Data, logs and the secret key are kept in /var/lib/storix."
    exit 0
fi

systemctl stop storix 2>/dev/null || true
mkdir -p "$PREFIX"
cp -R "$HERE"/. "$PREFIX"/
chmod 755 "$PREFIX/Storix.Service" "$PREFIX/storix"
ln -sf "$PREFIX/storix" /usr/local/bin/storix
install -m 644 "$HERE/storix.service" "$UNIT"
systemctl daemon-reload
systemctl enable --now storix
echo "Storix is running. Add jobs with: storix apply job.json   (see storix help)"
