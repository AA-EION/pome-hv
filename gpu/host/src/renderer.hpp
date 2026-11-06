// Pome Virtual GPU host: renderer back ends. SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace pvg {

// Values from the virtio-gpu DRM uAPI (include/uapi/drm/virtgpu_drm.h, MIT). The guest render node
// speaks this API, so the host keeps the same meaning.
constexpr uint32_t kBlobMemGuest = 1, kBlobMemHost3D = 2, kBlobMemHost3DGuest = 3;
constexpr uint32_t kBlobFlagMappable = 1, kBlobFlagShareable = 2, kBlobFlagCrossDevice = 4;
constexpr uint32_t kCapsetVenus = 4;

struct BlobMapping {
    void* host_ptr = nullptr;   // host copy of the resource; mirrored to the guest copy
    uint64_t size = 0;
    uint32_t map_info = 0;      // VIRGL_RENDERER_MAP_CACHE_* as the renderer reports it
};

class Renderer {
public:
    using FenceCallback = std::function<void(uint32_t context_id, uint32_t ring_idx, uint64_t fence_id)>;
    virtual ~Renderer() = default;
    virtual const char* Name() const = 0;
    virtual std::vector<uint8_t> VenusCapset() = 0;
    virtual int CreateContext(uint32_t context_id, uint32_t capset_id, const std::string& name) = 0;
    virtual void DestroyContext(uint32_t context_id) = 0;
    virtual int CreateBlob(uint32_t context_id, uint32_t resource_id, uint32_t blob_mem, uint32_t blob_flags,
                           uint64_t blob_id, uint64_t size, BlobMapping* mapping) = 0;
    virtual void Unref(uint32_t resource_id) = 0;
    virtual int Submit(uint32_t context_id, const void* commands, uint32_t size) = 0;
    virtual int CreateFence(uint32_t context_id, uint32_t ring_idx, uint64_t fence_id) = 0;
    virtual void Poll() = 0;
};

// Venus through virglrenderer's in-process vkr (no render server, no fork). Returns nullptr with a
// reason when pvg-host was built without virglrenderer or the selected GPU cannot run it.
std::unique_ptr<Renderer> CreateVenusRenderer(uint32_t adapter_index, Renderer::FenceCallback on_fence, std::string* error);

// Answers capability negotiation and refuses rendering contexts. Used when Venus is unavailable,
// so the guest still learns why and keeps the synthetic display.
std::unique_ptr<Renderer> CreateNullRenderer();

} // namespace pvg
