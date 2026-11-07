/*
 * Pome Virtual GPU (PVG) wire protocol.
 * Copyright (c) 2026 pome contributors. SPDX-License-Identifier: MIT
 *
 * One reliable byte stream per VM over a Hyper-V socket (AF_HYPERV on Windows, a VMBus pipe-mode
 * channel in the macOS guest). The Windows host companion (pvg-host) connects to the guest when the
 * VM starts; the guest kext accepts the offered channel. All integers are little endian. Every
 * message starts with pvg_msg_header and is padded to 8 bytes.
 *
 * The protocol carries three things:
 *   1. capability negotiation: the host reports what each host GPU really supports, so the guest
 *      advertises only Metal features that can be honoured (any vendor, not only NVIDIA);
 *   2. a virtio-gpu style context API (contexts, blob resources, execbuffer, fences) whose payloads
 *      are Mesa Venus command streams, executed on the host by virglrenderer's vkr;
 *   3. mirrored memory: a VM cannot share pages with a host process, so every mappable resource has
 *      a guest copy and a host copy, synchronised at defined points (ring notify, execbuffer,
 *      flush/invalidate, explicit transfers and host-pushed feedback ranges).
 *
 * This header is shared, byte for byte, by the host (C++), the guest kext (C++) and the guest
 * Mesa renderer (C). Keep it C99, no padding surprises: every struct is explicitly sized.
 */
#ifndef PVG_WIRE_H
#define PVG_WIRE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define PVG_MAGIC              0x31475650u /* "PVG1" */
#define PVG_PROTOCOL_MAJOR     1u
#define PVG_PROTOCOL_MINOR     0u
#define PVG_MAX_MESSAGE        (16u * 1024u * 1024u)
#define PVG_PAGE_SIZE          4096u

/* Hyper-V socket service. The value follows the Linux AF_VSOCK template
 * (<port>-facb-11e6-bd58-64006a7986d3) with port 0x50564731 ("PVG1"), so a Linux guest using
 * hv_sock could reach the same host service for diagnostics. */
#define PVG_SERVICE_GUID_STRING "50564731-facb-11e6-bd58-64006a7986d3"
#define PVG_VSOCK_PORT          0x50564731u

enum pvg_msg_type {
    /* negotiation */
    PVG_MSG_HELLO            = 0x0001, /* guest -> host: pvg_hello */
    PVG_MSG_CAPS             = 0x0002, /* host -> guest: pvg_caps_header + adapters + capset */
    PVG_MSG_SELECT_ADAPTER   = 0x0003, /* guest -> host: pvg_select_adapter */
    PVG_MSG_ERROR            = 0x000F, /* either direction: pvg_error */

    /* virtio-gpu style context API (mirrors DRM_IOCTL_VIRTGPU_*) */
    PVG_MSG_CONTEXT_INIT     = 0x0100, /* pvg_context_init */
    PVG_MSG_CONTEXT_DESTROY  = 0x0101, /* pvg_context_ref */
    PVG_MSG_RESOURCE_BLOB    = 0x0110, /* pvg_resource_blob -> reply pvg_resource_reply */
    PVG_MSG_RESOURCE_UNREF   = 0x0111, /* pvg_resource_ref */
    PVG_MSG_EXECBUFFER       = 0x0120, /* pvg_execbuffer + command bytes */
    PVG_MSG_FENCE_SIGNALED   = 0x0121, /* host -> guest: pvg_fence */
    PVG_MSG_WAIT             = 0x0122, /* guest -> host: pvg_fence (reply when signalled) */

    /* mirrored memory */
    PVG_MSG_WRITE_RANGE      = 0x0200, /* guest -> host: pvg_range + bytes (guest copy is newer) */
    PVG_MSG_READ_RANGE       = 0x0201, /* guest -> host: pvg_range; reply PVG_MSG_RANGE_DATA */
    PVG_MSG_RANGE_DATA       = 0x0202, /* host -> guest: pvg_range + bytes (host copy is newer) */
    PVG_MSG_WATCH_RANGE      = 0x0203, /* guest -> host: push changes to this range (flags: PVG_RANGE_CONTROL) */

    /* presentation and telemetry */
    PVG_MSG_PRESENT          = 0x0300, /* guest -> host: pvg_present (host window, no readback) */
    PVG_MSG_STATS            = 0x0310, /* host -> guest: pvg_stats */

    PVG_MSG_REPLY            = 0x8000  /* OR'ed into a type for its reply */
};

typedef struct pvg_msg_header {
    uint32_t magic;      /* PVG_MAGIC */
    uint16_t type;       /* enum pvg_msg_type, | PVG_MSG_REPLY for replies */
    uint16_t flags;      /* PVG_FLAG_* */
    uint32_t length;     /* payload bytes after this header, <= PVG_MAX_MESSAGE */
    uint32_t sequence;   /* request id; replies echo it */
} pvg_msg_header;

#define PVG_FLAG_NEEDS_REPLY 0x0001u

typedef struct pvg_hello {
    uint16_t protocol_major, protocol_minor;
    uint32_t guest_page_size;
    uint32_t guest_os_build;      /* Darwin major << 16 | minor << 8 | patch */
    uint32_t guest_driver_version;
    uint64_t guest_features;      /* PVG_GUEST_* */
    char     guest_name[64];      /* product name for logs, NUL terminated */
} pvg_hello;

#define PVG_GUEST_METAL_PLUGIN   (1ull << 0)
#define PVG_GUEST_VENUS          (1ull << 1)
#define PVG_GUEST_HOST_PRESENT   (1ull << 2)

/* Feature bits computed by the host from real Vulkan queries. The guest maps them to Metal. */
#define PVG_FEAT_RAY_QUERY            (1ull << 0)
#define PVG_FEAT_RAY_TRACING_PIPELINE (1ull << 1)
#define PVG_FEAT_ACCEL_STRUCTURE      (1ull << 2)
#define PVG_FEAT_MESH_SHADER          (1ull << 3)
#define PVG_FEAT_COOPERATIVE_MATRIX   (1ull << 4)
#define PVG_FEAT_BUFFER_DEVICE_ADDR   (1ull << 5)
#define PVG_FEAT_DESCRIPTOR_INDEXING  (1ull << 6)
#define PVG_FEAT_MUTABLE_DESCRIPTOR   (1ull << 7)
#define PVG_FEAT_FLOAT16_INT8         (1ull << 8)
#define PVG_FEAT_FLOAT64              (1ull << 9)
#define PVG_FEAT_INT64_ATOMICS        (1ull << 10)
#define PVG_FEAT_FLOAT_ATOMICS        (1ull << 11)
#define PVG_FEAT_TIMELINE_SEMAPHORE   (1ull << 12)
#define PVG_FEAT_SYNCHRONIZATION2     (1ull << 13)
#define PVG_FEAT_DYNAMIC_RENDERING    (1ull << 14)
#define PVG_FEAT_BARYCENTRIC          (1ull << 15)
#define PVG_FEAT_SAMPLE_LOCATIONS     (1ull << 16)
#define PVG_FEAT_VARIABLE_RATE        (1ull << 17)
#define PVG_FEAT_SPARSE_BINDING       (1ull << 18)
#define PVG_FEAT_SPARSE_RESIDENCY     (1ull << 19)
#define PVG_FEAT_BC_TEXTURES          (1ull << 20)
#define PVG_FEAT_ASTC_TEXTURES        (1ull << 21)
#define PVG_FEAT_ETC2_TEXTURES        (1ull << 22)
#define PVG_FEAT_SUBGROUP_ARITHMETIC  (1ull << 23)
#define PVG_FEAT_SUBGROUP_SHUFFLE     (1ull << 24)
#define PVG_FEAT_SUBGROUP_QUAD        (1ull << 25)
#define PVG_FEAT_SHADER_INT16         (1ull << 26)
#define PVG_FEAT_IMAGE_CUBE_ARRAY     (1ull << 27)
#define PVG_FEAT_GEOMETRY_SHADER      (1ull << 28)
#define PVG_FEAT_TESSELLATION         (1ull << 29)
#define PVG_FEAT_DEPTH_CLAMP          (1ull << 30)
#define PVG_FEAT_DEPTH_BOUNDS         (1ull << 31)
#define PVG_FEAT_CALIBRATED_TIME      (1ull << 32)
#define PVG_FEAT_PIPELINE_ROBUSTNESS  (1ull << 33)
#define PVG_FEAT_DEFERRED_HOST_OPS    (1ull << 34)
#define PVG_FEAT_SHADER_CLOCK         (1ull << 35)
#define PVG_FEAT_MULTIVIEW            (1ull << 36)
#define PVG_FEAT_EXTERNAL_MEM_HOST    (1ull << 37)
#define PVG_FEAT_DESCRIPTOR_BUFFER    (1ull << 38)
#define PVG_FEAT_SHADER_OBJECT        (1ull << 39)

/* Metal capability classes the guest may expose, decided on the host from the features above. */
#define PVG_METAL_COMMON3            (1u << 0)  /* MTLGPUFamilyCommon3 */
#define PVG_METAL_MAC2               (1u << 1)  /* MTLGPUFamilyMac2 */
#define PVG_METAL_METAL3             (1u << 2)  /* MTLGPUFamilyMetal3 */
#define PVG_METAL_METAL4_CANDIDATE   (1u << 3)  /* MTL4 objects can be built on this device (see docs) */
#define PVG_METAL_RAYTRACING         (1u << 4)  /* supportsRaytracing */
#define PVG_METAL_RAYTRACING_RENDER  (1u << 5)  /* supportsRaytracingFromRender (ray query in fragment) */
#define PVG_METAL_MESH               (1u << 6)  /* object/mesh shaders */
#define PVG_METAL_SIMDGROUP_MATRIX   (1u << 7)  /* simdgroup_matrix via cooperative matrix */
#define PVG_METAL_ARGBUF_TIER2       (1u << 8)
#define PVG_METAL_BC_TEXTURES        (1u << 9)
#define PVG_METAL_FUNCTION_POINTERS  (1u << 10)
#define PVG_METAL_FLOAT64            (1u << 11)
#define PVG_METAL_32BIT_FLOAT_FILTER (1u << 12)
#define PVG_METAL_SPARSE_TEXTURES    (1u << 13)
#define PVG_METAL_TENSOR_ML          (1u << 14)  /* MTLTensor / ML encoders via cooperative matrix */

typedef struct pvg_adapter_caps {
    uint32_t index;
    uint32_t vendor_id, device_id;
    uint32_t driver_version;          /* VkPhysicalDeviceProperties::driverVersion (vendor encoding) */
    uint32_t api_version;             /* Vulkan API version the host driver reports */
    uint32_t device_type;             /* VkPhysicalDeviceType */
    uint8_t  device_uuid[16];
    uint8_t  driver_uuid[16];
    char     device_name[256];
    char     driver_name[64];         /* VkPhysicalDeviceDriverProperties::driverName */
    char     driver_info[64];
    uint64_t features;                /* PVG_FEAT_* */
    uint32_t metal;                   /* PVG_METAL_* */
    uint32_t subgroup_size;
    uint64_t device_local_bytes;      /* largest DEVICE_LOCAL heap */
    uint64_t host_visible_bytes;      /* largest HOST_VISIBLE heap */
    uint32_t max_image_dimension_2d;
    uint32_t max_image_dimension_3d;
    uint32_t max_compute_workgroup_invocations;
    uint32_t max_compute_workgroup_size[3];
    uint32_t max_bound_descriptor_sets;
    uint32_t max_push_constants_size;
    uint32_t max_framebuffer_width, max_framebuffer_height;
    uint32_t max_color_attachments;
    uint32_t max_sampler_anisotropy_x100;
    uint32_t timestamp_period_ps;      /* nanoseconds * 1000 */
    uint32_t max_ray_recursion_depth;  /* 0 without ray tracing pipelines */
    uint32_t max_mesh_output_vertices; /* 0 without mesh shaders */
    uint32_t reserved[6];
} pvg_adapter_caps;

typedef struct pvg_caps_header {
    uint16_t protocol_major, protocol_minor;
    uint32_t adapter_count;           /* pvg_adapter_caps follow */
    uint32_t preferred_adapter;       /* host's choice: discrete > integrated > others */
    uint32_t venus_capset_size;       /* virglrenderer VIRGL_RENDERER_CAPSET_VENUS bytes after adapters */
    uint32_t host_build;              /* Windows build number */
    uint32_t host_flags;              /* PVG_HOST_* */
    char     host_renderer[64];       /* e.g. "pvg-host 0.1 (virglrenderer vkr)" */
} pvg_caps_header;

#define PVG_HOST_VENUS_AVAILABLE   (1u << 0)  /* the Venus renderer initialised on this host */
#define PVG_HOST_PRESENT_WINDOW    (1u << 1)  /* PVG_MSG_PRESENT is shown in pome's window */
#define PVG_HOST_SOFTWARE_ONLY     (1u << 2)  /* only a CPU Vulkan device exists (e.g. lavapipe) */

typedef struct pvg_select_adapter { uint32_t index, reserved; } pvg_select_adapter;

typedef struct pvg_error { int32_t code; uint32_t request_type; char message[248]; } pvg_error;

typedef struct pvg_context_init {
    uint32_t context_id;
    uint32_t capset_id;               /* 4 = Venus */
    uint32_t flags, reserved;
    char     debug_name[64];
} pvg_context_init;
typedef struct pvg_context_ref { uint32_t context_id, reserved; } pvg_context_ref;

typedef struct pvg_resource_blob {
    uint32_t context_id;
    uint32_t resource_id;             /* chosen by the guest, unique per VM */
    uint32_t blob_mem;                /* VIRTGPU_BLOB_MEM_* */
    uint32_t blob_flags;              /* VIRTGPU_BLOB_FLAG_* */
    uint64_t blob_id;                 /* Venus memory id, or 0 for shmem */
    uint64_t size;
} pvg_resource_blob;
typedef struct pvg_resource_reply { int32_t result; uint32_t map_info; uint64_t size; } pvg_resource_reply;
typedef struct pvg_resource_ref { uint32_t resource_id, reserved; } pvg_resource_ref;

typedef struct pvg_execbuffer {
    uint32_t context_id;
    uint32_t ring_idx;
    uint64_t fence_id;                /* 0: no fence */
    uint32_t command_size;            /* Venus command bytes follow */
    uint32_t flags;
} pvg_execbuffer;

typedef struct pvg_fence { uint32_t context_id, ring_idx; uint64_t fence_id; } pvg_fence;

/* pvg_range.flags (WATCH_RANGE only). A control range holds a word that orders other shared
 * bytes, such as a Venus ring's head, tail or status. Each side reads every control word before
 * the other bytes it is about to send and sends them after those bytes, so a peer that sees a new
 * head (or tail) also sees the data written before it. Ordinary watches skip control ranges. */
#define PVG_RANGE_CONTROL 0x1u

typedef struct pvg_range {
    uint32_t resource_id, flags;
    uint64_t offset;
    uint64_t size;                    /* data bytes follow for WRITE_RANGE / RANGE_DATA */
} pvg_range;

typedef struct pvg_present {
    uint32_t context_id;
    uint32_t resource_id;             /* blob holding the presented image */
    uint32_t width, height;
    uint32_t stride, format;          /* VkFormat */
    uint64_t present_id;
} pvg_present;

typedef struct pvg_stats {
    uint64_t bytes_to_host, bytes_to_guest;
    uint64_t execbuffers, fences;
    uint64_t host_gpu_busy_ns;
} pvg_stats;

#ifdef __cplusplus
}
#endif
#endif /* PVG_WIRE_H */
