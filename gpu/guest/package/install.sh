#!/bin/bash
# Pome Virtual GPU guest installer (macOS 26 Tahoe in Hyper-V). SPDX-License-Identifier: MIT
#   sudo ./install.sh            install the kexts and the Metal layer
#   sudo ./install.sh --check    report what is installed, loaded and connected; change nothing
# Requires the settings pome's "Turn on virtual GPU" writes to OpenCore: csr-active-config 0x0A43 and
# boot-args amfi_get_out_of_my_way=0x1 amfi=0x80 ipc_control_port_options=0. They relax guest
# code-signing checks, so pome applies them only when the user opts in.
# The package is on the EFI volume: sudo diskutil mount POME-BOOT; sudo /Volumes/POME-BOOT/PomeGPU/install.sh
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
LE=/Library/Extensions; GB=/Library/GPUBundles
[ "$(id -u)" = 0 ] || { echo "run with sudo"; exit 1; }
major=$(sw_vers -productVersion | cut -d. -f1)

if [ "${1:-}" = "--check" ]; then
  for k in PVGTransport PVGAccel; do
    printf "%-14s installed:%s loaded:%s\n" "$k" "$([ -d "$LE/$k.kext" ] && echo yes || echo no)" \
      "$(kmutil showloaded 2>/dev/null | grep -q "org.pome.$k" && echo yes || echo no)"
  done
  ioreg -r -c PVGTransport -d 1 | grep -E 'PVGHostRenderer|PVGVenusAvailable|PVGPreferredAdapter' || echo "PVGTransport: not connected to pvg-host"
  csrutil status 2>/dev/null | head -1 || true
  nvram boot-args 2>/dev/null | grep -q amfi_get_out_of_my_way || echo "boot-args lack amfi_get_out_of_my_way: turn on the virtual GPU in pome, then restart"
  system_profiler SPDisplaysDataType 2>/dev/null | grep -E 'Chipset Model|Metal' || echo "no Metal device reported"
  exit 0
fi

[ "$major" -ge 26 ] || { echo "The Pome Virtual GPU targets macOS 26 Tahoe (found $(sw_vers -productVersion))."; exit 1; }
shasum -a 256 -c "$HERE/SHA256SUMS" >/dev/null || { echo "package checksum mismatch"; exit 1; }

for k in PVGTransport PVGAccel; do
  rm -rf "$LE/$k.kext" && ditto "$HERE/Library/Extensions/$k.kext" "$LE/$k.kext"
  chown -R root:wheel "$LE/$k.kext" && chmod -R go-w,a+rX "$LE/$k.kext"
done
for b in PVGMTLDriver.bundle pvgmtl; do
  rm -rf "$GB/$b" && ditto "$HERE/Library/GPUBundles/$b" "$GB/$b"
  chown -R root:wheel "$GB/$b" && chmod -R go-w,a+rX "$GB/$b"
done
install -m 644 -o root -g wheel "$HERE/Library/GPUBundles/pvgmtl-allow.txt" "$GB/pvgmtl-allow.txt"

# Third-party kexts load from the Auxiliary Kernel Collection. SIP 0x0A43 includes
# CSR_ALLOW_UNAPPROVED_KEXTS, so no approval prompt should appear; a restart loads them.
kmutil load -p "$LE/PVGTransport.kext" -p "$LE/PVGAccel.kext" 2>/dev/null || true
kmutil install --update-all --check-rebuild --volume-root / 2>/dev/null || kmutil install --update-all --volume-root /
echo "Installed. If System Settings > Privacy & Security asks to allow the extensions, allow them. Then restart."
echo "After restart: sudo $0 --check"
