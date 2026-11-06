#!/usr/bin/env python3
"""Adds pome's Hyper-V renderer to a Mesa checkout so Venus runs in macOS guests.

Copyright 2026 pome contributors. SPDX-License-Identifier: MIT
Generated-by: LLM

Usage: apply_pvg.py <mesa-checkout>

Changes only src/virtio/vulkan (Venus). Per Mesa's AGENTS.md, nothing here is proposed upstream
and src/nouveau is never touched. The edits are idempotent and fail loudly if Mesa moved.
"""
import pathlib
import shutil
import sys

HERE = pathlib.Path(__file__).resolve().parent
GPU = HERE.parent.parent


def replace_once(path, old, new):
    text = path.read_text(encoding="utf-8")
    if new in text:
        return
    if text.count(old) != 1:
        raise SystemExit(f"{path}: expected exactly one occurrence of {old[:60]!r}; Mesa changed, update apply_pvg.py")
    path.write_text(text.replace(old, new), encoding="utf-8", newline="\n")


def main():
    mesa = pathlib.Path(sys.argv[1]).resolve()
    venus = mesa / "src/virtio/vulkan"
    if not (venus / "vn_renderer.h").exists():
        raise SystemExit(f"{mesa} is not a Mesa checkout with Venus")

    (venus / "pvg").mkdir(exist_ok=True)
    shutil.copy2(GPU / "protocol/pvg_wire.h", venus / "pvg/pvg_wire.h")
    shutil.copy2(GPU / "guest/kext/PVGTransport/pvg_uapi.h", venus / "pvg/pvg_uapi.h")
    shutil.copy2(HERE / "vn_renderer_pvg.c", venus / "vn_renderer_pvg.c")

    header = venus / "vn_renderer.h"
    replace_once(header,
        "static inline VkResult\nvn_renderer_create(struct vn_instance *instance,",
        "VkResult\nvn_renderer_create_pvg(struct vn_instance *instance,\n"
        "                       const VkAllocationCallbacks *alloc,\n"
        "                       struct vn_renderer **renderer);\n\n"
        "static inline VkResult\nvn_renderer_create(struct vn_instance *instance,")
    # macOS guests of Hyper-V: the kext transport first, vtest (developer socket) as fallback.
    replace_once(header,
        "#else\n   return vn_renderer_create_vtest(instance, alloc, renderer);\n#endif",
        "#elif defined(__APPLE__)\n"
        "   VkResult result = vn_renderer_create_pvg(instance, alloc, renderer);\n"
        "   if (result != VK_SUCCESS && VN_DEBUG(VTEST))\n"
        "      result = vn_renderer_create_vtest(instance, alloc, renderer);\n"
        "   return result;\n"
        "#else\n   return vn_renderer_create_vtest(instance, alloc, renderer);\n#endif")

    # vtest stays available on macOS as a developer fallback; Darwin has no SOCK_CLOEXEC.
    replace_once(venus / "vn_renderer_vtest.c",
        "#include <sys/socket.h>\n",
        "#include <sys/socket.h>\n#ifndef SOCK_CLOEXEC\n#define SOCK_CLOEXEC 0 /* pome: Darwin */\n#endif\n")

    build = venus / "meson.build"
    replace_once(build,
        "if not with_platform_windows\n  libvn_files += files('vn_renderer_vtest.c')",
        "if host_machine.system() == 'darwin'\n"
        "  libvn_files += files('vn_renderer_pvg.c')\n"
        "  vn_deps += dependency('appleframeworks', modules : ['IOKit', 'CoreFoundation'])\n"
        "endif\n\n"
        "if not with_platform_windows\n  libvn_files += files('vn_renderer_vtest.c')")
    print(f"pvg renderer added to {venus}")


if __name__ == "__main__":
    main()
