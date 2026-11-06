#!/bin/bash
# Pome Virtual GPU guest uninstaller. SPDX-License-Identifier: MIT
# Removes the accelerator and the Metal layer. PVGTransport is injected by OpenCore: pome's
# "Turn off virtual GPU" removes it from the EFI disk.
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo "run with sudo"; exit 1; }
LE=/Library/Extensions; KC=/Library/KernelCollections/AuxiliaryKernelExtensions.kc
rm -rf "$LE/PVGTransport.kext" "$LE/PVGAccel.kext" \
       /Library/GPUBundles/PVGMTLDriver.bundle /Library/GPUBundles/pvgmtl /Library/GPUBundles/pvgmtl-allow.txt
T=$(mktemp -d /var/tmp/pome-gpu.XXXXXX); trap 'rm -rf "$T"' EXIT
args=(--allow-missing-kdk -a x86_64)
[ -f /System/Library/Kernels/kernel ] && args+=(--kernel /System/Library/Kernels/kernel)
if kmutil create -n aux --volume-root / "${args[@]}" -B /System/Library/KernelCollections/BootKernelExtensions.kc \
     -S /System/Library/KernelCollections/SystemKernelExtensions.kc --repository "$LE" -A "$T/aux.kc" -z >"$T/kmutil.log" 2>&1 \
   && [ -s "$T/aux.kc" ]; then
  mv -f "$T/aux.kc" "$KC"
elif [ -f "$KC.before-pome-gpu" ]; then
  cp -p "$KC.before-pome-gpu" "$KC"     # no other third-party kexts to build from: put the old one back
else
  rm -f "$KC"                           # there was none before the install
fi
rm -f "$KC.before-pome-gpu"
echo "Removed. Restart to unload the accelerator; macOS returns to software rendering."
