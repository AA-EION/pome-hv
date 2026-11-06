#!/bin/bash
# Pome Virtual GPU guest installer (macOS 26 Tahoe in Hyper-V). SPDX-License-Identifier: MIT
#   sudo bash install.sh            install the accelerator and the Metal layer
#   sudo bash install.sh --check    report what is installed, loaded and connected; change nothing
# Requires the settings pome's "Turn on virtual GPU" writes to OpenCore: csr-active-config 0x0A43,
# boot-args amfi_get_out_of_my_way=0x1 amfi=0x80 ipc_control_port_options=0, and PVGTransport.kext
# in Kernel > Add. They relax guest code-signing checks, so pome applies them only when asked.
# The package is on the EFI volume: sudo diskutil mount POME-BOOT; sudo bash /Volumes/POME-BOOT/PomeGPU/install.sh
#
# Where each kext loads from:
#  - PVGTransport extends MacHyperVSupport, which OpenCore injects at boot and which is in no kernel
#    collection on disk, so kmutil cannot link it. OpenCore injects PVGTransport beside it.
#  - PVGAccel links against IOAcceleratorFamily2/IOGraphics/IOSurface (System collection), so it goes
#    into the Auxiliary Kernel Collection. kmutil builds that without a Kernel Debug Kit only with
#    --allow-missing-kdk (`kmutil install --update-all` insists on a KDK on macOS 13+).
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
LE=/Library/Extensions; GB=/Library/GPUBundles
KC=/Library/KernelCollections/AuxiliaryKernelExtensions.kc
KB=/System/Library/KernelCollections/BootKernelExtensions.kc
KS=/System/Library/KernelCollections/SystemKernelExtensions.kc
[ "$(id -u)" = 0 ] || { echo "run with sudo"; exit 1; }
major=$(sw_vers -productVersion | cut -d. -f1)

if [ "${1:-}" = "--check" ]; then
  loaded=$(kmutil showloaded --list-only 2>/dev/null || kextstat 2>/dev/null || true)
  for k in PVGTransport PVGAccel; do
    printf "%-14s loaded:%s\n" "$k" "$(printf '%s' "$loaded" | grep -q "org.pome.$k" && echo yes || echo no)"
  done
  printf "%-14s %s\n" "PVGAccel" "$([ -d "$LE/PVGAccel.kext" ] && echo "installed in $LE" || echo "not in $LE")"
  [ -f "$KC" ] && kmutil inspect -a x86_64 -A "$KC" 2>/dev/null | grep -q org.pome.PVGAccel \
    && echo "Auxiliary collection: holds PVGAccel" || echo "Auxiliary collection: no PVGAccel"
  ioreg -r -c PVGTransport -d 1 | grep -E 'PVGHostRenderer|PVGVenusAvailable|PVGPreferredAdapter' || echo "PVGTransport: not connected to pvg-host"
  csrutil status 2>/dev/null | head -1 || true
  nvram boot-args 2>/dev/null | grep -q amfi_get_out_of_my_way || echo "boot-args lack amfi_get_out_of_my_way: turn on the virtual GPU in pome, then restart"
  system_profiler SPDisplaysDataType 2>/dev/null | grep -E 'Chipset Model|Metal' || echo "no Metal device reported"
  exit 0
fi

[ "$major" -ge 26 ] || { echo "The Pome Virtual GPU targets macOS 26 Tahoe (found $(sw_vers -productVersion))."; exit 1; }
shasum -a 256 -c "$HERE/SHA256SUMS" >/dev/null || { echo "package checksum mismatch"; exit 1; }
T=$(mktemp -d /var/tmp/pome-gpu.XXXXXX); trap 'rm -rf "$T"' EXIT

# An earlier package put PVGTransport here; OpenCore injects it now, and kmutil cannot link it.
rm -rf "$LE/PVGTransport.kext"
rm -rf "$LE/PVGAccel.kext" && ditto "$HERE/Library/Extensions/PVGAccel.kext" "$LE/PVGAccel.kext"
# macOS 26 kmutil silently skips kexts that are not owned by root.
chown -R root:wheel "$LE/PVGAccel.kext" && chmod -R go-w,a+rX "$LE/PVGAccel.kext"
for b in PVGMTLDriver.bundle pvgmtl; do
  rm -rf "$GB/$b" && ditto "$HERE/Library/GPUBundles/$b" "$GB/$b"
  chown -R root:wheel "$GB/$b" && chmod -R go-w,a+rX "$GB/$b"
done
install -m 644 -o root -g wheel "$HERE/Library/GPUBundles/pvgmtl-allow.txt" "$GB/pvgmtl-allow.txt"

# Rebuild the Auxiliary collection from every kext in /Library/Extensions into a temporary file,
# check that it holds PVGAccel, keep the previous one, and only then replace it.
args=(--allow-missing-kdk -a x86_64)
[ -f /System/Library/Kernels/kernel ] && args+=(--kernel /System/Library/Kernels/kernel)
if ! kmutil create -n aux --volume-root / "${args[@]}" -B "$KB" -S "$KS" --repository "$LE" -A "$T/aux.kc" -z >"$T/kmutil.log" 2>&1; then
  tail -20 "$T/kmutil.log"; echo "Building the Auxiliary Kernel Collection failed; the installed one was not changed."; exit 1
fi
kmutil inspect -a x86_64 -A "$T/aux.kc" 2>/dev/null | grep -q org.pome.PVGAccel \
  || { tail -20 "$T/kmutil.log"; echo "kmutil left PVGAccel out of the collection; the installed one was not changed."; exit 1; }
[ ! -f "$KC" ] || [ -f "$KC.before-pome-gpu" ] || cp -p "$KC" "$KC.before-pome-gpu"
mv -f "$T/aux.kc" "$KC"
echo "Installed. Restart to load the accelerator and the Metal layer."
echo "If System Settings > Privacy & Security asks to allow the extension, allow it, then restart again."
echo "After restart: sudo bash $0 --check"
