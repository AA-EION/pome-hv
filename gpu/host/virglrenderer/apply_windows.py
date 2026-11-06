#!/usr/bin/env python3
"""Ports a virglrenderer checkout to Windows (MinGW-w64 UCRT) for pvg-host: Venus only, in-process.

Copyright 2026 pome contributors. SPDX-License-Identifier: MIT
    apply_windows.py <virglrenderer-checkout>

pvg-host runs Venus (vkr) inside its own process, so the Linux render server (fork, signalfd,
memfd) and its proxy are not built. Remaining POSIX uses are covered by MinGW-w64 and the
pvg_win32_compat.h shim this script installs. Edits are idempotent and fail loudly if
virglrenderer moved.
"""
import pathlib
import shutil
import sys

HERE = pathlib.Path(__file__).resolve().parent


def replace_once(path, old, new):
    text = path.read_text(encoding="utf-8")
    if new in text:
        return
    if text.count(old) != 1:
        raise SystemExit(f"{path}: expected one occurrence of {old[:70]!r}; update apply_windows.py")
    path.write_text(text.replace(old, new), encoding="utf-8", newline="\n")


def main():
    root = pathlib.Path(sys.argv[1]).resolve()
    top = root / "meson.build"
    # No render server on Windows: vkr runs in pvg-host's process (VIRGL_RENDERER_RENDER_SERVER unset).
    replace_once(top, "with_render_server = ''\nif with_venus\n",
                      "with_render_server = ''\nif with_venus and host_machine.system() != 'windows'\n")
    replace_once(top, "if with_render_server == 'process'\nsubdir('server')\nendif",
                      "if with_render_server == 'process' and host_machine.system() != 'windows'\nsubdir('server')\nendif")
    shutil.copy2(HERE / "pvg_win32_compat.h", root / "src" / "pvg_win32_compat.h")
    shutil.copytree(HERE / "win32-include", root / "pvg-win32-include", dirs_exist_ok=True)
    # The library reaches Venus only through the render server's fd-passing proxy, so pvg-host calls
    # vkr's own API (as server/render_context.c does) and needs no config for the server worker.
    replace_once(top, "   conf_data.set('ENABLE_RENDER_SERVER', 1)\n",
                      "   if with_render_server != ''\n   conf_data.set('ENABLE_RENDER_SERVER', 1)\n   endif\n")
    replace_once(top, "   if with_render_server_worker == 'process'\n      conf_data.set('ENABLE_RENDER_SERVER_WORKER_PROCESS', 1)",
                      "   if with_render_server == ''\n      # pome: Windows, Venus in pvg-host's process\n"
                      "   elif with_render_server_worker == 'process'\n      conf_data.set('ENABLE_RENDER_SERVER_WORKER_PROCESS', 1)")

    # vkr_renderer.c is only referenced by the (absent) render server, so the static archive would
    # drop it; link libvirgl whole so pvg-host can reach vkr_renderer_* through the DLL.
    replace_once(root / "src" / "meson.build",
        "   dependencies : [virgl_depends, libvirgl_dep, drm_uapi_dep],\n   version :",
        "   dependencies : [virgl_depends, libvirgl_dep, drm_uapi_dep],\n"
        "   link_whole : host_machine.system() == 'windows' ? [libvirgl] : [],\n   version :")
    venus = root / "src" / "venus"
    ctx = venus / "vkr_context.c"
    # Shmems (Venus rings, replies): plain committed memory; vkr keeps the pointer, pvg-host mirrors it.
    replace_once(ctx,
        '   int fd = os_create_anonymous_file(alloc_size, "vkr-shmem");\n   if (fd < 0)\n      return false;\n\n'
        '   void *mmap_ptr = mmap(NULL, alloc_size, PROT_WRITE | PROT_READ, MAP_SHARED, fd, 0);\n'
        '   if (mmap_ptr == MAP_FAILED) {\n      close(fd);\n      return false;\n   }',
        '#ifdef _WIN32\n   int fd = -1;\n   void *mmap_ptr = VirtualAlloc(NULL, alloc_size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);\n'
        '   if (!mmap_ptr)\n      return false;\n#else\n'
        '   int fd = os_create_anonymous_file(alloc_size, "vkr-shmem");\n   if (fd < 0)\n      return false;\n\n'
        '   void *mmap_ptr = mmap(NULL, alloc_size, PROT_WRITE | PROT_READ, MAP_SHARED, fd, 0);\n'
        '   if (mmap_ptr == MAP_FAILED) {\n      close(fd);\n      return false;\n   }\n#endif')
    replace_once(ctx,
        '                                             VIRGL_RESOURCE_FD_SHM, -1, mmap_ptr)) {\n      munmap(mmap_ptr, alloc_size);\n      close(fd);\n',
        '                                             VIRGL_RESOURCE_FD_SHM, -1, mmap_ptr)) {\n#ifdef _WIN32\n'
        '      VirtualFree(mmap_ptr, 0, MEM_RELEASE);\n#else\n      munmap(mmap_ptr, alloc_size);\n      close(fd);\n#endif\n')

    mem = venus / "vkr_device_memory.c"
    # Device memory: no fd export on Windows. The resource is recorded as OPAQUE with no fd (so vkr's
    # resource table never munmaps or closes anything), and pvg-host maps a mappable one in-process
    # with vkr_renderer_map_resource (vkMapMemory).
    replace_once(mem,
        '   if (mem->exported) {\n      vkr_log("mem has been exported");\n      return false;\n   }\n',
        '   if (mem->exported) {\n      vkr_log("mem has been exported");\n      return false;\n   }\n'
        '#ifdef _WIN32\n'
        '   const bool win_visible = mem->property_flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT;\n'
        '   const bool win_coherent = mem->property_flags & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;\n'
        '   const bool win_cached = mem->property_flags & VK_MEMORY_PROPERTY_HOST_CACHED_BIT;\n'
        '   if ((blob_flags & VIRGL_RENDERER_BLOB_FLAG_USE_MAPPABLE) && !win_visible) {\n'
        '      vkr_log("mem cannot support mappable blob");\n'
        '      return false;\n'
        '   }\n'
        '   mem->exported = true;\n'
        '   *out_blob = (struct virgl_context_blob){\n'
        '      .type = VIRGL_RESOURCE_FD_OPAQUE,\n'
        '      .u.fd = -1,\n'
        '      .map_info = !(blob_flags & VIRGL_RENDERER_BLOB_FLAG_USE_MAPPABLE) ? VIRGL_RENDERER_MAP_CACHE_NONE\n'
        '                  : (win_coherent && win_cached) ? VIRGL_RENDERER_MAP_CACHE_CACHED : VIRGL_RENDERER_MAP_CACHE_WC,\n'
        '      .vulkan_info = {\n'
        '         .allocation_size = mem->allocation_size,\n'
        '         .memory_type_index = mem->memory_type_index,\n'
        '      },\n'
        '   };\n'
        '   (void)blob_size;\n'
        '   return true;\n'
        '#endif\n')
    # might_export memories keep a dup of the exported fd; there is none on Windows.
    replace_once(ctx, "   int res_fd = -1;\n   if (mem->might_export) {",
                      "   int res_fd = -1;\n   if (mem->might_export && blob.u.fd >= 0) {")

    ring = venus / "vkr_ring.c"
    # thrd_current() is not portable to Windows (Mesa util/u_thread.h says so); a thread-local flag
    # answers the only question the ring asks: "is this the ring thread?"
    replace_once(ring, "static int\nvkr_ring_thread(void *arg)\n{\n   struct vkr_ring *ring = arg;\n",
        "static _Thread_local bool vkr_ring_on_ring_thread;\n\nstatic int\nvkr_ring_thread(void *arg)\n{\n"
        "   struct vkr_ring *ring = arg;\n   vkr_ring_on_ring_thread = true;\n")
    replace_once(ring, "   if (thrd_equal(ring->thread, thrd_current())) {",
        "   if (vkr_ring_on_ring_thread) {")

    header = venus / "vkr_renderer.h"
    replace_once(header, "void\nvkr_renderer_destroy_resource(uint32_t ctx_id, uint32_t res_id);\n",
        "void\nvkr_renderer_destroy_resource(uint32_t ctx_id, uint32_t res_id);\n\n"
        "/* pome (Windows): in-process CPU mapping of a shmem or host-visible device-memory blob. */\n"
        "bool\nvkr_renderer_map_resource(uint32_t ctx_id, uint32_t res_id, uint64_t blob_id, void **out_ptr, uint64_t *out_size);\n")
    # pvg-host (MSVC) calls vkr directly through the DLL, so its API is exported like virgl_renderer_*.
    for decl in ["size_t\nvkr_get_capset(", "bool\nvkr_renderer_init(", "void\nvkr_renderer_fini(",
                 "bool\nvkr_renderer_create_context(", "void\nvkr_renderer_destroy_context(",
                 "bool\nvkr_renderer_submit_cmd(", "bool\nvkr_renderer_submit_fence(",
                 "bool\nvkr_renderer_create_resource(", "bool\nvkr_renderer_import_resource(",
                 "void\nvkr_renderer_destroy_resource(", "bool\nvkr_renderer_map_resource("]:
        replace_once(header, "\n" + decl, "\nVIRGL_EXPORT " + decl)
    renderer = venus / "vkr_renderer.c"
    text = renderer.read_text(encoding="utf-8")
    if "vkr_renderer_map_resource" not in text:
        text += """
/* pome (Windows): pvg-host mirrors blobs to the guest, so it needs a CPU pointer, not an fd. */
#include "vkr_device.h"
#include "vkr_device_memory.h"

bool
vkr_renderer_map_resource(uint32_t ctx_id, uint32_t res_id, uint64_t blob_id, void **out_ptr, uint64_t *out_size)
{
   struct vkr_context *ctx = vkr_renderer_lookup_context(ctx_id);
   if (!ctx)
      return false;
   struct vkr_resource *res = vkr_context_get_resource(ctx, res_id);
   if (!res)
      return false;
   if (res->fd_type == VIRGL_RESOURCE_FD_SHM) {
      /* shmem (rings, reply streams): the VirtualAlloc'd pointer vkr itself uses */
      *out_ptr = res->u.data;
      *out_size = res->size;
      return res->u.data != NULL;
   }
   /* device memory: exported once (vkr enforces it), so it is mapped once; vkFreeMemory unmaps */
   struct vkr_device_memory *mem = vkr_context_get_object(ctx, blob_id);
   if (!mem || mem->base.type != VK_OBJECT_TYPE_DEVICE_MEMORY ||
       !(mem->property_flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT))
      return false;
   struct vn_device_proc_table *vk = &mem->device->proc_table;
   void *ptr = NULL;
   if (vk->MapMemory(mem->device->base.handle.device, mem->base.handle.device_memory, 0, VK_WHOLE_SIZE, 0, &ptr) != VK_SUCCESS)
      return false;
   *out_ptr = ptr;
   *out_size = mem->allocation_size;
   return true;
}
"""
        renderer.write_text(text, encoding="utf-8", newline="\n")
    print(f"virglrenderer at {root} prepared for a Windows Venus-only build")


if __name__ == "__main__":
    main()
