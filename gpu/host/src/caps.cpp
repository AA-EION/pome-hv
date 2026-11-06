// Pome Virtual GPU host: Vulkan capability probe. SPDX-License-Identifier: MIT
//
// The guest must never advertise a Metal feature the host GPU cannot execute, whatever the vendor.
// Every bit below comes from a real vkGetPhysicalDeviceFeatures2/Properties2 query with the
// matching extension present; nothing is inferred from vendor IDs.
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include "caps.hpp"
#include <algorithm>
#include <bit>
#include <cstdio>
#include <cstring>
#include <set>
#include <sstream>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace pvg {
namespace {

struct Loader {
    PFN_vkGetInstanceProcAddr gipa = nullptr;
    void* module = nullptr;
    bool Load() {
#ifdef _WIN32
        HMODULE m = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!m) return false;
        module = m;
        gipa = reinterpret_cast<PFN_vkGetInstanceProcAddr>(GetProcAddress(m, "vkGetInstanceProcAddr"));
#else
        module = dlopen("libvulkan.so.1", RTLD_NOW | RTLD_LOCAL);
        if (!module) module = dlopen("libvulkan.1.dylib", RTLD_NOW | RTLD_LOCAL);
        if (!module) return false;
        gipa = reinterpret_cast<PFN_vkGetInstanceProcAddr>(dlsym(module, "vkGetInstanceProcAddr"));
#endif
        return gipa != nullptr;
    }
    ~Loader() {
#ifdef _WIN32
        if (module) FreeLibrary(static_cast<HMODULE>(module));
#else
        if (module) dlclose(module);
#endif
    }
};

void CopyString(char* out, size_t size, const char* in) {
    if (!size) return;
    std::snprintf(out, size, "%s", in ? in : "");
}

} // namespace

const char* FeatureName(uint64_t bit) {
    switch (bit) {
    case PVG_FEAT_RAY_QUERY: return "ray_query";
    case PVG_FEAT_RAY_TRACING_PIPELINE: return "ray_tracing_pipeline";
    case PVG_FEAT_ACCEL_STRUCTURE: return "acceleration_structure";
    case PVG_FEAT_MESH_SHADER: return "mesh_shader";
    case PVG_FEAT_COOPERATIVE_MATRIX: return "cooperative_matrix";
    case PVG_FEAT_BUFFER_DEVICE_ADDR: return "buffer_device_address";
    case PVG_FEAT_DESCRIPTOR_INDEXING: return "descriptor_indexing";
    case PVG_FEAT_MUTABLE_DESCRIPTOR: return "mutable_descriptor";
    case PVG_FEAT_FLOAT16_INT8: return "float16_int8";
    case PVG_FEAT_FLOAT64: return "float64";
    case PVG_FEAT_INT64_ATOMICS: return "int64_atomics";
    case PVG_FEAT_FLOAT_ATOMICS: return "float_atomics";
    case PVG_FEAT_TIMELINE_SEMAPHORE: return "timeline_semaphore";
    case PVG_FEAT_SYNCHRONIZATION2: return "synchronization2";
    case PVG_FEAT_DYNAMIC_RENDERING: return "dynamic_rendering";
    case PVG_FEAT_BARYCENTRIC: return "fragment_barycentric";
    case PVG_FEAT_SAMPLE_LOCATIONS: return "sample_locations";
    case PVG_FEAT_VARIABLE_RATE: return "variable_rate_shading";
    case PVG_FEAT_SPARSE_BINDING: return "sparse_binding";
    case PVG_FEAT_SPARSE_RESIDENCY: return "sparse_residency";
    case PVG_FEAT_BC_TEXTURES: return "bc_textures";
    case PVG_FEAT_ASTC_TEXTURES: return "astc_textures";
    case PVG_FEAT_ETC2_TEXTURES: return "etc2_textures";
    case PVG_FEAT_SUBGROUP_ARITHMETIC: return "subgroup_arithmetic";
    case PVG_FEAT_SUBGROUP_SHUFFLE: return "subgroup_shuffle";
    case PVG_FEAT_SUBGROUP_QUAD: return "subgroup_quad";
    case PVG_FEAT_SHADER_INT16: return "shader_int16";
    case PVG_FEAT_IMAGE_CUBE_ARRAY: return "image_cube_array";
    case PVG_FEAT_GEOMETRY_SHADER: return "geometry_shader";
    case PVG_FEAT_TESSELLATION: return "tessellation";
    case PVG_FEAT_DEPTH_CLAMP: return "depth_clamp";
    case PVG_FEAT_DEPTH_BOUNDS: return "depth_bounds";
    case PVG_FEAT_CALIBRATED_TIME: return "calibrated_timestamps";
    case PVG_FEAT_PIPELINE_ROBUSTNESS: return "pipeline_robustness";
    case PVG_FEAT_DEFERRED_HOST_OPS: return "deferred_host_operations";
    case PVG_FEAT_SHADER_CLOCK: return "shader_clock";
    case PVG_FEAT_MULTIVIEW: return "multiview";
    case PVG_FEAT_EXTERNAL_MEM_HOST: return "external_memory_host";
    case PVG_FEAT_DESCRIPTOR_BUFFER: return "descriptor_buffer";
    case PVG_FEAT_SHADER_OBJECT: return "shader_object";
    default: return nullptr;
    }
}

const char* MetalClassName(uint32_t bit) {
    switch (bit) {
    case PVG_METAL_COMMON3: return "MTLGPUFamilyCommon3";
    case PVG_METAL_MAC2: return "MTLGPUFamilyMac2";
    case PVG_METAL_METAL3: return "MTLGPUFamilyMetal3";
    case PVG_METAL_METAL4_CANDIDATE: return "Metal4 candidate";
    case PVG_METAL_RAYTRACING: return "raytracing";
    case PVG_METAL_RAYTRACING_RENDER: return "raytracing from render";
    case PVG_METAL_MESH: return "mesh shaders";
    case PVG_METAL_SIMDGROUP_MATRIX: return "simdgroup_matrix";
    case PVG_METAL_ARGBUF_TIER2: return "argument buffers tier 2";
    case PVG_METAL_BC_TEXTURES: return "BC textures";
    case PVG_METAL_FUNCTION_POINTERS: return "function pointers";
    case PVG_METAL_FLOAT64: return "float64 (emulated in MSL)";
    case PVG_METAL_32BIT_FLOAT_FILTER: return "32-bit float filtering";
    case PVG_METAL_SPARSE_TEXTURES: return "sparse textures";
    case PVG_METAL_TENSOR_ML: return "tensors / ML encoders";
    default: return nullptr;
    }
}

uint32_t MetalClassesFor(const pvg_adapter_caps& c, std::vector<std::string>* notes) {
    auto has = [&](uint64_t bits) { return (c.features & bits) == bits; };
    auto note = [&](const std::string& s) { if (notes) notes->push_back(s); };
    uint32_t m = 0;
    const uint32_t major = VK_API_VERSION_MAJOR(c.api_version), minor = VK_API_VERSION_MINOR(c.api_version);
    const bool vk12 = major > 1 || (major == 1 && minor >= 2);
    const bool vk13 = major > 1 || (major == 1 && minor >= 3);
    if (c.device_type == VK_PHYSICAL_DEVICE_TYPE_CPU) note("CPU Vulkan device: usable for conformance, not for acceleration");

    // Common3 / Mac2: what every Metal 2 Mac GPU provides.
    const uint64_t mac2 = PVG_FEAT_BC_TEXTURES | PVG_FEAT_IMAGE_CUBE_ARRAY | PVG_FEAT_DESCRIPTOR_INDEXING | PVG_FEAT_DEPTH_CLAMP;
    if (vk12 && has(mac2)) { m |= PVG_METAL_COMMON3 | PVG_METAL_MAC2 | PVG_METAL_BC_TEXTURES; }
    else note("Mac2 needs Vulkan 1.2, BC textures, cube arrays, descriptor indexing and depth clamp");

    if (has(PVG_FEAT_DESCRIPTOR_INDEXING | PVG_FEAT_BUFFER_DEVICE_ADDR)) m |= PVG_METAL_ARGBUF_TIER2;
    else note("argument buffers tier 2 need descriptor indexing and buffer device addresses");

    if (has(PVG_FEAT_ACCEL_STRUCTURE | PVG_FEAT_RAY_QUERY | PVG_FEAT_BUFFER_DEVICE_ADDR)) {
        m |= PVG_METAL_RAYTRACING | PVG_METAL_RAYTRACING_RENDER;
        if (has(PVG_FEAT_RAY_TRACING_PIPELINE)) m |= PVG_METAL_FUNCTION_POINTERS; // intersection/visible function tables
    } else note("Metal ray tracing needs acceleration structures, ray query and buffer device addresses");

    if (has(PVG_FEAT_MESH_SHADER)) m |= PVG_METAL_MESH; else note("no mesh/task shaders: object and mesh stages unavailable");
    if (has(PVG_FEAT_COOPERATIVE_MATRIX)) m |= PVG_METAL_SIMDGROUP_MATRIX;
    if (has(PVG_FEAT_FLOAT64)) m |= PVG_METAL_FLOAT64;
    if (has(PVG_FEAT_SPARSE_BINDING | PVG_FEAT_SPARSE_RESIDENCY)) m |= PVG_METAL_SPARSE_TEXTURES;
    m |= PVG_METAL_32BIT_FLOAT_FILTER; // linear filtering of R32F etc. is checked per format at runtime by the plugin

    const uint64_t metal3 = PVG_FEAT_BUFFER_DEVICE_ADDR | PVG_FEAT_DESCRIPTOR_INDEXING | PVG_FEAT_TIMELINE_SEMAPHORE
                          | PVG_FEAT_SYNCHRONIZATION2 | PVG_FEAT_DYNAMIC_RENDERING | PVG_FEAT_MESH_SHADER;
    if ((m & PVG_METAL_MAC2) && vk13 && has(metal3)) m |= PVG_METAL_METAL3;
    else note("Metal3 family needs Vulkan 1.3 with BDA, descriptor indexing, timeline semaphores, sync2, dynamic rendering and mesh shaders");

    if (has(PVG_FEAT_COOPERATIVE_MATRIX | PVG_FEAT_FLOAT16_INT8)) m |= PVG_METAL_TENSOR_ML;
    else note("MTLTensor/ML encoders need cooperative matrix with float16/int8");

    const uint64_t metal4 = PVG_FEAT_COOPERATIVE_MATRIX | PVG_FEAT_FLOAT16_INT8 | PVG_FEAT_RAY_QUERY | PVG_FEAT_ACCEL_STRUCTURE;
    if ((m & PVG_METAL_METAL3) && has(metal4) && (has(PVG_FEAT_DESCRIPTOR_BUFFER) || has(PVG_FEAT_MUTABLE_DESCRIPTOR)))
        m |= PVG_METAL_METAL4_CANDIDATE;
    else note("Metal 4 objects need Metal3 plus cooperative matrix, ray query and descriptor buffers or mutable descriptors");
    return m;
}

uint32_t PreferredAdapter(const std::vector<AdapterReport>& adapters) {
    auto rank = [](uint32_t type) {
        switch (type) {
        case VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU: return 4;
        case VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU: return 3;
        case VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU: return 2;
        case VK_PHYSICAL_DEVICE_TYPE_OTHER: return 1;
        default: return 0;
        }
    };
    uint32_t best = 0;
    for (uint32_t i = 1; i < adapters.size(); ++i) {
        const auto& a = adapters[i].caps; const auto& b = adapters[best].caps;
        const int ra = rank(a.device_type), rb = rank(b.device_type);
        if (ra > rb || (ra == rb && std::popcount(a.metal) > std::popcount(b.metal))
            || (ra == rb && a.metal == b.metal && a.device_local_bytes > b.device_local_bytes)) best = i;
    }
    return best;
}

HostReport ProbeHost() {
    HostReport report;
    Loader loader;
    if (!loader.Load()) { report.error = "No Vulkan loader (vulkan-1.dll) is installed. Install or update the GPU driver."; return report; }
    auto gipa = loader.gipa;
    auto vkEnumerateInstanceVersion = reinterpret_cast<PFN_vkEnumerateInstanceVersion>(gipa(nullptr, "vkEnumerateInstanceVersion"));
    auto vkCreateInstance = reinterpret_cast<PFN_vkCreateInstance>(gipa(nullptr, "vkCreateInstance"));
    report.loader_api_version = VK_API_VERSION_1_0;
    if (vkEnumerateInstanceVersion) vkEnumerateInstanceVersion(&report.loader_api_version);
    if (!vkCreateInstance) { report.error = "The Vulkan loader does not export vkCreateInstance."; return report; }

    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.pApplicationName = "pvg-host"; app.pEngineName = "Pome Virtual GPU";
    app.apiVersion = std::min<uint32_t>(report.loader_api_version, VK_API_VERSION_1_4);
    VkInstanceCreateInfo ici{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ici.pApplicationInfo = &app;
    VkInstance instance = VK_NULL_HANDLE;
    if (VkResult r = vkCreateInstance(&ici, nullptr, &instance); r != VK_SUCCESS) {
        report.error = "vkCreateInstance failed (" + std::to_string(r) + ")"; return report;
    }
#define LOAD(name) auto name = reinterpret_cast<PFN_##name>(gipa(instance, #name))
    LOAD(vkDestroyInstance); LOAD(vkEnumeratePhysicalDevices); LOAD(vkGetPhysicalDeviceProperties2);
    LOAD(vkGetPhysicalDeviceFeatures2); LOAD(vkEnumerateDeviceExtensionProperties); LOAD(vkGetPhysicalDeviceMemoryProperties);
#undef LOAD
    uint32_t count = 0;
    vkEnumeratePhysicalDevices(instance, &count, nullptr);
    std::vector<VkPhysicalDevice> devices(count);
    vkEnumeratePhysicalDevices(instance, &count, devices.data());

    std::set<std::string> seenDevices;   // a GPU registered by two ICD manifests appears twice
    for (uint32_t index = 0; index < count; ++index) {
        VkPhysicalDevice pd = devices[index];
        AdapterReport rep; auto& c = rep.caps; c.index = static_cast<uint32_t>(report.adapters.size());
        uint32_t extCount = 0; vkEnumerateDeviceExtensionProperties(pd, nullptr, &extCount, nullptr);
        std::vector<VkExtensionProperties> exts(extCount); vkEnumerateDeviceExtensionProperties(pd, nullptr, &extCount, exts.data());
        std::set<std::string> ext;
        for (auto& e : exts) { ext.insert(e.extensionName); rep.extensions.emplace_back(e.extensionName); }
        auto hasExt = [&](const char* n) { return ext.count(n) != 0; };

        // Properties chain
        VkPhysicalDeviceDriverProperties driver{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRIVER_PROPERTIES};
        VkPhysicalDeviceIDProperties ids{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES}; ids.pNext = &driver;
        VkPhysicalDeviceSubgroupProperties subgroup{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SUBGROUP_PROPERTIES}; subgroup.pNext = &ids;
        VkPhysicalDeviceRayTracingPipelinePropertiesKHR rtp{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_TRACING_PIPELINE_PROPERTIES_KHR};
        VkPhysicalDeviceMeshShaderPropertiesEXT meshp{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MESH_SHADER_PROPERTIES_EXT};
        void* propChain = &subgroup;
        if (hasExt(VK_KHR_RAY_TRACING_PIPELINE_EXTENSION_NAME)) { rtp.pNext = propChain; propChain = &rtp; }
        if (hasExt(VK_EXT_MESH_SHADER_EXTENSION_NAME)) { meshp.pNext = propChain; propChain = &meshp; }
        VkPhysicalDeviceProperties2 props{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2}; props.pNext = propChain;
        vkGetPhysicalDeviceProperties2(pd, &props);
        const auto& p = props.properties;

        // Features chain (only extensions that exist are chained, as the spec requires)
        VkPhysicalDeviceVulkan11Features f11{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_1_FEATURES};
        VkPhysicalDeviceVulkan12Features f12{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES}; f11.pNext = &f12;
        VkPhysicalDeviceVulkan13Features f13{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
        void* tail = &f12;
        const bool api13 = p.apiVersion >= VK_API_VERSION_1_3;
        if (api13) { f12.pNext = &f13; tail = &f13; }
        auto chain = [&](auto& s) { reinterpret_cast<VkBaseOutStructure*>(tail)->pNext = reinterpret_cast<VkBaseOutStructure*>(&s); tail = &s; };
        VkPhysicalDeviceAccelerationStructureFeaturesKHR as{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ACCELERATION_STRUCTURE_FEATURES_KHR};
        VkPhysicalDeviceRayQueryFeaturesKHR rq{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_QUERY_FEATURES_KHR};
        VkPhysicalDeviceRayTracingPipelineFeaturesKHR rt{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_TRACING_PIPELINE_FEATURES_KHR};
        VkPhysicalDeviceMeshShaderFeaturesEXT mesh{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MESH_SHADER_FEATURES_EXT};
        VkPhysicalDeviceCooperativeMatrixFeaturesKHR coop{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_COOPERATIVE_MATRIX_FEATURES_KHR};
        VkPhysicalDeviceFragmentShaderBarycentricFeaturesKHR bary{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FRAGMENT_SHADER_BARYCENTRIC_FEATURES_KHR};
        VkPhysicalDeviceMutableDescriptorTypeFeaturesEXT mut{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MUTABLE_DESCRIPTOR_TYPE_FEATURES_EXT};
        VkPhysicalDeviceDescriptorBufferFeaturesEXT dbuf{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_BUFFER_FEATURES_EXT};
        VkPhysicalDeviceShaderObjectFeaturesEXT sobj{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_OBJECT_FEATURES_EXT};
        VkPhysicalDeviceShaderAtomicFloatFeaturesEXT fatom{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_ATOMIC_FLOAT_FEATURES_EXT};
        VkPhysicalDeviceFragmentShadingRateFeaturesKHR vrs{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FRAGMENT_SHADING_RATE_FEATURES_KHR};
        VkPhysicalDeviceShaderClockFeaturesKHR clock{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_CLOCK_FEATURES_KHR};
        VkPhysicalDevicePipelineRobustnessFeaturesEXT robust{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PIPELINE_ROBUSTNESS_FEATURES_EXT};
        if (hasExt(VK_KHR_ACCELERATION_STRUCTURE_EXTENSION_NAME)) chain(as);
        if (hasExt(VK_KHR_RAY_QUERY_EXTENSION_NAME)) chain(rq);
        if (hasExt(VK_KHR_RAY_TRACING_PIPELINE_EXTENSION_NAME)) chain(rt);
        if (hasExt(VK_EXT_MESH_SHADER_EXTENSION_NAME)) chain(mesh);
        if (hasExt(VK_KHR_COOPERATIVE_MATRIX_EXTENSION_NAME)) chain(coop);
        if (hasExt(VK_KHR_FRAGMENT_SHADER_BARYCENTRIC_EXTENSION_NAME)) chain(bary);
        if (hasExt(VK_EXT_MUTABLE_DESCRIPTOR_TYPE_EXTENSION_NAME)) chain(mut);
        if (hasExt(VK_EXT_DESCRIPTOR_BUFFER_EXTENSION_NAME)) chain(dbuf);
        if (hasExt(VK_EXT_SHADER_OBJECT_EXTENSION_NAME)) chain(sobj);
        if (hasExt(VK_EXT_SHADER_ATOMIC_FLOAT_EXTENSION_NAME)) chain(fatom);
        if (hasExt(VK_KHR_FRAGMENT_SHADING_RATE_EXTENSION_NAME)) chain(vrs);
        if (hasExt(VK_KHR_SHADER_CLOCK_EXTENSION_NAME)) chain(clock);
        if (hasExt(VK_EXT_PIPELINE_ROBUSTNESS_EXTENSION_NAME)) chain(robust);
        VkPhysicalDeviceFeatures2 feats{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2}; feats.pNext = &f11;
        vkGetPhysicalDeviceFeatures2(pd, &feats);
        const auto& f = feats.features;

        c.vendor_id = p.vendorID; c.device_id = p.deviceID; c.driver_version = p.driverVersion; c.api_version = p.apiVersion;
        c.device_type = p.deviceType;
        std::memcpy(c.device_uuid, ids.deviceUUID, 16); std::memcpy(c.driver_uuid, ids.driverUUID, 16);
        if (!seenDevices.insert(std::string(reinterpret_cast<const char*>(ids.deviceUUID), 16) + std::string(reinterpret_cast<const char*>(ids.driverUUID), 16)).second) continue;
        CopyString(c.device_name, sizeof c.device_name, p.deviceName);
        CopyString(c.driver_name, sizeof c.driver_name, driver.driverName);
        CopyString(c.driver_info, sizeof c.driver_info, driver.driverInfo);
        c.subgroup_size = subgroup.subgroupSize;
        c.max_image_dimension_2d = p.limits.maxImageDimension2D; c.max_image_dimension_3d = p.limits.maxImageDimension3D;
        c.max_compute_workgroup_invocations = p.limits.maxComputeWorkGroupInvocations;
        for (int i = 0; i < 3; ++i) c.max_compute_workgroup_size[i] = p.limits.maxComputeWorkGroupSize[i];
        c.max_bound_descriptor_sets = p.limits.maxBoundDescriptorSets; c.max_push_constants_size = p.limits.maxPushConstantsSize;
        c.max_framebuffer_width = p.limits.maxFramebufferWidth; c.max_framebuffer_height = p.limits.maxFramebufferHeight;
        c.max_color_attachments = p.limits.maxColorAttachments;
        c.max_sampler_anisotropy_x100 = static_cast<uint32_t>(p.limits.maxSamplerAnisotropy * 100.0f);
        c.timestamp_period_ps = static_cast<uint32_t>(p.limits.timestampPeriod * 1000.0f);

        VkPhysicalDeviceMemoryProperties mem{}; vkGetPhysicalDeviceMemoryProperties(pd, &mem);
        for (uint32_t h = 0; h < mem.memoryHeapCount; ++h) {
            bool hostVisible = false;
            for (uint32_t t = 0; t < mem.memoryTypeCount; ++t)
                if (mem.memoryTypes[t].heapIndex == h && (mem.memoryTypes[t].propertyFlags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT)) hostVisible = true;
            const uint64_t size = mem.memoryHeaps[h].size;
            if (mem.memoryHeaps[h].flags & VK_MEMORY_HEAP_DEVICE_LOCAL_BIT) c.device_local_bytes = std::max(c.device_local_bytes, size);
            if (hostVisible) c.host_visible_bytes = std::max(c.host_visible_bytes, size);
        }

        uint64_t bits = 0;
        auto set = [&](bool on, uint64_t bit) { if (on) bits |= bit; };
        set(rq.rayQuery && hasExt(VK_KHR_RAY_QUERY_EXTENSION_NAME), PVG_FEAT_RAY_QUERY);
        set(rt.rayTracingPipeline && hasExt(VK_KHR_RAY_TRACING_PIPELINE_EXTENSION_NAME), PVG_FEAT_RAY_TRACING_PIPELINE);
        set(as.accelerationStructure && hasExt(VK_KHR_ACCELERATION_STRUCTURE_EXTENSION_NAME), PVG_FEAT_ACCEL_STRUCTURE);
        set(mesh.meshShader && mesh.taskShader && hasExt(VK_EXT_MESH_SHADER_EXTENSION_NAME), PVG_FEAT_MESH_SHADER);
        set(coop.cooperativeMatrix && hasExt(VK_KHR_COOPERATIVE_MATRIX_EXTENSION_NAME), PVG_FEAT_COOPERATIVE_MATRIX);
        set(f12.bufferDeviceAddress, PVG_FEAT_BUFFER_DEVICE_ADDR);
        set(f12.descriptorIndexing && f12.runtimeDescriptorArray && f12.descriptorBindingPartiallyBound
            && f12.shaderSampledImageArrayNonUniformIndexing, PVG_FEAT_DESCRIPTOR_INDEXING);
        set(mut.mutableDescriptorType && hasExt(VK_EXT_MUTABLE_DESCRIPTOR_TYPE_EXTENSION_NAME), PVG_FEAT_MUTABLE_DESCRIPTOR);
        set(f12.shaderFloat16 && f12.shaderInt8, PVG_FEAT_FLOAT16_INT8);
        set(f.shaderFloat64, PVG_FEAT_FLOAT64);
        set(f12.shaderBufferInt64Atomics, PVG_FEAT_INT64_ATOMICS);
        set(fatom.shaderBufferFloat32AtomicAdd && hasExt(VK_EXT_SHADER_ATOMIC_FLOAT_EXTENSION_NAME), PVG_FEAT_FLOAT_ATOMICS);
        set(f12.timelineSemaphore, PVG_FEAT_TIMELINE_SEMAPHORE);
        set(api13 && f13.synchronization2, PVG_FEAT_SYNCHRONIZATION2);
        set(api13 && f13.dynamicRendering, PVG_FEAT_DYNAMIC_RENDERING);
        set(bary.fragmentShaderBarycentric && hasExt(VK_KHR_FRAGMENT_SHADER_BARYCENTRIC_EXTENSION_NAME), PVG_FEAT_BARYCENTRIC);
        set(hasExt(VK_EXT_SAMPLE_LOCATIONS_EXTENSION_NAME), PVG_FEAT_SAMPLE_LOCATIONS);
        set(vrs.pipelineFragmentShadingRate && hasExt(VK_KHR_FRAGMENT_SHADING_RATE_EXTENSION_NAME), PVG_FEAT_VARIABLE_RATE);
        set(f.sparseBinding, PVG_FEAT_SPARSE_BINDING);
        set(f.sparseResidencyImage2D && f.sparseResidencyBuffer, PVG_FEAT_SPARSE_RESIDENCY);
        set(f.textureCompressionBC, PVG_FEAT_BC_TEXTURES);
        set(f.textureCompressionASTC_LDR, PVG_FEAT_ASTC_TEXTURES);
        set(f.textureCompressionETC2, PVG_FEAT_ETC2_TEXTURES);
        set(subgroup.supportedOperations & VK_SUBGROUP_FEATURE_ARITHMETIC_BIT, PVG_FEAT_SUBGROUP_ARITHMETIC);
        set(subgroup.supportedOperations & VK_SUBGROUP_FEATURE_SHUFFLE_BIT, PVG_FEAT_SUBGROUP_SHUFFLE);
        set(subgroup.supportedOperations & VK_SUBGROUP_FEATURE_QUAD_BIT, PVG_FEAT_SUBGROUP_QUAD);
        set(f.shaderInt16, PVG_FEAT_SHADER_INT16);
        set(f.imageCubeArray, PVG_FEAT_IMAGE_CUBE_ARRAY);
        set(f.geometryShader, PVG_FEAT_GEOMETRY_SHADER);
        set(f.tessellationShader, PVG_FEAT_TESSELLATION);
        set(f.depthClamp, PVG_FEAT_DEPTH_CLAMP);
        set(f.depthBounds, PVG_FEAT_DEPTH_BOUNDS);
        set(hasExt(VK_KHR_CALIBRATED_TIMESTAMPS_EXTENSION_NAME) || hasExt(VK_EXT_CALIBRATED_TIMESTAMPS_EXTENSION_NAME), PVG_FEAT_CALIBRATED_TIME);
        set(robust.pipelineRobustness && hasExt(VK_EXT_PIPELINE_ROBUSTNESS_EXTENSION_NAME), PVG_FEAT_PIPELINE_ROBUSTNESS);
        set(hasExt(VK_KHR_DEFERRED_HOST_OPERATIONS_EXTENSION_NAME), PVG_FEAT_DEFERRED_HOST_OPS);
        set(clock.shaderDeviceClock && hasExt(VK_KHR_SHADER_CLOCK_EXTENSION_NAME), PVG_FEAT_SHADER_CLOCK);
        set(f11.multiview, PVG_FEAT_MULTIVIEW);
        set(hasExt(VK_EXT_EXTERNAL_MEMORY_HOST_EXTENSION_NAME), PVG_FEAT_EXTERNAL_MEM_HOST);
        set(dbuf.descriptorBuffer && hasExt(VK_EXT_DESCRIPTOR_BUFFER_EXTENSION_NAME), PVG_FEAT_DESCRIPTOR_BUFFER);
        set(sobj.shaderObject && hasExt(VK_EXT_SHADER_OBJECT_EXTENSION_NAME), PVG_FEAT_SHADER_OBJECT);
        c.features = bits;
        c.max_ray_recursion_depth = (bits & PVG_FEAT_RAY_TRACING_PIPELINE) ? rtp.maxRayRecursionDepth : 0;
        c.max_mesh_output_vertices = (bits & PVG_FEAT_MESH_SHADER) ? meshp.maxMeshOutputVertices : 0;
        c.metal = MetalClassesFor(c, &rep.notes);
        report.adapters.push_back(std::move(rep));
    }
    vkDestroyInstance(instance, nullptr);
    if (report.adapters.empty()) report.error = "Vulkan reports no physical devices.";
    else report.preferred = PreferredAdapter(report.adapters);
    return report;
}

namespace {
std::string Escape(const std::string& s) {
    std::string out;
    for (char ch : s) {
        if (ch == '"' || ch == '\\') { out += '\\'; out += ch; }
        else if (static_cast<unsigned char>(ch) < 0x20) { char b[8]; std::snprintf(b, sizeof b, "\\u%04x", ch); out += b; }
        else out += ch;
    }
    return out;
}
std::string Version(uint32_t v) {
    return std::to_string(VK_API_VERSION_MAJOR(v)) + "." + std::to_string(VK_API_VERSION_MINOR(v)) + "." + std::to_string(VK_API_VERSION_PATCH(v));
}
} // namespace

std::string ToJson(const HostReport& r) {
    std::ostringstream o;
    o << "{\"loaderApi\":\"" << Version(r.loader_api_version) << "\",\"preferred\":" << r.preferred;
    if (!r.error.empty()) o << ",\"error\":\"" << Escape(r.error) << "\"";
    o << ",\"adapters\":[";
    for (size_t i = 0; i < r.adapters.size(); ++i) {
        const auto& a = r.adapters[i]; const auto& c = a.caps;
        if (i) o << ",";
        char vendor[16], device[16];
        std::snprintf(vendor, sizeof vendor, "0x%04x", c.vendor_id); std::snprintf(device, sizeof device, "0x%04x", c.device_id);
        o << "{\"index\":" << c.index << ",\"name\":\"" << Escape(c.device_name) << "\",\"vendor\":\"" << vendor << "\",\"device\":\"" << device
          << "\",\"type\":" << c.device_type << ",\"api\":\"" << Version(c.api_version) << "\",\"driver\":\"" << Escape(c.driver_name)
          << " " << Escape(c.driver_info) << "\",\"vramBytes\":" << c.device_local_bytes << ",\"subgroup\":" << c.subgroup_size << ",\"features\":[";
        bool first = true;
        for (int b = 0; b < 64; ++b) if (c.features & (1ull << b)) { if (const char* n = FeatureName(1ull << b)) { o << (first ? "" : ",") << "\"" << n << "\""; first = false; } }
        o << "],\"metal\":["; first = true;
        for (int b = 0; b < 32; ++b) if (c.metal & (1u << b)) { if (const char* n = MetalClassName(1u << b)) { o << (first ? "" : ",") << "\"" << n << "\""; first = false; } }
        o << "],\"notes\":["; first = true;
        for (const auto& n : a.notes) { o << (first ? "" : ",") << "\"" << Escape(n) << "\""; first = false; }
        o << "],\"extensionCount\":" << a.extensions.size() << "}";
    }
    o << "]}";
    return o.str();
}

std::vector<uint8_t> SerializeCaps(const HostReport& r, uint32_t host_flags, const std::vector<uint8_t>& venus) {
    pvg_caps_header h{};
    h.protocol_major = PVG_PROTOCOL_MAJOR; h.protocol_minor = PVG_PROTOCOL_MINOR;
    h.adapter_count = static_cast<uint32_t>(r.adapters.size()); h.preferred_adapter = r.preferred;
    h.venus_capset_size = static_cast<uint32_t>(venus.size()); h.host_flags = host_flags;
#ifdef _WIN32
    h.host_build = 0;
    if (HMODULE nt = GetModuleHandleW(L"ntdll.dll")) {
        using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
        if (auto fn = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(nt, "RtlGetVersion"))) {
            RTL_OSVERSIONINFOW v{sizeof v}; if (fn(&v) == 0) h.host_build = v.dwBuildNumber;
        }
    }
#endif
    CopyString(h.host_renderer, sizeof h.host_renderer, (host_flags & PVG_HOST_VENUS_AVAILABLE) ? "pvg-host 0.1 (virglrenderer vkr)" : "pvg-host 0.1 (capabilities only)");
    std::vector<uint8_t> out(sizeof h + r.adapters.size() * sizeof(pvg_adapter_caps) + venus.size());
    std::memcpy(out.data(), &h, sizeof h);
    for (size_t i = 0; i < r.adapters.size(); ++i) std::memcpy(out.data() + sizeof h + i * sizeof(pvg_adapter_caps), &r.adapters[i].caps, sizeof(pvg_adapter_caps));
    if (!venus.empty()) std::memcpy(out.data() + sizeof h + r.adapters.size() * sizeof(pvg_adapter_caps), venus.data(), venus.size());
    return out;
}

} // namespace pvg
