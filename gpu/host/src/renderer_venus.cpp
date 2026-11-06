// Pome Virtual GPU host: Venus renderer on virglrenderer's vkr API. SPDX-License-Identifier: MIT
//
// virglrenderer's public virgl_renderer_* API reaches Venus only through its render server, a
// forked process that passes fds, which Windows cannot do. pvg-host therefore drives vkr directly,
// the way virglrenderer's own server/render_context.c does, inside its own process.
//
// The library is built with MinGW-w64 (gpu/host/virglrenderer/build-windows.ps1) and loaded at run
// time, so pvg-host (MSVC) has no link-time dependency on it and still answers capability
// negotiation when it is missing. Only plain C types cross the boundary.
#include "renderer.hpp"
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <vector>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

namespace pvg {
namespace {

// Mirrors of virglrenderer's C declarations (src/venus/vkr_renderer.h, src/virgl_resource.h, MIT).
enum VirglFdType : int { kFdDmabuf = 0, kFdOpaque = 1, kFdShm = 2 };
struct VirglVulkanInfo { uint8_t device_uuid[16]; uint8_t driver_uuid[16]; uint64_t allocation_size; uint32_t memory_type_index; };
using LogCb = void (*)(int level, const char* message, void* user);
using RetireFenceCb = void (*)(uint32_t ctx_id, uint32_t ring_idx, uint64_t fence_id);
struct VkrCallbacks { LogCb debug_logger; RetireFenceCb retire_fence; };
constexpr uint32_t kVkrThreadSync = 1u << 0, kVkrAsyncFenceCb = 1u << 1;
constexpr uint32_t kFenceFlagMergeable = 1u << 0;

struct VkrApi {
    size_t (*get_capset)(void*, uint32_t);
    bool (*init)(uint32_t, const VkrCallbacks*);
    void (*fini)();
    bool (*create_context)(uint32_t, uint32_t, uint32_t, const char*);
    void (*destroy_context)(uint32_t);
    bool (*submit_cmd)(uint32_t, void*, uint32_t);
    bool (*submit_fence)(uint32_t, uint32_t, uint64_t, uint64_t);
    bool (*create_resource)(uint32_t, uint32_t, uint64_t, uint64_t, uint32_t, VirglFdType*, int*, uint32_t*, VirglVulkanInfo*);
    void (*destroy_resource)(uint32_t, uint32_t);
    bool (*map_resource)(uint32_t, uint32_t, uint64_t, void**, uint64_t*);
};

#ifdef _WIN32
bool LoadVkr(VkrApi* api, std::string* error) {
    // Next to pvg-host.exe first (pome ships it there), then PVG_VIRGLRENDERER, then the DLL path.
    HMODULE lib = nullptr;
    wchar_t self[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, self, MAX_PATH)) {
        std::wstring dir(self);
        dir.resize(dir.find_last_of(L"\\/") + 1);
        lib = LoadLibraryExW((dir + L"libvirglrenderer-1.dll").c_str(), nullptr,
                             LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    }
    if (!lib) {
        wchar_t custom[MAX_PATH]{};
        if (GetEnvironmentVariableW(L"PVG_VIRGLRENDERER", custom, MAX_PATH))
            lib = LoadLibraryExW(custom, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    }
    if (!lib) lib = LoadLibraryW(L"libvirglrenderer-1.dll");
    if (!lib) { if (error) *error = "libvirglrenderer-1.dll not found next to pvg-host.exe (or PVG_VIRGLRENDERER)"; return false; }

    bool ok = true;
    auto get = [&](auto& fn, const char* name) {
        FARPROC p = GetProcAddress(lib, name);
        if (!p) { ok = false; if (error) *error = std::string("libvirglrenderer-1.dll lacks ") + name + " (rebuild with apply_windows.py)"; }
        static_assert(sizeof(fn) == sizeof(p));
        std::memcpy(&fn, &p, sizeof(p));
    };
    get(api->get_capset, "vkr_get_capset");
    get(api->init, "vkr_renderer_init");
    get(api->fini, "vkr_renderer_fini");
    get(api->create_context, "vkr_renderer_create_context");
    get(api->destroy_context, "vkr_renderer_destroy_context");
    get(api->submit_cmd, "vkr_renderer_submit_cmd");
    get(api->submit_fence, "vkr_renderer_submit_fence");
    get(api->create_resource, "vkr_renderer_create_resource");
    get(api->destroy_resource, "vkr_renderer_destroy_resource");
    get(api->map_resource, "vkr_renderer_map_resource");
    return ok;   // the module stays loaded for the life of the process (vkr is process-global)
}
#else
bool LoadVkr(VkrApi*, std::string* error) { if (error) *error = "the Venus back end is Windows-only"; return false; }
#endif

class VenusRenderer;
VenusRenderer* g_instance = nullptr;   // vkr keeps one global state and plain-function callbacks
std::mutex g_instance_mutex;

class VenusRenderer final : public Renderer {
public:
    explicit VenusRenderer(FenceCallback cb) : on_fence_(std::move(cb)) {}
    ~VenusRenderer() override {
        if (initialized_) api_.fini();
        std::lock_guard<std::mutex> lock(g_instance_mutex);
        if (g_instance == this) g_instance = nullptr;
    }

    bool Init(std::string* error) {
        std::lock_guard<std::mutex> lock(g_instance_mutex);
        if (g_instance) { if (error) *error = "only one Venus renderer per process"; return false; }
        if (!LoadVkr(&api_, error)) return false;
        callbacks_.debug_logger = [](int, const char* message, void*) {
            if (message) std::fprintf(stderr, "[vkr] %s", message);
        };
        callbacks_.retire_fence = [](uint32_t ctx, uint32_t ring, uint64_t fence) {
            // Called from vkr's sync threads; the session's callback is thread-safe.
            if (VenusRenderer* self = g_instance) self->on_fence_(ctx, ring, fence);
        };
        if (!api_.init(kVkrThreadSync | kVkrAsyncFenceCb, &callbacks_)) {
            if (error) *error = "vkr_renderer_init failed";
            return false;
        }
        initialized_ = true;
        g_instance = this;
        return true;
    }

    const char* Name() const override { return "venus"; }

    std::vector<uint8_t> VenusCapset() override {
        std::vector<uint8_t> caps(api_.get_capset(nullptr, 0));
        if (!caps.empty()) api_.get_capset(caps.data(), 0);
        return caps;
    }

    int CreateContext(uint32_t context_id, uint32_t capset_id, const std::string& name) override {
        if (capset_id != kCapsetVenus || context_id == 0) return -22;   // -EINVAL
        return api_.create_context(context_id, capset_id, static_cast<uint32_t>(name.size()), name.c_str()) ? 0 : -22;
    }

    void DestroyContext(uint32_t context_id) override {
        api_.destroy_context(context_id);   // frees every resource the context still holds
        std::lock_guard<std::mutex> lock(mutex_);
        for (auto it = resources_.begin(); it != resources_.end();)
            it = it->second == context_id ? resources_.erase(it) : std::next(it);
    }

    int CreateBlob(uint32_t context_id, uint32_t resource_id, uint32_t blob_mem, uint32_t blob_flags,
                   uint64_t blob_id, uint64_t size, BlobMapping* mapping) override {
        // vkr serves host-allocated blobs only: blob_id 0 is a shmem (rings, replies), otherwise a
        // VkDeviceMemory the guest allocated and is now exporting.
        if (blob_mem != kBlobMemHost3D || resource_id == 0 || size == 0) return -22;
        VirglFdType fd_type{};
        int fd = -1;
        uint32_t map_info = 0;
        VirglVulkanInfo vulkan_info{};
        if (!api_.create_resource(context_id, resource_id, blob_id, size, blob_flags, &fd_type, &fd, &map_info, &vulkan_info))
            return -22;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            resources_[resource_id] = context_id;
        }
        mapping->map_info = map_info;
        mapping->size = size;
        if (blob_flags & kBlobFlagMappable) {
            void* ptr = nullptr;
            uint64_t mapped = 0;
            if (!api_.map_resource(context_id, resource_id, blob_id, &ptr, &mapped) || !ptr) {
                Unref(resource_id);
                return -12;   // -ENOMEM: the host driver would not map it
            }
            mapping->host_ptr = ptr;
            mapping->size = mapped < size ? mapped : size;
        }
        return 0;
    }

    void Unref(uint32_t resource_id) override {
        uint32_t context_id = 0;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            auto it = resources_.find(resource_id);
            if (it == resources_.end()) return;
            context_id = it->second;
            resources_.erase(it);
        }
        api_.destroy_resource(context_id, resource_id);
    }

    int Submit(uint32_t context_id, const void* commands, uint32_t size) override {
        // vkr decodes in place; the session hands over its own copy of the guest's stream.
        return api_.submit_cmd(context_id, const_cast<void*>(commands), size) ? 0 : -22;
    }

    int CreateFence(uint32_t context_id, uint32_t ring_idx, uint64_t fence_id) override {
        return api_.submit_fence(context_id, kFenceFlagMergeable, ring_idx, fence_id) ? 0 : -22;
    }

    void Poll() override {}   // VKR_RENDERER_ASYNC_FENCE_CB: fences retire from vkr's own threads

private:
    FenceCallback on_fence_;
    VkrApi api_{};
    VkrCallbacks callbacks_{};
    bool initialized_ = false;
    std::mutex mutex_;
    std::map<uint32_t, uint32_t> resources_;   // resource id -> owning context
};

} // namespace

std::unique_ptr<Renderer> CreateVenusRenderer(uint32_t, Renderer::FenceCallback on_fence, std::string* error) {
    // Venus exposes every host physical device to the guest; CAPS tells the guest which one pvg-host
    // prefers (or the --adapter choice), and the guest selects it by its device UUID.
    auto r = std::make_unique<VenusRenderer>(std::move(on_fence));
    if (!r->Init(error)) return nullptr;
    return r;
}

} // namespace pvg
