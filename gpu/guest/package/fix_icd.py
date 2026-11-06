#!/usr/bin/env python3
"""Rewrites Mesa's Venus ICD manifest for the installed location. SPDX-License-Identifier: MIT"""
import json
import sys

src, dst = sys.argv[1], sys.argv[2]
with open(src, encoding="utf-8") as f:
    manifest = json.load(f)
manifest["ICD"]["library_path"] = "/Library/GPUBundles/pvgmtl/libvulkan_virtio.dylib"
with open(dst, "w", encoding="utf-8", newline="\n") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
print("ICD:", manifest["ICD"]["library_path"], "api", manifest["ICD"].get("api_version"))
