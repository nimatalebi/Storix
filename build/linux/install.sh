#!/bin/sh
# Installs (or upgrades) the KeepstoneBackup agent as a systemd service.
# Usage: sudo ./install.sh            (from an extracted keepstonebackup-<version>-linux-x64.tar.gz)
#        sudo ./install.sh --uninstall
set -eu

PREFIX=/opt/keepstonebackup
UNIT=/etc/systemd/system/keepstonebackup.service
HERE=$(cd "$(dirname "$0")" && pwd)

if [ "$(id -u)" -ne 0 ]; then
    echo "Run as root (sudo)." >&2
    exit 1
fi

if [ "${1:-}" = "--uninstall" ]; then
    systemctl disable --now keepstonebackup 2>/dev/null || true
    rm -f "$UNIT" /usr/local/bin/keepstonebackup
    rm -rf "$PREFIX"
    systemctl daemon-reload
    echo "KeepstoneBackup removed. Data, logs and the secret key are kept in /var/lib/keepstonebackup."
    exit 0
fi

systemctl stop keepstonebackup 2>/dev/null || true

# Upgrade from the agent's former name (Storix): remove the old service and move its data here.
if [ -f /etc/systemd/system/storix.service ]; then
    systemctl disable --now storix 2>/dev/null || true
    rm -f /etc/systemd/system/storix.service /usr/local/bin/storix
    rm -rf /opt/storix
fi
if [ -d /var/lib/storix ] && [ ! -e /var/lib/keepstonebackup ]; then
    mv /var/lib/storix /var/lib/keepstonebackup
fi

mkdir -p "$PREFIX"
cp -R "$HERE"/. "$PREFIX"/
chmod 755 "$PREFIX/KeepstoneBackup.Service" "$PREFIX/keepstonebackup"
ln -sf "$PREFIX/keepstonebackup" /usr/local/bin/keepstonebackup
install -m 644 "$HERE/keepstonebackup.service" "$UNIT"
systemctl daemon-reload
systemctl enable --now keepstonebackup
echo "KeepstoneBackup is running. Add jobs with: keepstonebackup apply job.json   (see keepstonebackup help)"
