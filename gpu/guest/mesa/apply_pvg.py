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
        "/* pome: PVG mirrors shmems instead of sharing them (no-ops for other renderers). */\n"
        "void\nvn_renderer_pvg_kick(struct vn_renderer *renderer);\n\n"
        "void\nvn_renderer_pvg_ring_created(struct vn_renderer *renderer,\n"
        "                             struct vn_renderer_shmem *shmem,\n"
        "                             size_t head_offset,\n"
        "                             size_t tail_offset,\n"
        "                             size_t status_offset);\n\n"
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

    # PVG has no memory shared with the host: the kext mirrors shmems and sends guest writes when
    # flushed. Venus assumes a shared ring, so (1) the ring's head/tail/status are marked as control
    # words, which both sides read first and send last (a tail never overtakes its commands, a head
    # never overtakes its replies), (2) a new tail is flushed at once, and (3) every sleeping ring wait
    # flushes, which also carries the ALIVE resets the watchdog depends on. Without (3) the host never
    # sees the reset, the watchdog expires and vn_relax aborts the process (loginwindow included).
    ring = venus / "vn_ring.c"
    replace_once(ring,
        "   ring->shared.extra = (char *)shared + layout->extra_offset;\n",
        "   ring->shared.extra = (char *)shared + layout->extra_offset;\n"
        "#ifdef __APPLE__\n"
        "   vn_renderer_pvg_ring_created(instance->renderer, ring->shmem, layout->head_offset,\n"
        "                                layout->tail_offset, layout->status_offset); /* pome */\n"
        "#endif\n")
    replace_once(ring,
        "   vn_ring_store_tail(ring);\n   const VkRingStatusFlagsMESA status = vn_ring_load_status(ring);\n",
        "   vn_ring_store_tail(ring);\n"
        "#ifdef __APPLE__\n"
        "   vn_renderer_pvg_kick(ring->instance->renderer); /* pome: send the commands, then the tail */\n"
        "#endif\n"
        "   const VkRingStatusFlagsMESA status = vn_ring_load_status(ring);\n")
    replace_once(venus / "vn_common.c",
        "   state->warn = false;\n",
        "   state->warn = false;\n"
        "#ifdef __APPLE__\n"
        "   if (state->instance)\n"
        "      vn_renderer_pvg_kick(state->instance->renderer); /* pome: tails and ALIVE resets */\n"
        "#endif\n")

    build = venus / "meson.build"
    # ld64 cannot leave Mesa's optional (weak, hidden) entrypoints such as vk_cmd_enqueue_* undefined;
    # KosmicKrisp (Mesa's own macOS driver) links with dynamic_lookup for the same reason.
    # dynamic_lookup also lets real missing symbols through to load time: vk_util.c's
    # vk_spec_info_to_nir_spirv calls the SPIR-V compiler (vtn_*), which Venus does not link.
    # -dead_strip drops such unused code, as --gc-sections (GNU only, so empty here) does on Linux.
    replace_once(build,
        "vn_link_args = [\n  ld_args_bsymbolic,\n  ld_args_gc_sections,\n  vulkan_icd_link_args,\n]\n",
        "vn_link_args = [\n  ld_args_bsymbolic,\n  ld_args_gc_sections,\n  vulkan_icd_link_args,\n]\n"
        "if host_machine.system() == 'darwin'\n  vn_link_args += ['-Wl,-undefined,dynamic_lookup', '-Wl,-dead_strip']\nendif\n")
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
