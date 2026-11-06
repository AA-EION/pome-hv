#!/usr/bin/env python3
"""Imports the portable Metal layer from NullMoth into pome as the Pome Virtual GPU Metal driver.

Copyright 2026 pome contributors. SPDX-License-Identifier: MIT (this script only)

    import_from_nullmoth.py <nvidia-macos-driver checkout> [--rev 18ba05f]

What is imported, and under which licence (the licences travel with the files):
  plugin/      Metal driver plugin                      PolyForm Noncommercial 1.0.0 (NullMoth Systems)
  accel/       IOAccelerator kext (one source, 3 builds) PolyForm Noncommercial 1.0.0 (NullMoth Systems)
  translator/  Apple AIR -> SPIR-V (metal2vulkan)       LGPL-3.0-or-later (Anees Iqbal, NullMoth changes)
PolyForm Noncommercial allows changes and redistribution for noncommercial purposes only, with its
terms and the "Required Notice:" line. Copyright headers are kept; only identifiers, file names and
install paths are renamed (NVMTL -> PVGMTL), and the virtual-GPU edits below are applied.
"""
import argparse
import io
import pathlib
import re
import shutil
import subprocess
import tarfile

HERE = pathlib.Path(__file__).resolve().parent
PINNED_REV = "18ba05f38b5eadea8b5ae05834bb51c61fb4c5c3"   # NullMoth v2.7.2
REQUIRED_NOTICE = "Required Notice: Copyright (c) 2026 NullMoth Systems (https://nullmothsystems.com)"
PATHS = ["plugin", "translator", "kexts/NVRM/accel", "kexts/NVRM/nvrm_off.h", "kexts/NVRM/nvrm_gpuva_abi.h",
         "kexts/NVRM/nvrm_utilization_abi.h", "kexts/NVRM/nvrm_surfshare_abi.h", "kexts/NVRM/nvrm_vram_abi.h",
         "LICENSE", "NOTICE", "licenses"]
TEXT = {".c", ".h", ".m", ".mm", ".cpp", ".hpp", ".inc", ".rs", ".toml", ".plist", ".py", ".sh", ".metal", ".json", ".md", ".txt", ".lock", ".yml", ".yaml"}
TEXT_NAMES = {".gitignore", "Makefile", "README"}

RENAMES = [("NVMTL", "PVGMTL"), ("nvmtl", "pvgmtl")]


def extract(src, rev, dest):
    data = subprocess.run(["git", "-C", str(src), "archive", "--format=tar", rev, *PATHS], check=True, capture_output=True).stdout
    with tarfile.open(fileobj=io.BytesIO(data)) as tar:
        tar.extractall(dest, filter="data")


def rename_tree(root):
    for path in sorted(root.rglob("*"), key=lambda p: -len(p.parts)):
        name = path.name
        for a, b in RENAMES:
            name = name.replace(a, b)
        if path.is_file() and (path.suffix in TEXT or path.name in TEXT_NAMES):
            text = path.read_text(encoding="utf-8", errors="surrogateescape")
            new = text
            for a, b in RENAMES:
                new = new.replace(a, b)
            if new != text:
                path.write_text(new, encoding="utf-8", errors="surrogateescape", newline="")
        if name != path.name:
            path.rename(path.with_name(name))


def edit(path, old, new):
    text = path.read_text(encoding="utf-8", errors="surrogateescape")
    if new in text:
        return
    if text.count(old) != 1:
        raise SystemExit(f"{path}: expected one occurrence of {old[:70]!r}; NullMoth changed, update the importer")
    path.write_text(text.replace(old, new), encoding="utf-8", errors="surrogateescape", newline="")


def patch_plugin(plugin):
    vk = plugin / "pvgmtl_vk.c"
    # 0x1af4 (virtio): the Hyper-V virtual GPU, served by Venus through PVGTransport.kext.
    edit(vk, '    if (accel && IOObjectConformsTo(accel, "NMXAccel")) {',
             '    if (accel && IOObjectConformsTo(accel, "PVGAccel")) v = 0x1af4;\n'
             '    else if (accel && IOObjectConformsTo(accel, "NMXAccel")) {')
    edit(vk, '    if (g_vendor) return g_vendor;\n    io_service_t s = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("NVAccel"));',
             '    if (g_vendor) return g_vendor;\n'
             '    io_service_t s = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("PVGAccel"));\n'
             '    if (s) { IOObjectRelease(s); g_vendor = 0x1af4; return g_vendor; }\n'
             '    s = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("NVAccel"));')
    edit(vk, '    case 0x1002: return "/Library/GPUBundles/pvgmtl/radeon_icd.json";',
             '    case 0x1af4: return "/Library/GPUBundles/pvgmtl/virtio_icd.json";\n'
             '    case 0x1002: return "/Library/GPUBundles/pvgmtl/radeon_icd.json";')
    edit(vk, '    case 0x1002: return "/Library/GPUBundles/pvgmtl/libvulkan_radeon.dylib";',
             '    case 0x1af4: return "/Library/GPUBundles/pvgmtl/libvulkan_virtio.dylib";\n'
             '    case 0x1002: return "/Library/GPUBundles/pvgmtl/libvulkan_radeon.dylib";')
    # Venus shows every host GPU. Open the one pvg-host prefers (PVGTransport publishes its UUID)
    # instead of the first, so an Intel iGPU never wins over the discrete GPU by enumeration order.
    edit(vk, '    VkPhysicalDevice pds[8]; if (n > 8) n = 8; pvkEnumeratePhysicalDevices(g_inst, &n, pds); g_pdev = pds[0];\n',
             '    VkPhysicalDevice pds[8]; if (n > 8) n = 8; pvkEnumeratePhysicalDevices(g_inst, &n, pds); g_pdev = pds[0];\n'
             '    if (pvgmtl_vk_vendor() == 0x1af4 && n > 1) {\n'
             '        io_service_t t = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("PVGTransport"));\n'
             '        CFDataRef want = t ? (CFDataRef)IORegistryEntryCreateCFProperty(t, CFSTR("PVGPreferredDeviceUUID"), kCFAllocatorDefault, 0) : NULL;\n'
             '        if (t) IOObjectRelease(t);\n'
             '        if (want && CFGetTypeID(want) == CFDataGetTypeID() && CFDataGetLength(want) == VK_UUID_SIZE) {\n'
             '            for (uint32_t i = 0; i < n; i++) {\n'
             '                VkPhysicalDeviceIDProperties idp = { VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES };\n'
             '                VkPhysicalDeviceProperties2 p2 = { VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &idp };\n'
             '                pvkGetPhysicalDeviceProperties2(pds[i], &p2);\n'
             '                if (!memcmp(idp.deviceUUID, CFDataGetBytePtr(want), VK_UUID_SIZE)) { g_pdev = pds[i]; nvlog("vk: host GPU %u is pvg-host\'s preferred adapter", i); break; }\n'
             '            }\n'
             '        }\n'
             '        if (want) CFRelease(want);\n'
             '    }\n')
    dev = plugin / "PVGMTLDevice.m"
    edit(dev, 'live == 0x1002 ? "AMD, RADV" : live == 0x8086 ? "Intel, ANV" : "NVIDIA, NVK"',
              'live == 0x1af4 ? "Hyper-V virtual GPU, Venus" : live == 0x1002 ? "AMD, RADV" : live == 0x8086 ? "Intel, ANV" : "NVIDIA, NVK"')
    edit(dev, 'NSString *v = pvgmtl_vk_vendor() == 0x1002 ? @"AMD"',
              'NSString *v = pvgmtl_vk_vendor() == 0x1af4 ? @"Virtual" : pvgmtl_vk_vendor() == 0x1002 ? @"AMD"')


def patch_accel(accel):
    src = accel / "nvrm-accel.cpp"
    edit(src, '#ifdef NMX_ACCEL\n// NMXAccel: this accelerator built for the AMD and Intel GPUs that NMXGPU drives (see nmx_accel_names.h)\n#include "nmx_accel_names.h"\n#endif',
              '#if defined(PVG_ACCEL)\n// PVGAccel: this accelerator built for the Hyper-V virtual GPU (pome PVGTransport + Venus); see pvg_accel_names.h\n'
              '#include "pvg_accel_names.h"\n#elif defined(NMX_ACCEL)\n'
              '// NMXAccel: this accelerator built for the AMD and Intel GPUs that NMXGPU drives (see nmx_accel_names.h)\n#include "nmx_accel_names.h"\n#endif')
    edit(src, '#ifdef NMX_ACCEL\n#define NV_LOG_TAG "NMXAccel: "', '#if defined(PVG_ACCEL)\n#define NV_LOG_TAG "PVGAccel: "\n#elif defined(NMX_ACCEL)\n#define NV_LOG_TAG "NMXAccel: "')
    edit(src, '#ifdef NMX_ACCEL\nextern "C" { KMOD_EXPLICIT_DECL(com.nullmoth.NMXAccel, "0.1", _start, _stop) }',
              '#if defined(PVG_ACCEL)\nextern "C" { KMOD_EXPLICIT_DECL(org.pome.PVGAccel, "0.1", _start, _stop) }\n'
              '#elif defined(NMX_ACCEL)\nextern "C" { KMOD_EXPLICIT_DECL(com.nullmoth.NMXAccel, "0.1", _start, _stop) }')
    edit(src, '#ifdef NMX_ACCEL\nSYSCTL_INT(_debug, OID_AUTO, nmxaccel_headless,',
              '#if defined(PVG_ACCEL)\nSYSCTL_INT(_debug, OID_AUTO, pvgaccel_headless, CTLFLAG_RD | CTLFLAG_LOCKED | CTLFLAG_KERN, &gNvHeadless, 0,\n'
              '           "1 = Hyper-V virtual GPU Metal device for apps; frames are copied to the synthetic framebuffer");\n'
              '#elif defined(NMX_ACCEL)\nSYSCTL_INT(_debug, OID_AUTO, nmxaccel_headless,')
    edit(src, '    sysctl_register_oid(&sysctl__debug_nmxaccel_headless);\n',
              '#if defined(PVG_ACCEL)\n    sysctl_register_oid(&sysctl__debug_pvgaccel_headless);\n#else\n'
              '    sysctl_register_oid(&sysctl__debug_nmxaccel_headless);\n#endif\n')
    # Probe: the provider is pome's transport, not a PCI GPU.
    edit(src, '        ALOG("probe(%s) score %d", provider ? provider->getName() : "?", score ? (int)*score : 0);',
              '        ALOG("probe(%s) score %d", provider ? provider->getName() : "?", score ? (int)*score : 0);\n'
              '#ifdef PVG_ACCEL\n'
              '        if (!provider || !provider->metaCast("PVGTransport")) return NULL;\n'
              '        return IOGraphicsAccelerator2::probe(provider, score);\n'
              '#endif')
    # Start only when the host really renders: otherwise macOS keeps its software path.
    edit(src, '        { if (nvrm_driver_off()) return false; }\n#ifdef NMX_ACCEL',
              '        { if (nvrm_driver_off()) return false; }\n'
              '#ifdef PVG_ACCEL\n'
              '        if (!provider || provider->getProperty("PVGVenusAvailable") != kOSBooleanTrue) {\n'
              '            ALOG("the host companion has no Venus renderer - not starting; macOS keeps software rendering");\n'
              '            return false;\n'
              '        }\n'
              '#endif\n#ifdef NMX_ACCEL')
    (accel / "pvg_accel_names.h").write_text(
        "/*\n * Pome Virtual GPU accelerator class names. Derived from NullMoth's nmx_accel_names.h.\n"
        " * Copyright (c) 2026 NullMoth Systems; changes by pome contributors.\n"
        " * SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0\n *\n"
        " * nvrm-accel.cpp built with -DNMX_ACCEL -DPVG_ACCEL: the headless app-only accelerator path of the\n"
        " * AMD/Intel build, on PVGTransport. IOKit class names are global, so every class is renamed.\n */\n"
        "#ifndef PVG_ACCEL_NAMES_H\n#define PVG_ACCEL_NAMES_H\n"
        + "".join(f"#define {a:<18} {b}\n" for a, b in [
            ("NVAccel", "PVGAccel"), ("NVEventMachine", "PVGEventMachine"), ("NVDisplayMachine", "PVGDisplayMachine"),
            ("NVTask", "PVGTask"), ("NVMemoryMap", "PVGMemoryMap"), ("IOAccelNVRMCarrier", "IOAccelPVGCarrier"),
            ("NVResource", "PVGResource"), ("NVSysMemory", "PVGSysMemory"), ("NVVidMemory", "PVGVidMemory"),
            ("NVSurface", "PVGSurface"), ("NVDisplayPipe", "PVGDisplayPipe"), ("NVGLContext", "PVGGLContext"),
            ("NV2DContext", "PVG2DContext"), ("NVCLContext", "PVGCLContext"), ("NVVideoContext", "PVGVideoContext")])
        + "#endif\n", encoding="utf-8", newline="\n")
    (accel / "Info.plist").write_text("""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleExecutable</key><string>PVGAccel</string>
	<key>CFBundleIdentifier</key><string>org.pome.PVGAccel</string>
	<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
	<key>CFBundleName</key><string>PVGAccel</string>
	<key>CFBundlePackageType</key><string>KEXT</string>
	<key>CFBundleShortVersionString</key><string>0.1.0</string>
	<key>CFBundleVersion</key><string>0.1.0</string>
	<key>IOKitPersonalities</key>
	<dict>
		<key>PVGAccel</key>
		<dict>
			<key>CFBundleIdentifier</key><string>org.pome.PVGAccel</string>
			<key>IOClass</key><string>PVGAccel</string>
			<key>IOProviderClass</key><string>PVGTransport</string>
			<key>IOMatchCategory</key><string>IOAccelerator</string>
			<key>IOProbeScore</key><integer>100</integer>
			<key>MetalPluginName</key><string>../../../Library/GPUBundles/PVGMTLDriver</string>
			<key>MetalPluginClassName</key><string>PVGMTLDevice</string>
		</dict>
	</dict>
	<key>OSBundleLibraries</key>
	<dict>
		<key>org.pome.PVGTransport</key><string>0.1.0</string>
		<key>com.apple.iokit.IOAcceleratorFamily2</key><string>2.0.0</string>
		<key>com.apple.iokit.IOGraphicsFamily</key><string>1.0.0b1</string>
		<key>com.apple.iokit.IOPCIFamily</key><string>1.0.0b1</string>
		<key>com.apple.iokit.IOSurface</key><string>1.0</string>
		<key>com.apple.kpi.iokit</key><string>8.0</string>
		<key>com.apple.kpi.libkern</key><string>8.0</string>
		<key>com.apple.kpi.mach</key><string>8.0</string>
		<key>com.apple.kpi.bsd</key><string>8.0</string>
	</dict>
</dict>
</plist>
""", encoding="utf-8", newline="\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("nullmoth")
    parser.add_argument("--rev", default=PINNED_REV)
    args = parser.parse_args()
    staging = HERE / ".import"
    if staging.exists():
        shutil.rmtree(staging)
    staging.mkdir()
    extract(pathlib.Path(args.nullmoth), args.rev, staging)

    for name in ["plugin", "translator", "accel", "licenses"]:
        if (HERE / name).exists():
            shutil.rmtree(HERE / name)
    shutil.move(str(staging / "plugin"), HERE / "plugin")
    shutil.move(str(staging / "translator"), HERE / "translator")
    shutil.move(str(staging / "kexts/NVRM/accel"), HERE / "accel")
    # accel/nvrm-accel.cpp includes "../nvrm_*.h": the ABI headers sit beside accel/.
    for h in (staging / "kexts/NVRM").glob("*.h"):
        shutil.move(str(h), HERE / h.name)
    shutil.move(str(staging / "licenses"), HERE / "licenses")
    shutil.copy2(staging / "LICENSE", HERE / "LICENSE")
    notice = (staging / "NOTICE").read_text(encoding="utf-8")
    shutil.rmtree(staging)

    # The plugin and the translator wrapper share the renamed prefix (dlsym names and dylib path).
    rename_tree(HERE / "plugin")
    # The whole translator: the plugin passes PVGMTL_* settings that the translator core reads.
    rename_tree(HERE / "translator")
    patch_plugin(HERE / "plugin")
    patch_accel(HERE / "accel")
    (HERE / "NOTICE").write_text(
        "Pome Virtual GPU Metal driver (gpu/guest/metal)\n\n"
        f"{REQUIRED_NOTICE}\n\n"
        "plugin/ and accel/ are NullMoth's Metal driver plugin and IOAccelerator, imported from\n"
        f"https://github.com/nullmoth/nvidia-macos-driver at {args.rev} and licensed under the\n"
        "PolyForm Noncommercial License 1.0.0 (LICENSE). They may be used, changed and redistributed for\n"
        "noncommercial purposes only. pome renamed NVMTL to PVGMTL and added the Hyper-V virtual GPU back end.\n\n"
        "translator/ is metal2vulkan, LGPL-3.0-or-later (translator/LICENSE, translator/COPYING).\n\n"
        "NullMoth's own third-party notice follows.\n\n" + notice, encoding="utf-8", newline="\n")
    print("imported NullMoth", args.rev[:7], "into", HERE)


if __name__ == "__main__":
    main()
