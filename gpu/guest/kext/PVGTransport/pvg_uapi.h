/*
 * Pome Virtual GPU: interface between PVGTransport.kext and user space (the Venus renderer and the
 * Metal layer). SPDX-License-Identifier: MIT
 *
 * Open the service with IOServiceOpen(service matching "PVGTransport", type 0). Every call uses
 * IOConnectCallMethod with the selectors below. Mappable blobs are mapped with
 * IOConnectMapMemory64(connection, resource_id, ...): the mapping is the guest copy of a mirrored
 * resource; see pvg_wire.h for when the copies are synchronised.
 */
#ifndef PVG_UAPI_H
#define PVG_UAPI_H
#include <stdint.h>

#define PVG_USERCLIENT_CLASS "PVGTransport"

enum pvg_uc_selector {
    PVG_UC_GET_CAPS = 0,        /* out struct: raw CAPS payload (pvg_caps_header + adapters + capset) */
    PVG_UC_CONTEXT_INIT,        /* in struct pvg_uc_context_init; out scalar: context_id */
    PVG_UC_CONTEXT_DESTROY,     /* in scalar: context_id */
    PVG_UC_RESOURCE_BLOB,       /* in struct pvg_uc_blob; out struct pvg_uc_blob_reply */
    PVG_UC_RESOURCE_UNREF,      /* in scalar: resource_id */
    PVG_UC_WRITE_RANGE,         /* in scalars: resource_id, offset, size (guest copy -> host) */
    PVG_UC_READ_RANGE,          /* in scalars: resource_id, offset, size (host copy -> guest copy) */
    PVG_UC_WATCH_RANGE,         /* in scalars: resource_id, offset, size (host pushes changes) */
    PVG_UC_EXECBUFFER,          /* in struct pvg_uc_exec + commands; out scalar: fence_id */
    PVG_UC_SYNC_CREATE,         /* in scalar: initial value; out scalar: sync_id */
    PVG_UC_SYNC_DESTROY,        /* in scalar: sync_id */
    PVG_UC_SYNC_READ,           /* in scalar: sync_id; out scalar: value */
    PVG_UC_SYNC_WRITE,          /* in scalars: sync_id, value */
    PVG_UC_WAIT,                /* in struct pvg_uc_wait; returns kIOReturnTimeout on timeout */
    PVG_UC_GET_STATS,           /* out struct pvg_uc_stats */
    PVG_UC_FLUSH_MIRRORS,       /* send every byte this client changed in mirrored resources since the last sync */
    PVG_UC_MARK_CONTROL,        /* in scalars: resource_id, offset, size: a control word (PVG_RANGE_CONTROL) of a
                                   mirrored resource, read first and sent last by FLUSH_MIRRORS and by the host */
    PVG_UC_SELECTOR_COUNT
};

typedef struct pvg_uc_context_init { uint32_t capset_id, flags; char name[64]; } pvg_uc_context_init;

/* pvg_uc_blob.mirror: keep a kernel shadow so FLUSH_MIRRORS can send only guest-written bytes.
 * Used for Venus shmems (ring, reply) and HOST_COHERENT memory, where nothing calls flush. */
typedef struct pvg_uc_blob {
    uint32_t context_id, blob_mem, blob_flags, mirror;
    uint64_t blob_id, size;
} pvg_uc_blob;
typedef struct pvg_uc_blob_reply { uint32_t resource_id, map_info; uint64_t size; } pvg_uc_blob_reply;

typedef struct pvg_uc_sync_point { uint32_t sync_id, reserved; uint64_t value; } pvg_uc_sync_point;

#define PVG_UC_MAX_SYNCS 64
typedef struct pvg_uc_exec {
    uint32_t context_id, ring_idx;
    uint32_t sync_count, command_size;           /* commands follow the sync points */
    pvg_uc_sync_point syncs[PVG_UC_MAX_SYNCS];   /* signalled when the ring's fence completes */
} pvg_uc_exec;

typedef struct pvg_uc_wait {
    uint32_t wait_any, sync_count;
    uint64_t timeout_ns;                          /* UINT64_MAX: forever */
    pvg_uc_sync_point syncs[PVG_UC_MAX_SYNCS];
} pvg_uc_wait;

typedef struct pvg_uc_stats {
    uint64_t bytes_tx, bytes_rx, messages_tx, messages_rx, fences_signalled, range_pushes;
    uint32_t connected, negotiated, contexts, resources;
} pvg_uc_stats;

#endif
