#!/bin/sh
# Builds the Linux agent: storix-<version>-linux-x64.tar.gz with the service, the CLI and the systemd unit.
# Usage: ./build/package-linux.sh 1.2.0 [output-folder]
set -eu

VERSION=${1:-1.0.0}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=${2:-$ROOT/artifacts}
STAGE="$OUT/storix-$VERSION-linux-x64"

rm -rf "$STAGE"
mkdir -p "$STAGE"
for project in src/NT.Storix.Service/NT.Storix.Service.csproj src/NT.Storix.Cli/NT.Storix.Cli.csproj; do
    dotnet publish "$ROOT/$project" -c Release -r linux-x64 --self-contained true -p:Version="$VERSION" -o "$STAGE"
done
cp "$ROOT/build/linux/storix.service" "$ROOT/build/linux/install.sh" "$ROOT/LICENSE" "$STAGE/"
chmod +x "$STAGE/install.sh"

tar -C "$OUT" -czf "$OUT/storix-$VERSION-linux-x64.tar.gz" "storix-$VERSION-linux-x64"
rm -rf "$STAGE"
(cd "$OUT" && sha256sum "storix-$VERSION-linux-x64.tar.gz" > "SHA256SUMS-linux.txt")
echo "Created $OUT/storix-$VERSION-linux-x64.tar.gz"
