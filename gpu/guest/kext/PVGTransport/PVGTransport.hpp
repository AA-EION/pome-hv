/*
 * Pome Virtual GPU: Hyper-V socket transport and guest protocol endpoint.
 * Copyright (c) 2026 pome contributors. SPDX-License-Identifier: MIT
 *
 * pvg-host on Windows connects to the VM with AF_HYPERV and service id PVG_SERVICE_GUID_STRING.
 * Hyper-V turns that into a pipe-mode VMBus offer whose type GUID is the service id; MacHyperVSupport
 * publishes it as a HyperVVMBusDevice nub and this driver matches it by HVType. Each VMBus inband
 * packet carries a vmpipe header {type = 1 (data), size} then stream bytes, as in Linux hv_sock
 * (net/vmw_vsock/hyperv_transport.c) and FreeBSD hvsock. A zero-length data packet is EOF.
 */
#ifndef PVGTransport_hpp
#define PVGTransport_hpp

#include <IOKit/IOService.h>
#include <IOKit/IOUserClient.h>
#include <IOKit/IOBufferMemoryDescriptor.h>
#include <IOKit/IOLocks.h>
#include "HyperVVMBusDevice.hpp"
#include "pvg_wire.h"
#include "pvg_uapi.h"

// IOMallocData/IOFreeData (data-only zones) arrived in the macOS 12 kernel SDK; the MacKernelSDK
// that MacHyperVSupport builds against predates them. Plain IOMalloc/IOFree are equivalent here.
#ifndef IOMallocData
#define IOMallocData(size) IOMalloc(size)
#define IOFreeData(ptr, size) IOFree((ptr), (size))
#endif

class PVGTransport : public IOService {
    OSDeclareDefaultStructors(PVGTransport);
    typedef IOService super;
    friend class PVGUserClient;

public:
    bool start(IOService *provider) APPLE_KEXT_OVERRIDE;
    void stop(IOService *provider) APPLE_KEXT_OVERRIDE;
    void free() APPLE_KEXT_OVERRIDE;

    // Called by PVGUserClient. All block on the calling thread, never in interrupt context.
    IOReturn getCaps(void *out, uint32_t *size);
    IOReturn contextInit(const pvg_uc_context_init *in, uint32_t *contextId, task_t owner);
    IOReturn contextDestroy(uint32_t contextId);
    IOReturn createBlob(const pvg_uc_blob *in, pvg_uc_blob_reply *out, task_t owner);
    IOReturn unrefBlob(uint32_t resourceId);
    IOReturn writeRange(uint32_t resourceId, uint64_t offset, uint64_t size);
    IOReturn flushMirrors(task_t owner);
    IOReturn readRange(uint32_t resourceId, uint64_t offset, uint64_t size);
    IOReturn watchRange(uint32_t resourceId, uint64_t offset, uint64_t size);
    IOReturn markControl(uint32_t resourceId, uint64_t offset, uint64_t size, task_t owner);
    IOReturn execbuffer(const pvg_uc_exec *in, const void *commands, uint64_t *fenceId);
    IOReturn syncCreate(uint64_t initial, uint32_t *syncId);
    IOReturn syncDestroy(uint32_t syncId);
    IOReturn syncRead(uint32_t syncId, uint64_t *value);
    IOReturn syncWrite(uint32_t syncId, uint64_t value);
    IOReturn wait(const pvg_uc_wait *in);
    void getStats(pvg_uc_stats *out);
    IOMemoryDescriptor *mappingForResource(uint32_t resourceId);
    void releaseOwner(task_t owner);

private:
    enum { kMaxResources = 4096, kMaxSyncs = 4096, kMaxContexts = 64, kMaxPendingSignals = 1024, kMaxReplies = 32 };
    enum { kMaxControls = 4, kMaxControlSize = 8, kMaxControlSnaps = 512, kDiffStage = 256 * 1024 };
    // memory: guest copy mapped into user space. synced: kernel-only copy of the last state both sides agree on.
    // controls: words that order the rest (a Venus ring's head, tail, status); see PVG_RANGE_CONTROL.
    struct Resource { uint32_t id, contextId; uint64_t size; IOBufferMemoryDescriptor *memory; task_t owner; IOBufferMemoryDescriptor *synced;
                      uint32_t controlCount; uint32_t controlOffset[kMaxControls]; uint8_t controlSize[kMaxControls]; };
    // bytes: the guest copy; agreed: the synced copy, both taken together under _lock.
    struct ControlSnap { uint32_t resourceId, offset; uint8_t size; uint8_t bytes[kMaxControlSize], agreed[kMaxControlSize]; };
    struct Sync { uint32_t id; bool used; uint64_t value; };
    struct PendingSignal { bool used; uint32_t contextId, ringIdx; uint64_t fenceId; uint32_t count; pvg_uc_sync_point points[PVG_UC_MAX_SYNCS]; };
    struct Reply { bool used, done; uint32_t sequence; uint16_t type; void *buffer; uint32_t capacity, length; int32_t error; };

    static void packetReady(void *target, VMBusPacketHeader *header, UInt32 headerLength, UInt8 *data, UInt32 dataLength);
    void receiveBytes(const uint8_t *bytes, uint32_t length);
    void dispatchMessage(const pvg_msg_header &h, const uint8_t *payload);
    IOReturn sendMessage(uint16_t type, uint16_t flags, uint32_t sequence, const void *payload, uint32_t size,
                         const void *tail = nullptr, uint32_t tailSize = 0);
    IOReturn request(uint16_t type, const void *payload, uint32_t size, void *reply, uint32_t replyCapacity,
                     uint32_t *replyLength, const void *tail = nullptr, uint32_t tailSize = 0, uint32_t timeoutMs = 10000);
    IOReturn negotiate();
    void publishCaps();
    Resource *findResource(uint32_t id);
    Sync *findSync(uint32_t id);
    bool syncsReached(const pvg_uc_wait *in);
    IOReturn sendDiff(uint32_t resourceId, const uint8_t *current, uint8_t *synced, uint64_t size,
                      uint32_t controlCount, const uint32_t *controlOffset);

    HyperVVMBusDevice *_device = nullptr;
    IOLock *_lock = nullptr;          // state, replies, syncs
    IOLock *_sendLock = nullptr;      // keeps each message contiguous in the stream
    IOLock *_flushLock = nullptr;     // one FLUSH_MIRRORS at a time (they share _controlSnaps)
    ControlSnap *_controlSnaps = nullptr;
    uint8_t *_diffStage = nullptr;      // one changed run of a flush, copied under _lock (_flushLock)
    bool _connected = false, _negotiated = false;
    uint32_t _nextSequence = 1, _nextResource = 1, _nextSync = 1, _nextContext = 1;
    uint64_t _nextFence[kMaxContexts] = {};
    uint8_t *_rxBuffer = nullptr; uint32_t _rxLength = 0, _rxCapacity = 0;
    uint8_t *_txPacket = nullptr;
    uint8_t *_caps = nullptr; uint32_t _capsLength = 0;
    Resource _resources[kMaxResources] = {};
    Sync _syncs[kMaxSyncs] = {};
    PendingSignal _pending[kMaxPendingSignals] = {};
    Reply _replies[kMaxReplies] = {};
    bool _contexts[kMaxContexts] = {};
    task_t _contextOwner[kMaxContexts] = {};   // the process that created each context (freed on close)
    pvg_uc_stats _stats = {};
};

class PVGUserClient : public IOUserClient {
    OSDeclareDefaultStructors(PVGUserClient);
    typedef IOUserClient super;
public:
    bool initWithTask(task_t owningTask, void *securityID, UInt32 type, OSDictionary *properties) APPLE_KEXT_OVERRIDE;
    bool start(IOService *provider) APPLE_KEXT_OVERRIDE;
    IOReturn clientClose() APPLE_KEXT_OVERRIDE;
    IOReturn externalMethod(uint32_t selector, IOExternalMethodArguments *arguments,
                            IOExternalMethodDispatch *dispatch, OSObject *target, void *reference) APPLE_KEXT_OVERRIDE;
    IOReturn clientMemoryForType(UInt32 type, IOOptionBits *options, IOMemoryDescriptor **memory) APPLE_KEXT_OVERRIDE;
private:
    PVGTransport *_transport = nullptr;
    task_t _task = nullptr;
};

#endif
