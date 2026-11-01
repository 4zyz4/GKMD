#!/usr/bin/env bash
# Manual Linux smoke test for GKMD's net10.0 build.
#
#   1. Publish the test from Windows (self-contained, no .NET needed in WSL):
#        dotnet publish tools/LinuxSmokeTest/GkmdLinuxSmoke.csproj \
#          -c Release -r linux-x64 --self-contained true
#   2. Run this script from WSL (it re-executes itself through sudo, so you
#      only have to type your WSL password):
#        bash tools/LinuxSmokeTest/run-linux-smoke.sh [profile] [seconds]
#
# Defaults: profile=dualshock-4-v2, seconds=6.
# It loads vhci-hcd/usbhid/evdev, creates a controller over the in-kernel
# vhci_hcd, checks enumeration, usbhid binding and evdev input reports, then
# detaches and checks that every vhci port is free again.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROFILE="${1:-dualshock-4-v2}"
RUN_SECONDS="${2:-6}"
PUBLISH_DIR="${GKMD_SMOKE_PUBLISH_DIR:-$HERE/bin/Release/net10.0/linux-x64/publish}"
BIN="$PUBLISH_DIR/GkmdLinuxSmoke"

if [[ ! -f "$BIN" ]]; then
    echo "ERROR: smoke binary not found: $BIN" >&2
    echo "Publish it from Windows first:" >&2
    echo "  dotnet publish \"$HERE/GkmdLinuxSmoke.csproj\" -c Release -r linux-x64 --self-contained true" >&2
    exit 2
fi

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
    echo "== root required: re-running through sudo =="
    exec sudo -E bash "$HERE/run-linux-smoke.sh" "$@"
fi

WORKDIR="${TMPDIR:-/tmp}/gkmd-linux-smoke"
rm -rf "$WORKDIR"
mkdir -p "$WORKDIR"
cp -a "$PUBLISH_DIR/." "$WORKDIR/"
chmod +x "$WORKDIR/GkmdLinuxSmoke"
trap 'rm -rf "$WORKDIR"' EXIT

if ! modinfo vhci-hcd >/dev/null 2>&1; then
    echo "ERROR: this kernel has no vhci-hcd module (CONFIG_USBIP_VHCI_HCD)." >&2
    exit 2
fi

echo "== loading modules =="
if ! modprobe vhci-hcd; then
    echo "ERROR: modprobe vhci-hcd failed" >&2
    exit 2
fi
modprobe usbhid 2>/dev/null || true
modprobe evdev 2>/dev/null || true
modprobe hid-generic 2>/dev/null || true
sleep 0.5

echo "== platform devices =="
ls /sys/devices/platform | grep -i vhci || true

echo
echo "== smoke test: profile=$PROFILE seconds=$RUN_SECONDS =="
"$WORKDIR/GkmdLinuxSmoke" "$PROFILE" "$RUN_SECONDS"
rc=$?

echo
echo "== dmesg (last 30 lines) =="
dmesg | tail -n 30 || true

echo
if [[ $rc -eq 0 ]]; then
    echo "SMOKE TEST PASSED"
else
    echo "SMOKE TEST FAILED (exit $rc)"
fi
exit $rc
