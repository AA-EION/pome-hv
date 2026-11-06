#!/bin/bash
# Pome Virtual GPU guest uninstaller. SPDX-License-Identifier: MIT
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo "run with sudo"; exit 1; }
rm -rf /Library/Extensions/PVGTransport.kext /Library/Extensions/PVGAccel.kext \
       /Library/GPUBundles/PVGMTLDriver.bundle /Library/GPUBundles/pvgmtl /Library/GPUBundles/pvgmtl-allow.txt
kmutil install --update-all --volume-root / || true
echo "Removed. Restart to unload the extensions; macOS returns to software rendering."
