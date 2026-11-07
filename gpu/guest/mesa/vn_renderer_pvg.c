/*
 * Venus renderer for macOS guests of Hyper-V: talks to PVGTransport.kext, which forwards the
 * Venus wire format to pvg-host on Windows (virglrenderer vkr on the host's Vulkan driver).
 *
 * Copyright 2026 pome contributors. SPDX-License-Identifier: MIT
 * Generated-by: LLM
 *
 * Differences from vn_renderer_vtest.c, whose structure this follows:
 *  - transport is an IOKit user client, not an AF_UNIX socket with fd passing;
 *  - shmems and HOST_COHERENT memory are mirrored: the kext keeps an agreed copy and
 *    PVG_UC_FLUSH_MIRRORS sends only guest-written bytes before each submit, while the host
 *    pushes the bytes it writes (ring head/status, replies, watched ranges);
 *  - timeline syncs live in the kext and advance when the host signals the batch's ring fence.
 */
#include <IOKit/IOKitLib.h>
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <stdlib.h>
#include <string.h>

#include "virtio/virtio-gpu/venus_hw.h"
#include "drm-uapi/virtgpu_drm.h"
#include "util/sparse_array.h"
#include "util/u_process.h"

#include "vn_renderer_internal.h"
#include "pvg/pvg_wire.h"
#include "pvg/pvg_uapi.h"

#define PVG_WATCH_CHUNK (1u << 20)

struct pvg_shmem {
   struct vn_renderer_shmem base;
};

struct pvg_bo {
   struct vn_renderer_bo base;
   uint32_t blob_flags;
   VkMemoryPropertyFlags flags;
};

struct pvg_sync {
   struct vn_renderer_sync base;
};

struct pvg {
   struct vn_renderer base;
   struct vn_instance *instance;
   io_connect_t conn;
   mtx_t mutex;
   uint32_t context_id;
   uint32_t host_flags;
   struct virgl_renderer_capset_venus capset;
   struct util_sparse_array shmem_array;
   struct util_sparse_array bo_array;
   struct vn_renderer_shmem_cache shmem_cache;
};

static kern_return_t
pvg_call(struct pvg *pvg, uint32_t selector, const uint64_t *in, uint32_t in_count, const void *in_struct,
         size_t in_size, uint64_t *out, uint32_t out_count, void *out_struct, size_t *out_size)
{
   uint32_t oc = out_count;
   return IOConnectCallMethod(pvg->conn, selector, in, in_count, in_struct, in_size, out, out ? &oc : NULL,
                              out_struct, out_size);
}

static void *
pvg_map(struct pvg *pvg, uint32_t res_id, size_t size, void *placed_addr)
{
   mach_vm_address_t addr = (mach_vm_address_t)(uintptr_t)placed_addr;
   mach_vm_size_t mapped = 0;
   /* VK_EXT_map_memory_placed: the app reserved this range. IOKit maps at a fixed address only
    * into free space (no kIOMapAnywhere), so release the reservation first, as MAP_FIXED would. */
   if (placed_addr)
      mach_vm_deallocate(mach_task_self(), addr, size);
   const IOOptionBits options = placed_addr ? 0 : kIOMapAnywhere;
   if (IOConnectMapMemory64(pvg->conn, res_id, mach_task_self(), &addr, &mapped, options) != KERN_SUCCESS ||
       mapped < size) {
      vn_log(pvg->instance, "pvg: mapping resource %u failed", res_id);
      return NULL;
   }
   return (void *)(uintptr_t)addr;
}

static void
pvg_unmap(struct pvg *pvg, uint32_t res_id, void *ptr)
{
   if (ptr)
      IOConnectUnmapMemory64(pvg->conn, res_id, mach_task_self(), (mach_vm_address_t)(uintptr_t)ptr);
}

static uint32_t
pvg_create_blob(struct pvg *pvg, uint32_t blob_mem, uint32_t blob_flags, uint64_t blob_id, uint64_t size,
                bool mirror)
{
   pvg_uc_blob in = {
      .context_id = pvg->context_id,
      .blob_mem = blob_mem,
      .blob_flags = blob_flags,
      .mirror = mirror,
      .blob_id = blob_id,
      .size = size,
   };
   pvg_uc_blob_reply reply = { 0 };
   size_t reply_size = sizeof(reply);
   if (pvg_call(pvg, PVG_UC_RESOURCE_BLOB, NULL, 0, &in, sizeof(in), NULL, 0, &reply, &reply_size) != KERN_SUCCESS)
      return 0;
   return reply.resource_id;
}

static void
pvg_unref(struct pvg *pvg, uint32_t res_id)
{
   const uint64_t in = res_id;
   pvg_call(pvg, PVG_UC_RESOURCE_UNREF, &in, 1, NULL, 0, NULL, 0, NULL, NULL);
}

static void
pvg_call_range(struct pvg *pvg, uint32_t selector, uint32_t res_id, uint64_t offset, uint64_t size)
{
   const uint64_t in[3] = { res_id, offset, size };
   pvg_call(pvg, selector, in, 3, NULL, 0, NULL, 0, NULL, NULL);
}

/* ---------------------------------------------------------------- syncs */

static VkResult
pvg_sync_create(struct vn_renderer *renderer, uint64_t initial_val, struct vn_renderer_sync **out_sync)
{
   struct pvg *pvg = (struct pvg *)renderer;
   struct pvg_sync *sync = calloc(1, sizeof(*sync));
   if (!sync)
      return VK_ERROR_OUT_OF_HOST_MEMORY;
   uint64_t id = 0;
   if (pvg_call(pvg, PVG_UC_SYNC_CREATE, &initial_val, 1, NULL, 0, &id, 1, NULL, NULL) != KERN_SUCCESS) {
      free(sync);
      return VK_ERROR_OUT_OF_DEVICE_MEMORY;
   }
   sync->base.sync_id = (uint32_t)id;
   *out_sync = &sync->base;
   return VK_SUCCESS;
}

static void
pvg_sync_destroy(struct vn_renderer *renderer, struct vn_renderer_sync *sync)
{
   const uint64_t in = sync->sync_id;
   pvg_call((struct pvg *)renderer, PVG_UC_SYNC_DESTROY, &in, 1, NULL, 0, NULL, 0, NULL, NULL);
   free(sync);
}

static VkResult
pvg_sync_write(struct vn_renderer *renderer, struct vn_renderer_sync *sync, uint64_t val)
{
   const uint64_t in[2] = { sync->sync_id, val };
   return pvg_call((struct pvg *)renderer, PVG_UC_SYNC_WRITE, in, 2, NULL, 0, NULL, 0, NULL, NULL) == KERN_SUCCESS
             ? VK_SUCCESS : VK_ERROR_DEVICE_LOST;
}

static VkResult
pvg_sync_reset(struct vn_renderer *renderer, struct vn_renderer_sync *sync)
{
   return pvg_sync_write(renderer, sync, 0);
}

static VkResult
pvg_sync_read(struct vn_renderer *renderer, struct vn_renderer_sync *sync, uint64_t *val)
{
   const uint64_t in = sync->sync_id;
   return pvg_call((struct pvg *)renderer, PVG_UC_SYNC_READ, &in, 1, NULL, 0, val, 1, NULL, NULL) == KERN_SUCCESS
             ? VK_SUCCESS : VK_ERROR_DEVICE_LOST;
}

/* ---------------------------------------------------------------- bos */

static uint32_t
pvg_bo_blob_flags(VkMemoryPropertyFlags flags, VkExternalMemoryHandleTypeFlags external_handles)
{
   uint32_t blob_flags = 0;
   if (flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT)
      blob_flags |= VIRTGPU_BLOB_FLAG_USE_MAPPABLE;
   if (external_handles)
      blob_flags |= VIRTGPU_BLOB_FLAG_USE_SHAREABLE;
   return blob_flags;
}

static VkResult
pvg_submit(struct vn_renderer *renderer, const struct vn_renderer_submit_batch *batch);

static VkResult
pvg_bo_create_from_device_memory(struct vn_renderer *renderer, struct vn_renderer_submit_batch *batch,
                                 VkDeviceSize size, vn_object_id mem_id, VkMemoryPropertyFlags flags,
                                 VkExternalMemoryHandleTypeFlags external_handles, struct vn_renderer_bo **out_bo)
{
   struct pvg *pvg = (struct pvg *)renderer;
   if (batch) {
      VkResult r = pvg_submit(renderer, batch);
      if (r != VK_SUCCESS)
         return r;
   }
   const uint32_t blob_flags = pvg_bo_blob_flags(flags, external_handles);
   /* Coherent memory gets the kernel shadow: nothing will call flush for it. */
   const bool mirror = (flags & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT) && (blob_flags & VIRTGPU_BLOB_FLAG_USE_MAPPABLE);
   mtx_lock(&pvg->mutex);
   const uint32_t res_id = pvg_create_blob(pvg, VIRTGPU_BLOB_MEM_HOST3D, blob_flags, mem_id, size, mirror);
   mtx_unlock(&pvg->mutex);
   if (!res_id)
      return VK_ERROR_OUT_OF_DEVICE_MEMORY;

   struct pvg_bo *bo = util_sparse_array_get(&pvg->bo_array, res_id);
   *bo = (struct pvg_bo){
      .base = {
         .refcount = VN_REFCOUNT_INIT(1),
         .res_id = res_id,
         .mmap_size = size,
      },
      .blob_flags = blob_flags,
      .flags = flags,
   };
   *out_bo = &bo->base;
   return VK_SUCCESS;
}

static bool
pvg_bo_destroy(struct vn_renderer *renderer, struct vn_renderer_bo *_bo)
{
   struct pvg *pvg = (struct pvg *)renderer;
   pvg_unmap(pvg, _bo->res_id, _bo->mmap_ptr);
   pvg_unref(pvg, _bo->res_id);
   return true;
}

static void *
pvg_bo_map(struct vn_renderer *renderer, struct vn_renderer_bo *_bo, void *placed_addr)
{
   struct pvg *pvg = (struct pvg *)renderer;
   struct pvg_bo *bo = (struct pvg_bo *)_bo;
   if (!bo->base.mmap_ptr && (bo->blob_flags & VIRTGPU_BLOB_FLAG_USE_MAPPABLE)) {
      bo->base.mmap_ptr = pvg_map(pvg, bo->base.res_id, bo->base.mmap_size, placed_addr);
      /* The guest copy starts with what the host holds (uploads may follow, readbacks need it). */
      if (bo->base.mmap_ptr)
         pvg_call_range(pvg, PVG_UC_READ_RANGE, bo->base.res_id, 0, bo->base.mmap_size);
   }
   return bo->base.mmap_ptr;
}

static void
pvg_bo_flush(struct vn_renderer *renderer, struct vn_renderer_bo *bo, VkDeviceSize offset, VkDeviceSize size)
{
   if (size == VK_WHOLE_SIZE || offset + size > bo->mmap_size)
      size = bo->mmap_size - offset;
   pvg_call_range((struct pvg *)renderer, PVG_UC_WRITE_RANGE, bo->res_id, offset, size);
}

static void
pvg_bo_invalidate(struct vn_renderer *renderer, struct vn_renderer_bo *bo, VkDeviceSize offset, VkDeviceSize size)
{
   if (size == VK_WHOLE_SIZE || offset + size > bo->mmap_size)
      size = bo->mmap_size - offset;
   pvg_call_range((struct pvg *)renderer, PVG_UC_READ_RANGE, bo->res_id, offset, size);
}

static int
pvg_bo_export_dma_buf(struct vn_renderer *renderer, struct vn_renderer_bo *bo)
{
   return -1; /* no dma-buf on macOS; IOSurface sharing goes through the Metal layer */
}

/* ---------------------------------------------------------------- shmems */

static void
pvg_shmem_destroy_now(struct vn_renderer *renderer, struct vn_renderer_shmem *shmem)
{
   struct pvg *pvg = (struct pvg *)renderer;
   pvg_unmap(pvg, shmem->res_id, shmem->mmap_ptr);
   pvg_unref(pvg, shmem->res_id);
}

static void
pvg_shmem_destroy(struct vn_renderer *renderer, struct vn_renderer_shmem *shmem)
{
   struct pvg *pvg = (struct pvg *)renderer;
   if (vn_renderer_shmem_cache_add(&pvg->shmem_cache, shmem))
      return;
   pvg_shmem_destroy_now(renderer, shmem);
}

static struct vn_renderer_shmem *
pvg_shmem_create(struct vn_renderer *renderer, size_t size)
{
   struct pvg *pvg = (struct pvg *)renderer;
   struct vn_renderer_shmem *cached = vn_renderer_shmem_cache_get(&pvg->shmem_cache, size);
   if (cached) {
      cached->refcount = VN_REFCOUNT_INIT(1);
      return cached;
   }
   mtx_lock(&pvg->mutex);
   const uint32_t res_id = pvg_create_blob(pvg, VIRTGPU_BLOB_MEM_HOST3D, VIRTGPU_BLOB_FLAG_USE_MAPPABLE, 0, size, true);
   mtx_unlock(&pvg->mutex);
   if (!res_id)
      return NULL;
   void *ptr = pvg_map(pvg, res_id, size, NULL);
   if (!ptr) {
      pvg_unref(pvg, res_id);
      return NULL;
   }
   /* Rings, replies and feedback slots are written by the host: have it push them. */
   for (size_t off = 0; off < size; off += PVG_WATCH_CHUNK)
      pvg_call_range(pvg, PVG_UC_WATCH_RANGE, res_id, off, MIN2(PVG_WATCH_CHUNK, size - off));

   struct pvg_shmem *shmem = util_sparse_array_get(&pvg->shmem_array, res_id);
   *shmem = (struct pvg_shmem){
      .base = {
         .refcount = VN_REFCOUNT_INIT(1),
         .res_id = res_id,
         .mmap_size = size,
         .mmap_ptr = ptr,
      },
   };
   return &shmem->base;
}

/* ---------------------------------------------------------------- submit / wait */

static VkResult
pvg_submit(struct vn_renderer *renderer, const struct vn_renderer_submit_batch *batch)
{
   struct pvg *pvg = (struct pvg *)renderer;
   if (batch->sync_count > PVG_UC_MAX_SYNCS || batch->cs_size % 4)
      return VK_ERROR_DEVICE_LOST;
   const size_t total = sizeof(pvg_uc_exec) + batch->cs_size;
   pvg_uc_exec *exec = malloc(total);
   if (!exec)
      return VK_ERROR_OUT_OF_HOST_MEMORY;
   memset(exec, 0, sizeof(*exec));
   exec->context_id = pvg->context_id;
   exec->ring_idx = batch->ring_idx;
   exec->sync_count = batch->sync_count;
   exec->command_size = (uint32_t)batch->cs_size;
   for (uint32_t i = 0; i < batch->sync_count; i++) {
      exec->syncs[i].sync_id = batch->syncs[i]->sync_id;
      exec->syncs[i].value = batch->sync_values[i];
   }
   if (batch->cs_size)
      memcpy(exec + 1, batch->cs_data, batch->cs_size);

   mtx_lock(&pvg->mutex);
   /* The ring and coherent memory must reach the host before the commands that read them. */
   kern_return_t kr = pvg_call(pvg, PVG_UC_FLUSH_MIRRORS, NULL, 0, NULL, 0, NULL, 0, NULL, NULL);
   uint64_t fence = 0;
   if (kr == KERN_SUCCESS)
      kr = pvg_call(pvg, PVG_UC_EXECBUFFER, NULL, 0, exec, total, &fence, 1, NULL, NULL);
   mtx_unlock(&pvg->mutex);
   free(exec);
   return kr == KERN_SUCCESS ? VK_SUCCESS : VK_ERROR_DEVICE_LOST;
}

static VkResult
pvg_wait(struct vn_renderer *renderer, const struct vn_renderer_wait *wait)
{
   struct pvg *pvg = (struct pvg *)renderer;
   if (wait->sync_count > PVG_UC_MAX_SYNCS)
      return VK_ERROR_DEVICE_LOST;
   pvg_uc_wait in = {
      .wait_any = wait->wait_any,
      .sync_count = wait->sync_count,
      .timeout_ns = wait->timeout,
   };
   for (uint32_t i = 0; i < wait->sync_count; i++) {
      in.syncs[i].sync_id = wait->syncs[i]->sync_id;
      in.syncs[i].value = wait->sync_values[i];
   }
   /* Ring notifications may be pending in mirrored memory; send them so the host can progress. */
   pvg_call(pvg, PVG_UC_FLUSH_MIRRORS, NULL, 0, NULL, 0, NULL, 0, NULL, NULL);
   const kern_return_t kr = pvg_call(pvg, PVG_UC_WAIT, NULL, 0, &in, sizeof(in), NULL, 0, NULL, NULL);
   if (kr == KERN_SUCCESS)
      return VK_SUCCESS;
   return kr == kIOReturnTimeout ? VK_TIMEOUT : VK_ERROR_DEVICE_LOST;
}

/* ---------------------------------------------------------------- init */

static void
pvg_destroy(struct vn_renderer *renderer, const VkAllocationCallbacks *alloc)
{
   struct pvg *pvg = (struct pvg *)renderer;
   vn_renderer_shmem_cache_fini(&pvg->shmem_cache);
   if (pvg->context_id) {
      const uint64_t in = pvg->context_id;
      pvg_call(pvg, PVG_UC_CONTEXT_DESTROY, &in, 1, NULL, 0, NULL, 0, NULL, NULL);
   }
   if (pvg->conn)
      IOServiceClose(pvg->conn);
   mtx_destroy(&pvg->mutex);
   util_sparse_array_finish(&pvg->shmem_array);
   util_sparse_array_finish(&pvg->bo_array);
   vk_free(alloc, pvg);
}

static VkResult
pvg_open(struct pvg *pvg)
{
   io_service_t service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching(PVG_USERCLIENT_CLASS));
   if (!service)
      return VK_ERROR_INITIALIZATION_FAILED;
   const kern_return_t kr = IOServiceOpen(service, mach_task_self(), 0, &pvg->conn);
   IOObjectRelease(service);
   return kr == KERN_SUCCESS ? VK_SUCCESS : VK_ERROR_INITIALIZATION_FAILED;
}

static VkResult
pvg_init_capset(struct pvg *pvg)
{
   size_t size = 64 * 1024;
   uint8_t *caps = malloc(size);
   if (!caps)
      return VK_ERROR_OUT_OF_HOST_MEMORY;
   VkResult result = VK_ERROR_INITIALIZATION_FAILED;
   if (pvg_call(pvg, PVG_UC_GET_CAPS, NULL, 0, NULL, 0, NULL, 0, caps, &size) == KERN_SUCCESS &&
       size >= sizeof(pvg_caps_header)) {
      pvg_caps_header h;
      memcpy(&h, caps, sizeof(h));
      const size_t capset_offset = sizeof(h) + (size_t)h.adapter_count * sizeof(pvg_adapter_caps);
      pvg->host_flags = h.host_flags;
      if (!(h.host_flags & PVG_HOST_VENUS_AVAILABLE)) {
         vn_log(pvg->instance, "pvg: host '%.64s' has no Venus renderer", h.host_renderer);
      } else if (capset_offset + h.venus_capset_size <= size && h.venus_capset_size) {
         memcpy(&pvg->capset, caps + capset_offset, MIN2(sizeof(pvg->capset), h.venus_capset_size));
         result = VK_SUCCESS;
      }
   }
   free(caps);
   return result;
}

static void
pvg_init_renderer_info(struct pvg *pvg)
{
   struct vn_renderer_info *info = &pvg->base.info;
   /* Identify as virtio-gpu; Venus reports the host GPU's own properties to applications. */
   info->pci.vendor_id = 0x1af4;
   info->pci.device_id = 0x1050;
   info->has_dma_buf_import = false;
   info->has_external_sync = false;
   info->has_timeline_sync = true;
   info->has_implicit_fencing = false;
   info->has_guest_vram = false;
   info->max_timeline_count = 64;
   const struct virgl_renderer_capset_venus *capset = &pvg->capset;
   info->wire_format_version = capset->wire_format_version;
   info->vk_xml_version = capset->vk_xml_version;
   info->vk_ext_command_serialization_spec_version = capset->vk_ext_command_serialization_spec_version;
   info->vk_mesa_venus_protocol_spec_version = capset->vk_mesa_venus_protocol_spec_version;
   STATIC_ASSERT(sizeof(info->vk_extension_mask) >= sizeof(capset->vk_extension_mask1));
   memcpy(info->vk_extension_mask, capset->vk_extension_mask1, sizeof(capset->vk_extension_mask1));
}

static VkResult
pvg_init(struct pvg *pvg)
{
   util_sparse_array_init(&pvg->shmem_array, sizeof(struct pvg_shmem), 1024);
   util_sparse_array_init(&pvg->bo_array, sizeof(struct pvg_bo), 1024);
   mtx_init(&pvg->mutex, mtx_plain);

   VkResult result = pvg_open(pvg);
   if (result == VK_SUCCESS)
      result = pvg_init_capset(pvg);
   if (result != VK_SUCCESS)
      return result;

   const char *name = util_get_process_name();
   pvg_uc_context_init ci = { .capset_id = VIRTGPU_DRM_CAPSET_VENUS };
   snprintf(ci.name, sizeof(ci.name), "%s", name ? name : "venus");
   uint64_t ctx = 0;
   if (pvg_call(pvg, PVG_UC_CONTEXT_INIT, NULL, 0, &ci, sizeof(ci), &ctx, 1, NULL, NULL) != KERN_SUCCESS || !ctx)
      return VK_ERROR_INITIALIZATION_FAILED;
   pvg->context_id = (uint32_t)ctx;

   vn_renderer_shmem_cache_init(&pvg->shmem_cache, &pvg->base, pvg_shmem_destroy_now);
   pvg_init_renderer_info(pvg);

   pvg->base.ops.destroy = pvg_destroy;
   pvg->base.ops.submit = pvg_submit;
   pvg->base.ops.wait = pvg_wait;

   pvg->base.shmem_ops.create = pvg_shmem_create;
   pvg->base.shmem_ops.destroy = pvg_shmem_destroy;

   pvg->base.bo_ops.create_from_device_memory = pvg_bo_create_from_device_memory;
   pvg->base.bo_ops.create_from_dma_buf = NULL;
   pvg->base.bo_ops.destroy = pvg_bo_destroy;
   pvg->base.bo_ops.export_dma_buf = pvg_bo_export_dma_buf;
   pvg->base.bo_ops.export_sync_file = NULL;
   pvg->base.bo_ops.map = pvg_bo_map;
   pvg->base.bo_ops.flush = pvg_bo_flush;
   pvg->base.bo_ops.invalidate = pvg_bo_invalidate;

   pvg->base.sync_ops.create = pvg_sync_create;
   pvg->base.sync_ops.create_from_syncobj = NULL;
   pvg->base.sync_ops.destroy = pvg_sync_destroy;
   pvg->base.sync_ops.export_syncobj = NULL;
   pvg->base.sync_ops.reset = pvg_sync_reset;
   pvg->base.sync_ops.read = pvg_sync_read;
   pvg->base.sync_ops.write = pvg_sync_write;
   return VK_SUCCESS;
}

VkResult
vn_renderer_create_pvg(struct vn_instance *instance, const VkAllocationCallbacks *alloc,
                       struct vn_renderer **renderer)
{
   struct pvg *pvg = vk_zalloc(alloc, sizeof(*pvg), VN_DEFAULT_ALIGN, VK_SYSTEM_ALLOCATION_SCOPE_INSTANCE);
   if (!pvg)
      return VK_ERROR_OUT_OF_HOST_MEMORY;
   pvg->instance = instance;
   VkResult result = pvg_init(pvg);
   if (result != VK_SUCCESS) {
      pvg_destroy(&pvg->base, alloc);
      return result;
   }
   *renderer = &pvg->base;
   if (VN_DEBUG(INIT))
      vn_log(instance, "pvg backend initialized (Hyper-V)");
   return VK_SUCCESS;
}

/* ---------------------------------------------------------------- ring hooks
 *
 * Called from vn_ring.c and vn_relax (patched by apply_pvg.py). Venus expects the ring to live in
 * memory both sides see; here each side holds a copy, so guest writes travel when flushed and the
 * host pushes its own. Both are no-ops for other renderers (VN_DEBUG=vtest). */

void
vn_renderer_pvg_kick(struct vn_renderer *renderer)
{
   if (!renderer || renderer->ops.submit != pvg_submit)
      return;
   struct pvg *pvg = (struct pvg *)renderer;
   mtx_lock(&pvg->mutex);
   pvg_call(pvg, PVG_UC_FLUSH_MIRRORS, NULL, 0, NULL, 0, NULL, 0, NULL, NULL);
   mtx_unlock(&pvg->mutex);
}

void
vn_renderer_pvg_ring_created(struct vn_renderer *renderer, struct vn_renderer_shmem *shmem, size_t head_offset,
                             size_t tail_offset, size_t status_offset)
{
   if (!renderer || renderer->ops.submit != pvg_submit || !shmem)
      return;
   struct pvg *pvg = (struct pvg *)renderer;
   const size_t offsets[] = { head_offset, tail_offset, status_offset };
   for (uint32_t i = 0; i < ARRAY_SIZE(offsets); i++) {
      const uint64_t in[3] = { shmem->res_id, offsets[i], sizeof(uint32_t) };
      if (pvg_call(pvg, PVG_UC_MARK_CONTROL, in, 3, NULL, 0, NULL, 0, NULL, NULL) != KERN_SUCCESS)
         vn_log(pvg->instance, "pvg: marking ring word at %zu of resource %u failed", offsets[i], shmem->res_id);
   }
}
