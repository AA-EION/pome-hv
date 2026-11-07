/*
 * Pome Virtual GPU: Hyper-V socket transport and guest protocol endpoint.
 * Copyright (c) 2026 pome contributors. SPDX-License-Identifier: MIT
 */
#include "PVGTransport.hpp"
// version_major/version_minor come from Lilu's kern_util.hpp (via HyperVVMBusDevice.hpp); including
// <libkern/version.h> as well redeclares them with C linkage.
#include <kern/thread.h>
#include <mach/kmod.h>

// kmod_info names the kext and its version. Xcode generates it for kext targets; this one is built
// with plain clang, and OpenCore refuses to inject a kext without it ("Invalid Parameter").
// Keep the version equal to CFBundleVersion in Info.plist.
extern "C" kern_return_t _start(kmod_info_t *ki, void *d);
extern "C" kern_return_t _stop(kmod_info_t *ki, void *d);
extern "C" { KMOD_EXPLICIT_DECL(org.pome.PVGTransport, "0.1.0", _start, _stop) }
extern "C" kern_return_t _start(kmod_info_t *, void *) { return KERN_SUCCESS; }
extern "C" kern_return_t _stop(kmod_info_t *, void *) { return KERN_SUCCESS; }

OSDefineMetaClassAndStructors(PVGTransport, IOService);
OSDefineMetaClassAndStructors(PVGUserClient, IOUserClient);

#define PVGLOG(fmt, ...) IOLog("PVGTransport: " fmt "\n", ##__VA_ARGS__)

namespace {
// vmpipe framing used by Hyper-V sockets (hv_sock).
struct VMPipeHeader { uint32_t type; uint32_t size; };
constexpr uint32_t kVMPipeData = 1;
constexpr uint32_t kRingBytes = 64 * 4096;          // 256 KiB each way; newer hosts accept up to 64 pages
constexpr uint32_t kPacketPayload = 16 * 1024 - 64;  // stay below the 16 KiB hv_sock MTU
constexpr uint32_t kRxCapacity = PVG_MAX_MESSAGE + 2 * sizeof(pvg_msg_header);
constexpr uint32_t kRangeChunk = 4u * 1024 * 1024;

inline uint32_t Padded(uint32_t n) { return (n + 7u) & ~7u; }
}

bool PVGTransport::start(IOService *provider) {
    if (PE_parse_boot_argn("-pvgoff", nullptr, 0)) { PVGLOG("disabled by -pvgoff"); return false; }
    if (!super::start(provider)) return false;
    _device = OSDynamicCast(HyperVVMBusDevice, provider);
    if (!_device) { PVGLOG("provider is not a VMBus device"); return false; }
    _device->retain();
    _lock = IOLockAlloc(); _sendLock = IOLockAlloc(); _flushLock = IOLockAlloc();
    _controlSnaps = static_cast<ControlSnap *>(IOMallocData(kMaxControlSnaps * sizeof(ControlSnap)));
    _rxCapacity = kRxCapacity;
    _rxBuffer = static_cast<uint8_t *>(IOMallocData(_rxCapacity));
    _txPacket = static_cast<uint8_t *>(IOMallocData(sizeof(VMPipeHeader) + kPacketPayload));
    if (!_lock || !_sendLock || !_flushLock || !_controlSnaps || !_rxBuffer || !_txPacket) { PVGLOG("allocation failed"); return false; }

    if (_device->installPacketActions(this, &PVGTransport::packetReady, nullptr, 32 * 1024) != kIOReturnSuccess ||
        _device->openVMBusChannel(kRingBytes, kRingBytes) != kIOReturnSuccess) {
        PVGLOG("could not open the Hyper-V socket channel");
        return false;
    }
    _connected = true;
    setProperty("PVGProtocol", PVG_PROTOCOL_MAJOR * 100 + PVG_PROTOCOL_MINOR, 32);
    setProperty("PVGService", PVG_SERVICE_GUID_STRING);
    PVGLOG("channel open (service %s)", PVG_SERVICE_GUID_STRING);

    // Negotiation waits for replies, so it must not run on the VMBus work loop thread.
    retain();
    thread_t thread;
    if (kernel_thread_start([](void *self, wait_result_t) {
            auto *me = static_cast<PVGTransport *>(self);
            if (me->negotiate() == kIOReturnSuccess) { me->publishCaps(); me->registerService(); }
            me->release();
            thread_terminate(current_thread());
        }, this, &thread) == KERN_SUCCESS) {
        thread_deallocate(thread);
    } else {
        release();
        return false;
    }
    return true;
}

void PVGTransport::stop(IOService *provider) {
    IOLockLock(_lock);
    _connected = false;
    for (auto &r : _replies) if (r.used && !r.done) { r.done = true; r.error = kIOReturnNotAttached; }
    IOLockWakeup(_lock, _replies, false);
    IOLockWakeup(_lock, _syncs, false);
    IOLockUnlock(_lock);
    if (_device) { _device->uninstallPacketActions(); _device->closeVMBusChannel(); }
    super::stop(provider);
}

void PVGTransport::free() {
    for (auto &r : _resources) { if (r.memory) r.memory->release(); if (r.synced) r.synced->release(); r = Resource{}; }
    if (_caps) IOFreeData(_caps, _capsLength);
    if (_rxBuffer) IOFreeData(_rxBuffer, _rxCapacity);
    if (_txPacket) IOFreeData(_txPacket, sizeof(VMPipeHeader) + kPacketPayload);
    if (_lock) IOLockFree(_lock);
    if (_sendLock) IOLockFree(_sendLock);
    if (_flushLock) IOLockFree(_flushLock);
    if (_controlSnaps) IOFreeData(_controlSnaps, kMaxControlSnaps * sizeof(ControlSnap));
    if (_device) _device->release();
    super::free();
}

// ---------------------------------------------------------------- receive path

void PVGTransport::packetReady(void *target, VMBusPacketHeader *, UInt32, UInt8 *data, UInt32 dataLength) {
    auto *me = static_cast<PVGTransport *>(target);
    if (dataLength < sizeof(VMPipeHeader)) return;
    VMPipeHeader pipe; memcpy(&pipe, data, sizeof pipe);
    if (pipe.type != kVMPipeData || pipe.size > dataLength - sizeof pipe) return;
    if (pipe.size == 0) {   // EOF from pvg-host
        PVGLOG("host closed the stream");
        IOLockLock(me->_lock); me->_connected = false; IOLockWakeup(me->_lock, me->_replies, false); IOLockUnlock(me->_lock);
        return;
    }
    me->_stats.bytes_rx += pipe.size;
    me->receiveBytes(data + sizeof pipe, pipe.size);
}

void PVGTransport::receiveBytes(const uint8_t *bytes, uint32_t length) {
    while (length) {
        const uint32_t space = _rxCapacity - _rxLength;
        const uint32_t take = length < space ? length : space;
        memcpy(_rxBuffer + _rxLength, bytes, take);
        _rxLength += take; bytes += take; length -= take;
        uint32_t consumed = 0;
        for (;;) {
            if (_rxLength - consumed < sizeof(pvg_msg_header)) break;
            pvg_msg_header h; memcpy(&h, _rxBuffer + consumed, sizeof h);
            if (h.magic != PVG_MAGIC || h.length > PVG_MAX_MESSAGE) {
                PVGLOG("protocol error: bad header, dropping %u buffered bytes", _rxLength - consumed);
                consumed = _rxLength; break;
            }
            const uint32_t total = sizeof h + Padded(h.length);
            if (_rxLength - consumed < total) break;
            _stats.messages_rx++;
            dispatchMessage(h, _rxBuffer + consumed + sizeof h);
            consumed += total;
        }
        if (consumed) { memmove(_rxBuffer, _rxBuffer + consumed, _rxLength - consumed); _rxLength -= consumed; }
        if (take == 0 && length) { PVGLOG("receive buffer overflow"); _rxLength = 0; }
    }
}

void PVGTransport::dispatchMessage(const pvg_msg_header &h, const uint8_t *payload) {
    const uint16_t base = h.type & ~PVG_MSG_REPLY;
    IOLockLock(_lock);
    if (base == PVG_MSG_RANGE_DATA && h.length >= sizeof(pvg_range)) {
        pvg_range r; memcpy(&r, payload, sizeof r);
        if (Resource *res = findResource(r.resource_id)) {
            if (res->memory && r.offset + r.size <= res->size && sizeof r + r.size <= h.length) {
                memcpy(static_cast<uint8_t *>(res->memory->getBytesNoCopy()) + r.offset, payload + sizeof r, static_cast<size_t>(r.size));
                if (res->synced)   // the guest now agrees with these bytes: never echo them back
                    memcpy(static_cast<uint8_t *>(res->synced->getBytesNoCopy()) + r.offset, payload + sizeof r, static_cast<size_t>(r.size));
                if (!(h.type & PVG_MSG_REPLY)) _stats.range_pushes++;
            }
        }
    } else if (base == PVG_MSG_FENCE_SIGNALED && h.length >= sizeof(pvg_fence)) {
        pvg_fence f; memcpy(&f, payload, sizeof f);
        _stats.fences_signalled++;
        for (auto &p : _pending) {
            if (!p.used || p.contextId != f.context_id || p.ringIdx != f.ring_idx || p.fenceId > f.fence_id) continue;
            for (uint32_t i = 0; i < p.count; ++i)
                if (Sync *s = findSync(p.points[i].sync_id)) if (s->value < p.points[i].value) s->value = p.points[i].value;
            p.used = false;
        }
        IOLockWakeup(_lock, _syncs, false);
    } else if (base == PVG_MSG_ERROR && h.length >= sizeof(pvg_error) && !(h.type & PVG_MSG_REPLY && h.sequence)) {
        pvg_error e; memcpy(&e, payload, sizeof e); e.message[sizeof e.message - 1] = 0;
        PVGLOG("host error %d for 0x%04x: %s", e.code, e.request_type, e.message);
    }
    if ((h.type & PVG_MSG_REPLY) && h.sequence) {
        for (auto &r : _replies) {
            if (!r.used || r.done || r.sequence != h.sequence) continue;
            if (base == PVG_MSG_ERROR && h.length >= sizeof(pvg_error)) {
                pvg_error e; memcpy(&e, payload, sizeof e); e.message[sizeof e.message - 1] = 0;
                r.error = e.code ? e.code : -1;
                PVGLOG("request 0x%04x refused (%d): %s", e.request_type, e.code, e.message);
            } else {
                r.length = h.length < r.capacity ? h.length : r.capacity;
                if (r.buffer && r.length) memcpy(r.buffer, payload, r.length);
                r.type = h.type;
            }
            r.done = true;
            IOLockWakeup(_lock, _replies, false);
            break;
        }
    }
    IOLockUnlock(_lock);
}

// ---------------------------------------------------------------- send path

IOReturn PVGTransport::sendMessage(uint16_t type, uint16_t flags, uint32_t sequence, const void *payload, uint32_t size,
                                   const void *tail, uint32_t tailSize) {
    if (!_connected) return kIOReturnNotAttached;
    const uint32_t total = size + tailSize;
    if (total > PVG_MAX_MESSAGE) return kIOReturnBadArgument;
    pvg_msg_header h{PVG_MAGIC, type, flags, total, sequence};
    static const uint8_t zeros[8] = {};
    const struct { const void *p; uint32_t n; } parts[] = {{&h, sizeof h}, {payload, size}, {tail, tailSize}, {zeros, Padded(total) - total}};
    IOLockLock(_sendLock);
    IOReturn status = kIOReturnSuccess;
    uint32_t fill = 0;
    auto flush = [&]() -> IOReturn {
        if (!fill) return kIOReturnSuccess;
        VMPipeHeader pipe{kVMPipeData, fill};
        memcpy(_txPacket, &pipe, sizeof pipe);
        IOReturn r = _device->writeInbandPacket(_txPacket, sizeof pipe + fill, false);
        _stats.bytes_tx += fill; fill = 0;
        return r;
    };
    for (const auto &part : parts) {
        const uint8_t *p = static_cast<const uint8_t *>(part.p); uint32_t n = part.n;
        while (n && status == kIOReturnSuccess) {
            const uint32_t room = kPacketPayload - fill, chunk = n < room ? n : room;
            memcpy(_txPacket + sizeof(VMPipeHeader) + fill, p, chunk);
            fill += chunk; p += chunk; n -= chunk;
            if (fill == kPacketPayload) status = flush();
        }
    }
    if (status == kIOReturnSuccess) status = flush();
    if (status == kIOReturnSuccess) _stats.messages_tx++;
    IOLockUnlock(_sendLock);
    return status;
}

IOReturn PVGTransport::request(uint16_t type, const void *payload, uint32_t size, void *reply, uint32_t replyCapacity,
                               uint32_t *replyLength, const void *tail, uint32_t tailSize, uint32_t timeoutMs) {
    IOLockLock(_lock);
    Reply *slot = nullptr;
    for (auto &r : _replies) if (!r.used) { slot = &r; break; }
    if (!slot) { IOLockUnlock(_lock); return kIOReturnNoResources; }
    *slot = Reply{true, false, _nextSequence++, 0, reply, replyCapacity, 0, 0};
    if (!_nextSequence) _nextSequence = 1;
    const uint32_t sequence = slot->sequence;
    IOLockUnlock(_lock);

    IOReturn status = sendMessage(type, PVG_FLAG_NEEDS_REPLY, sequence, payload, size, tail, tailSize);
    IOLockLock(_lock);
    if (status == kIOReturnSuccess) {
        uint64_t deadline; clock_interval_to_deadline(timeoutMs, kMillisecondScale, &deadline);
        while (!slot->done && _connected)
            if (IOLockSleepDeadline(_lock, _replies, deadline, THREAD_INTERRUPTIBLE) == THREAD_TIMED_OUT) break;
        if (!slot->done) status = _connected ? kIOReturnTimeout : kIOReturnNotAttached;
        else if (slot->error) status = kIOReturnError;
        if (replyLength) *replyLength = slot->length;
    }
    slot->used = false;
    IOLockUnlock(_lock);
    return status;
}

IOReturn PVGTransport::negotiate() {
    pvg_hello hello{};
    hello.protocol_major = PVG_PROTOCOL_MAJOR; hello.protocol_minor = PVG_PROTOCOL_MINOR;
    hello.guest_page_size = PVG_PAGE_SIZE;
    hello.guest_os_build = (uint32_t(version_major) << 16) | (uint32_t(version_minor) << 8);
    hello.guest_driver_version = 0x00000100;
    hello.guest_features = PVG_GUEST_METAL_PLUGIN | PVG_GUEST_VENUS;
    strlcpy(hello.guest_name, "PVGTransport (macOS)", sizeof hello.guest_name);
    const uint32_t capacity = 64 * 1024;
    auto *buffer = static_cast<uint8_t *>(IOMallocData(capacity));
    if (!buffer) return kIOReturnNoMemory;
    uint32_t length = 0;
    // pvg-host may still be probing GPUs; allow it a generous first reply.
    IOReturn status = request(PVG_MSG_HELLO, &hello, sizeof hello, buffer, capacity, &length, nullptr, 0, 30000);
    if (status != kIOReturnSuccess || length < sizeof(pvg_caps_header)) {
        PVGLOG("capability negotiation failed (0x%x, %u bytes)", status, length);
        IOFreeData(buffer, capacity);
        return status ? status : kIOReturnUnderrun;
    }
    IOLockLock(_lock);
    _caps = static_cast<uint8_t *>(IOMallocData(length));
    if (_caps) { memcpy(_caps, buffer, length); _capsLength = length; _negotiated = true; }
    IOLockUnlock(_lock);
    IOFreeData(buffer, capacity);
    return _caps ? kIOReturnSuccess : kIOReturnNoMemory;
}

// Publishes the host's real capabilities so the accelerator and the Metal layer advertise only
// what the selected host GPU can execute.
void PVGTransport::publishCaps() {
    pvg_caps_header h; memcpy(&h, _caps, sizeof h);
    const uint32_t need = sizeof h + h.adapter_count * sizeof(pvg_adapter_caps) + h.venus_capset_size;
    if (need > _capsLength || h.adapter_count > 16) { PVGLOG("malformed CAPS (%u > %u)", need, _capsLength); return; }
    OSArray *adapters = OSArray::withCapacity(h.adapter_count);
    for (uint32_t i = 0; i < h.adapter_count && adapters; ++i) {
        pvg_adapter_caps a; memcpy(&a, _caps + sizeof h + i * sizeof a, sizeof a);
        a.device_name[sizeof a.device_name - 1] = 0; a.driver_name[sizeof a.driver_name - 1] = 0;
        OSDictionary *d = OSDictionary::withCapacity(12);
        if (!d) continue;
        auto num = [&](const char *key, uint64_t v, int bits) { if (OSNumber *n = OSNumber::withNumber(v, bits)) { d->setObject(key, n); n->release(); } };
        auto str = [&](const char *key, const char *v) { if (OSString *s = OSString::withCString(v)) { d->setObject(key, s); s->release(); } };
        str("Name", a.device_name); str("Driver", a.driver_name);
        num("VendorID", a.vendor_id, 32); num("DeviceID", a.device_id, 32); num("VulkanAPI", a.api_version, 32);
        num("DeviceType", a.device_type, 32); num("Features", a.features, 64); num("MetalClasses", a.metal, 32);
        num("DeviceLocalBytes", a.device_local_bytes, 64); num("SubgroupSize", a.subgroup_size, 32);
        num("MaxImageDimension2D", a.max_image_dimension_2d, 32); num("MaxRayRecursionDepth", a.max_ray_recursion_depth, 32);
        if (OSData *uuid = OSData::withBytes(a.device_uuid, sizeof a.device_uuid)) { d->setObject("DeviceUUID", uuid); uuid->release(); }
        // Venus shows the guest every host GPU; the Metal layer opens the one whose UUID is preferred.
        if (i == h.preferred_adapter)
            if (OSData *uuid = OSData::withBytes(a.device_uuid, sizeof a.device_uuid)) { setProperty("PVGPreferredDeviceUUID", uuid); uuid->release(); }
        adapters->setObject(d); d->release();
    }
    if (adapters) { setProperty("PVGAdapters", adapters); adapters->release(); }
    h.host_renderer[sizeof h.host_renderer - 1] = 0;
    setProperty("PVGHostRenderer", h.host_renderer);
    setProperty("PVGHostFlags", h.host_flags, 32);
    setProperty("PVGHostBuild", h.host_build, 32);
    setProperty("PVGPreferredAdapter", h.preferred_adapter, 32);
    setProperty("PVGVenusAvailable", (h.host_flags & PVG_HOST_VENUS_AVAILABLE) ? kOSBooleanTrue : kOSBooleanFalse);
    if (h.venus_capset_size)
        if (OSData *capset = OSData::withBytes(_caps + sizeof h + h.adapter_count * sizeof(pvg_adapter_caps), h.venus_capset_size)) {
            setProperty("PVGVenusCapset", capset); capset->release();
        }
    PVGLOG("host '%s': %u adapter(s), preferred %u, Venus %s", h.host_renderer, h.adapter_count, h.preferred_adapter,
           (h.host_flags & PVG_HOST_VENUS_AVAILABLE) ? "available" : "unavailable");
}

// ---------------------------------------------------------------- user client operations

PVGTransport::Resource *PVGTransport::findResource(uint32_t id) {
    for (auto &r : _resources) if (r.id == id && id) return &r;
    return nullptr;
}
PVGTransport::Sync *PVGTransport::findSync(uint32_t id) {
    for (auto &s : _syncs) if (s.used && s.id == id) return &s;
    return nullptr;
}

IOReturn PVGTransport::getCaps(void *out, uint32_t *size) {
    IOLockLock(_lock);
    IOReturn r = kIOReturnNotReady;
    if (_negotiated) {
        if (*size < _capsLength) { *size = _capsLength; r = kIOReturnNoSpace; }
        else { memcpy(out, _caps, _capsLength); *size = _capsLength; r = kIOReturnSuccess; }
    }
    IOLockUnlock(_lock);
    return r;
}

IOReturn PVGTransport::contextInit(const pvg_uc_context_init *in, uint32_t *contextId, task_t owner) {
    IOLockLock(_lock);
    uint32_t id = 0;
    for (uint32_t i = 1; i < kMaxContexts; ++i) if (!_contexts[i]) { id = i; _contexts[i] = true; _contextOwner[i] = owner; break; }
    IOLockUnlock(_lock);
    if (!id) return kIOReturnNoResources;
    pvg_context_init c{}; c.context_id = id; c.capset_id = in->capset_id; c.flags = in->flags;
    memcpy(c.debug_name, in->name, sizeof c.debug_name); c.debug_name[sizeof c.debug_name - 1] = 0;
    int32_t result = -1;
    IOReturn r = request(PVG_MSG_CONTEXT_INIT, &c, sizeof c, &result, sizeof result, nullptr);
    if (r != kIOReturnSuccess || result != 0) {
        // The host may still have created it (a timed-out or interrupted wait): tell it to let go
        // before the id is handed out again.
        if (r != kIOReturnSuccess) { pvg_context_ref ref{id, 0}; sendMessage(PVG_MSG_CONTEXT_DESTROY, 0, 0, &ref, sizeof ref); }
        IOLockLock(_lock); _contexts[id] = false; _contextOwner[id] = nullptr; IOLockUnlock(_lock);
        return r ? r : kIOReturnError;
    }
    *contextId = id;
    return kIOReturnSuccess;
}

IOReturn PVGTransport::contextDestroy(uint32_t contextId) {
    if (!contextId || contextId >= kMaxContexts) return kIOReturnBadArgument;
    pvg_context_ref c{contextId, 0};
    sendMessage(PVG_MSG_CONTEXT_DESTROY, 0, 0, &c, sizeof c);
    IOLockLock(_lock);
    _contexts[contextId] = false; _contextOwner[contextId] = nullptr; _nextFence[contextId] = 0;
    for (auto &res : _resources) if (res.id && res.contextId == contextId) {
        if (res.memory) res.memory->release();
        if (res.synced) res.synced->release();
        res = Resource{};
    }
    for (auto &p : _pending) if (p.used && p.contextId == contextId) p.used = false;
    IOLockUnlock(_lock);
    return kIOReturnSuccess;
}

IOReturn PVGTransport::createBlob(const pvg_uc_blob *in, pvg_uc_blob_reply *out, task_t owner) {
    if (!in->size || in->size > (16ull << 30)) return kIOReturnBadArgument;
    IOLockLock(_lock);
    Resource *slot = nullptr;
    for (auto &r : _resources) if (!r.id) { slot = &r; break; }
    if (!slot) { IOLockUnlock(_lock); return kIOReturnNoResources; }
    const uint32_t id = _nextResource++;
    *slot = Resource{id, in->context_id, in->size, nullptr, owner, nullptr};
    IOLockUnlock(_lock);

    pvg_resource_blob b{in->context_id, id, in->blob_mem, in->blob_flags, in->blob_id, in->size};
    pvg_resource_reply reply{-1, 0, 0};
    IOReturn r = request(PVG_MSG_RESOURCE_BLOB, &b, sizeof b, &reply, sizeof reply, nullptr);
    IOBufferMemoryDescriptor *memory = nullptr, *synced = nullptr;
    if (r == kIOReturnSuccess && reply.result == 0 && in->mirror && in->size <= (512ull << 20)) {
        synced = IOBufferMemoryDescriptor::inTaskWithOptions(kernel_task, kIODirectionInOut, static_cast<vm_size_t>(in->size), PAGE_SIZE);
        if (synced) bzero(synced->getBytesNoCopy(), static_cast<size_t>(in->size)); else r = kIOReturnNoMemory;
    }
    if (r == kIOReturnSuccess && reply.result == 0 && (in->blob_flags & 1 /* mappable */ || in->blob_mem == 1 /* guest */)) {
        // The guest copy of the mirrored resource; user space maps these pages directly.
        memory = IOBufferMemoryDescriptor::inTaskWithOptions(kernel_task, kIODirectionInOut | kIOMemoryKernelUserShared,
                                                              static_cast<vm_size_t>(in->size), PAGE_SIZE);
        if (memory) bzero(memory->getBytesNoCopy(), static_cast<size_t>(in->size));
        else r = kIOReturnNoMemory;
    }
    IOLockLock(_lock);
    if (r != kIOReturnSuccess || reply.result != 0) {
        *slot = Resource{}; IOLockUnlock(_lock);
        if (memory) memory->release();
        if (synced) synced->release();
        return r ? r : kIOReturnError;
    }
    slot->memory = memory; slot->synced = synced;
    IOLockUnlock(_lock);
    // A new resource is zero on both sides (bzero above; the host's shmems are fresh mappings), so
    // it is not read back here: that sent every shmem of every process twice and flooded the stream.
    // Device memory is read when Venus maps it (pvg_bo_map), and host writes travel as watch pushes.
    out->resource_id = id; out->map_info = reply.map_info; out->size = in->size;
    return kIOReturnSuccess;
}

IOReturn PVGTransport::unrefBlob(uint32_t resourceId) {
    IOLockLock(_lock);
    Resource *r = findResource(resourceId);
    IOBufferMemoryDescriptor *memory = r ? r->memory : nullptr, *synced = r ? r->synced : nullptr;
    if (r) *r = Resource{};
    IOLockUnlock(_lock);
    if (!r) return kIOReturnNotFound;
    pvg_resource_ref ref{resourceId, 0};
    sendMessage(PVG_MSG_RESOURCE_UNREF, 0, 0, &ref, sizeof ref);
    if (memory) memory->release();
    if (synced) synced->release();
    return kIOReturnSuccess;
}

IOReturn PVGTransport::writeRange(uint32_t resourceId, uint64_t offset, uint64_t size) {
    IOLockLock(_lock);
    Resource *r = findResource(resourceId);
    IOBufferMemoryDescriptor *memory = r ? r->memory : nullptr, *synced = r ? r->synced : nullptr;
    const uint64_t limit = r ? r->size : 0;
    if (memory) memory->retain();
    if (synced) synced->retain();
    IOLockUnlock(_lock);
    if (!memory) return kIOReturnNotFound;
    IOReturn status = (offset + size <= limit) ? kIOReturnSuccess : kIOReturnBadArgument;
    for (uint64_t done = 0; status == kIOReturnSuccess && done < size; done += kRangeChunk) {
        const uint64_t n = (size - done) < kRangeChunk ? (size - done) : kRangeChunk;
        pvg_range rg{resourceId, 0, offset + done, n};
        status = sendMessage(PVG_MSG_WRITE_RANGE, 0, 0, &rg, sizeof rg,
                             static_cast<uint8_t *>(memory->getBytesNoCopy()) + offset + done, static_cast<uint32_t>(n));
    }
    if (synced) {
        if (status == kIOReturnSuccess)
            memcpy(static_cast<uint8_t *>(synced->getBytesNoCopy()) + offset, static_cast<uint8_t *>(memory->getBytesNoCopy()) + offset, static_cast<size_t>(size));
        synced->release();
    }
    memory->release();
    return status;
}

// Sends the 64-byte granules where the guest copy differs from the agreed copy, coalesced into spans.
// Each byte of a Venus shmem has one writer (guest: commands, tail; host: head, status, replies), so
// bytes the host pushed are already in the agreed copy and are never sent back.
IOReturn PVGTransport::sendDiff(uint32_t resourceId, const uint8_t *current, uint8_t *synced, uint64_t size,
                               uint32_t controlCount, const uint32_t *controlOffset) {
    constexpr uint64_t granule = 64;
    // Control words travel last, from their snapshot (flushMirrors): skip their granules here.
    auto isControl = [&](uint64_t at) {
        for (uint32_t c = 0; c < controlCount; ++c) if (controlOffset[c] / granule == at / granule) return true;
        return false;
    };
    IOReturn status = kIOReturnSuccess;
    uint64_t pos = 0;
    while (pos < size && status == kIOReturnSuccess) {
        const uint64_t len = (size - pos) < granule ? (size - pos) : granule;
        if (isControl(pos) || !memcmp(current + pos, synced + pos, static_cast<size_t>(len))) { pos += len; continue; }
        uint64_t end = pos + len;
        while (end < size && end - pos < kRangeChunk && !isControl(end)) {
            const uint64_t l2 = (size - end) < granule ? (size - end) : granule;
            if (!memcmp(current + end, synced + end, static_cast<size_t>(l2))) break;
            end += l2;
        }
        memcpy(synced + pos, current + pos, static_cast<size_t>(end - pos));
        pvg_range rg{resourceId, 0, pos, end - pos};
        status = sendMessage(PVG_MSG_WRITE_RANGE, 0, 0, &rg, sizeof rg, synced + pos, static_cast<uint32_t>(end - pos));
        pos = end;
    }
    return status;
}

IOReturn PVGTransport::flushMirrors(task_t owner) {
    // Venus calls this before every submit and while it waits on a ring, so the host sees ring
    // contents and coherent writes. Order matters: a ring tail must never reach the host ahead of the
    // commands it covers. Control words are therefore read first (snapshot), everything else is sent,
    // and the snapshots go last. A command written after the snapshot simply waits for the next flush.
    IOLockLock(_flushLock);
    uint32_t snaps = 0;
    IOLockLock(_lock);
    for (uint32_t i = 0; i < kMaxResources; ++i) {
        const Resource &r = _resources[i];
        if (!r.id || r.owner != owner || !r.synced || !r.memory) continue;
        const auto *bytes = static_cast<const uint8_t *>(r.memory->getBytesNoCopy());
        for (uint32_t c = 0; c < r.controlCount && snaps < kMaxControlSnaps; ++c) {
            ControlSnap &s = _controlSnaps[snaps++];
            s.resourceId = r.id; s.offset = r.controlOffset[c]; s.size = r.controlSize[c];
            memcpy(s.bytes, bytes + s.offset, s.size);
        }
    }
    IOLockUnlock(_lock);
    __atomic_thread_fence(__ATOMIC_SEQ_CST);   // the snapshot is ordered before the data reads below

    IOReturn status = kIOReturnSuccess;
    for (uint32_t i = 0; i < kMaxResources && status == kIOReturnSuccess; ++i) {
        IOLockLock(_lock);
        Resource *r = &_resources[i];
        if (!r->id || r->owner != owner || !r->synced || !r->memory) { IOLockUnlock(_lock); continue; }
        const uint32_t id = r->id; const uint64_t size = r->size;
        const uint32_t controls = r->controlCount; uint32_t controlOffset[kMaxControls];
        memcpy(controlOffset, r->controlOffset, sizeof controlOffset);
        IOBufferMemoryDescriptor *memory = r->memory, *synced = r->synced;
        memory->retain(); synced->retain();
        IOLockUnlock(_lock);
        status = sendDiff(id, static_cast<const uint8_t *>(memory->getBytesNoCopy()), static_cast<uint8_t *>(synced->getBytesNoCopy()), size,
                          controls, controlOffset);
        memory->release(); synced->release();
    }

    for (uint32_t k = 0; k < snaps && status == kIOReturnSuccess; ++k) {
        const ControlSnap &s = _controlSnaps[k];
        IOLockLock(_lock);
        Resource *r = findResource(s.resourceId);
        IOBufferMemoryDescriptor *synced = (r && r->synced) ? r->synced : nullptr;
        if (synced) synced->retain();
        IOLockUnlock(_lock);
        if (!synced) continue;
        auto *agreed = static_cast<uint8_t *>(synced->getBytesNoCopy()) + s.offset;
        if (memcmp(agreed, s.bytes, s.size)) {
            memcpy(agreed, s.bytes, s.size);
            pvg_range rg{s.resourceId, 0, s.offset, s.size};
            status = sendMessage(PVG_MSG_WRITE_RANGE, 0, 0, &rg, sizeof rg, s.bytes, s.size);
        }
        synced->release();
    }
    IOLockUnlock(_flushLock);
    return status;
}

IOReturn PVGTransport::readRange(uint32_t resourceId, uint64_t offset, uint64_t size) {
    IOReturn status = kIOReturnSuccess;
    for (uint64_t done = 0; status == kIOReturnSuccess && done < size; done += kRangeChunk) {
        const uint64_t n = (size - done) < kRangeChunk ? (size - done) : kRangeChunk;
        pvg_range rg{resourceId, 0, offset + done, n};
        // RANGE_DATA replies are copied into the guest copy by dispatchMessage.
        status = request(PVG_MSG_READ_RANGE, &rg, sizeof rg, nullptr, 0, nullptr);
    }
    return status;
}

IOReturn PVGTransport::watchRange(uint32_t resourceId, uint64_t offset, uint64_t size) {
    pvg_range rg{resourceId, 0, offset, size};
    return sendMessage(PVG_MSG_WATCH_RANGE, 0, 0, &rg, sizeof rg);
}

IOReturn PVGTransport::markControl(uint32_t resourceId, uint64_t offset, uint64_t size, task_t owner) {
    if (!size || size > kMaxControlSize) return kIOReturnBadArgument;
    IOLockLock(_lock);
    Resource *r = findResource(resourceId);
    IOReturn status = kIOReturnSuccess;
    if (!r || r->owner != owner || !r->synced) status = kIOReturnNotFound;
    else if (offset + size > r->size || offset / 64 != (offset + size - 1) / 64) status = kIOReturnBadArgument;
    else if ([&] { for (uint32_t c = 0; c < r->controlCount; ++c) if (r->controlOffset[c] == offset) return true; return false; }()) {
        IOLockUnlock(_lock); return kIOReturnSuccess;   // a cached shmem reused for a new ring
    }
    else if (r->controlCount >= kMaxControls) status = kIOReturnNoResources;
    else {
        r->controlOffset[r->controlCount] = static_cast<uint32_t>(offset);
        r->controlSize[r->controlCount] = static_cast<uint8_t>(size);
        r->controlCount++;
    }
    IOLockUnlock(_lock);
    if (status != kIOReturnSuccess) return status;
    // The host orders its pushes the same way: control words read first and sent last.
    pvg_range rg{resourceId, PVG_RANGE_CONTROL, offset, size};
    return sendMessage(PVG_MSG_WATCH_RANGE, 0, 0, &rg, sizeof rg);
}

IOReturn PVGTransport::execbuffer(const pvg_uc_exec *in, const void *commands, uint64_t *fenceId) {
    if (!in->context_id || in->context_id >= kMaxContexts || in->sync_count > PVG_UC_MAX_SYNCS || (in->command_size & 3))
        return kIOReturnBadArgument;
    IOLockLock(_lock);
    const uint64_t fence = ++_nextFence[in->context_id];
    if (in->sync_count) {
        PendingSignal *p = nullptr;
        for (auto &s : _pending) if (!s.used) { p = &s; break; }
        if (!p) { IOLockUnlock(_lock); return kIOReturnNoResources; }
        p->used = true; p->contextId = in->context_id; p->ringIdx = in->ring_idx; p->fenceId = fence; p->count = in->sync_count;
        memcpy(p->points, in->syncs, in->sync_count * sizeof(pvg_uc_sync_point));
    }
    IOLockUnlock(_lock);
    pvg_execbuffer e{in->context_id, in->ring_idx, fence, in->command_size, 0};
    IOReturn r = sendMessage(PVG_MSG_EXECBUFFER, 0, 0, &e, sizeof e, commands, in->command_size);
    *fenceId = fence;
    return r;
}

IOReturn PVGTransport::syncCreate(uint64_t initial, uint32_t *syncId) {
    IOLockLock(_lock);
    for (auto &s : _syncs) if (!s.used) {
        s.used = true; s.id = _nextSync++; s.value = initial; *syncId = s.id;
        IOLockUnlock(_lock); return kIOReturnSuccess;
    }
    IOLockUnlock(_lock);
    return kIOReturnNoResources;
}
IOReturn PVGTransport::syncDestroy(uint32_t syncId) {
    IOLockLock(_lock); Sync *s = findSync(syncId); if (s) s->used = false; IOLockUnlock(_lock);
    return s ? kIOReturnSuccess : kIOReturnNotFound;
}
IOReturn PVGTransport::syncRead(uint32_t syncId, uint64_t *value) {
    IOLockLock(_lock); Sync *s = findSync(syncId); if (s) *value = s->value; IOLockUnlock(_lock);
    return s ? kIOReturnSuccess : kIOReturnNotFound;
}
IOReturn PVGTransport::syncWrite(uint32_t syncId, uint64_t value) {
    IOLockLock(_lock); Sync *s = findSync(syncId); if (s) { s->value = value; IOLockWakeup(_lock, _syncs, false); } IOLockUnlock(_lock);
    return s ? kIOReturnSuccess : kIOReturnNotFound;
}

bool PVGTransport::syncsReached(const pvg_uc_wait *in) {
    uint32_t reached = 0;
    for (uint32_t i = 0; i < in->sync_count; ++i) {
        Sync *s = findSync(in->syncs[i].sync_id);
        if (!s || s->value >= in->syncs[i].value) reached++;   // a destroyed sync no longer blocks
    }
    return in->wait_any ? reached > 0 : reached == in->sync_count;
}

IOReturn PVGTransport::wait(const pvg_uc_wait *in) {
    if (in->sync_count > PVG_UC_MAX_SYNCS) return kIOReturnBadArgument;
    // The host may hold a fence until it is asked to poll; nudge it once before sleeping.
    sendMessage(PVG_MSG_WAIT, 0, 0, nullptr, 0);
    IOLockLock(_lock);
    uint64_t deadline = 0;
    if (in->timeout_ns != UINT64_MAX) nanoseconds_to_absolutetime(in->timeout_ns, &deadline), deadline += mach_absolute_time();
    IOReturn status = kIOReturnSuccess;
    while (!syncsReached(in)) {
        if (!_connected) { status = kIOReturnNotAttached; break; }
        if (in->timeout_ns == 0) { status = kIOReturnTimeout; break; }
        const int w = deadline ? IOLockSleepDeadline(_lock, _syncs, deadline, THREAD_INTERRUPTIBLE) : IOLockSleep(_lock, _syncs, THREAD_INTERRUPTIBLE);
        if (w == THREAD_TIMED_OUT) { status = syncsReached(in) ? kIOReturnSuccess : kIOReturnTimeout; break; }
        if (w == THREAD_INTERRUPTED) { status = kIOReturnAborted; break; }
    }
    IOLockUnlock(_lock);
    return status;
}

void PVGTransport::getStats(pvg_uc_stats *out) {
    IOLockLock(_lock);
    *out = _stats;
    out->connected = _connected; out->negotiated = _negotiated;
    for (bool c : _contexts) out->contexts += c;
    for (auto &r : _resources) out->resources += r.id != 0;
    IOLockUnlock(_lock);
}

IOMemoryDescriptor *PVGTransport::mappingForResource(uint32_t resourceId) {
    IOLockLock(_lock);
    Resource *r = findResource(resourceId);
    IOMemoryDescriptor *m = (r && r->memory) ? r->memory : nullptr;
    if (m) m->retain();
    IOLockUnlock(_lock);
    return m;
}

void PVGTransport::releaseOwner(task_t owner) {
    // A crashed process must not leak host GPU memory: free everything it created.
    auto *ids = static_cast<uint32_t *>(IOMallocData(kMaxResources * sizeof(uint32_t)));   // too large for the kernel stack
    if (!ids) return;
    uint32_t n = 0;
    IOLockLock(_lock);
    for (auto &r : _resources) if (r.id && r.owner == owner) ids[n++] = r.id;
    IOLockUnlock(_lock);
    for (uint32_t i = 0; i < n; ++i) unrefBlob(ids[i]);
    IOFreeData(ids, kMaxResources * sizeof(uint32_t));
    // Contexts too: a process that exits without vkDestroyInstance would otherwise keep its host
    // context, and the next process given the same id would be refused.
    uint32_t contexts[kMaxContexts]; uint32_t m = 0;   // 256 bytes: fine on the kernel stack
    IOLockLock(_lock);
    for (uint32_t i = 1; i < kMaxContexts; ++i) if (_contexts[i] && _contextOwner[i] == owner) contexts[m++] = i;
    IOLockUnlock(_lock);
    for (uint32_t i = 0; i < m; ++i) contextDestroy(contexts[i]);
}
