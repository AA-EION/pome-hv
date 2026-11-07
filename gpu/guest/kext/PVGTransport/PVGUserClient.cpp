/*
 * Pome Virtual GPU: user client for PVGTransport. SPDX-License-Identifier: MIT
 */
#include "PVGTransport.hpp"

bool PVGUserClient::initWithTask(task_t owningTask, void *securityID, UInt32 type, OSDictionary *properties) {
    if (!super::initWithTask(owningTask, securityID, type, properties)) return false;
    _task = owningTask;
    return true;
}

bool PVGUserClient::start(IOService *provider) {
    _transport = OSDynamicCast(PVGTransport, provider);
    return _transport && super::start(provider);
}

IOReturn PVGUserClient::clientClose() {
    if (_transport) _transport->releaseOwner(_task);
    terminate();
    return kIOReturnSuccess;
}

IOReturn PVGUserClient::clientMemoryForType(UInt32 type, IOOptionBits *options, IOMemoryDescriptor **memory) {
    if (!_transport) return kIOReturnNotAttached;
    IOMemoryDescriptor *m = _transport->mappingForResource(type);
    if (!m) return kIOReturnNotFound;
    *options = 0;        // cached, read/write
    *memory = m;         // reference passed to IOKit
    return kIOReturnSuccess;
}

IOReturn PVGUserClient::externalMethod(uint32_t selector, IOExternalMethodArguments *a,
                                       IOExternalMethodDispatch *, OSObject *, void *) {
    if (!_transport) return kIOReturnNotAttached;
    auto scalars = [&](uint32_t in, uint32_t out) { return a->scalarInputCount == in && a->scalarOutputCount <= out; };
    switch (selector) {
    case PVG_UC_GET_CAPS: {
        // Small enough for the inline structure in practice; larger payloads use the descriptor.
        if (IOMemoryDescriptor *d = a->structureOutputDescriptor) {
            const uint32_t capacity = static_cast<uint32_t>(d->getLength());
            uint32_t size = capacity;
            void *tmp = IOMallocData(capacity);
            if (!tmp) return kIOReturnNoMemory;
            IOReturn r = _transport->getCaps(tmp, &size);
            // XNU hands out-of-line structures over as unwired user ranges (withAddressRange);
            // wire them before copying (IOUserClient.cpp, is_io_connect_method).
            if (r == kIOReturnSuccess) r = d->prepare(kIODirectionIn);
            if (r == kIOReturnSuccess) {
                if (d->writeBytes(0, tmp, size) != size) r = kIOReturnVMError;
                d->complete(kIODirectionIn);
                a->structureOutputDescriptorSize = size;
            }
            IOFreeData(tmp, capacity);
            return r;
        }
        uint32_t size = a->structureOutputSize;
        IOReturn r = _transport->getCaps(a->structureOutput, &size);
        a->structureOutputSize = size;
        return r;
    }
    case PVG_UC_CONTEXT_INIT: {
        if (a->structureInputSize != sizeof(pvg_uc_context_init) || a->scalarOutputCount < 1) return kIOReturnBadArgument;
        uint32_t id = 0;
        IOReturn r = _transport->contextInit(static_cast<const pvg_uc_context_init *>(a->structureInput), &id, _task);
        a->scalarOutput[0] = id; a->scalarOutputCount = 1;
        return r;
    }
    case PVG_UC_CONTEXT_DESTROY:
        if (!scalars(1, 0)) return kIOReturnBadArgument;
        return _transport->contextDestroy(static_cast<uint32_t>(a->scalarInput[0]));
    case PVG_UC_RESOURCE_BLOB: {
        if (a->structureInputSize != sizeof(pvg_uc_blob) || a->structureOutputSize < sizeof(pvg_uc_blob_reply)) return kIOReturnBadArgument;
        pvg_uc_blob_reply reply{};
        IOReturn r = _transport->createBlob(static_cast<const pvg_uc_blob *>(a->structureInput), &reply, _task);
        memcpy(a->structureOutput, &reply, sizeof reply); a->structureOutputSize = sizeof reply;
        return r;
    }
    case PVG_UC_RESOURCE_UNREF:
        if (!scalars(1, 0)) return kIOReturnBadArgument;
        return _transport->unrefBlob(static_cast<uint32_t>(a->scalarInput[0]));
    case PVG_UC_WRITE_RANGE:
    case PVG_UC_READ_RANGE:
    case PVG_UC_WATCH_RANGE: {
        if (!scalars(3, 0)) return kIOReturnBadArgument;
        const uint32_t id = static_cast<uint32_t>(a->scalarInput[0]);
        const uint64_t offset = a->scalarInput[1], size = a->scalarInput[2];
        if (selector == PVG_UC_WRITE_RANGE) return _transport->writeRange(id, offset, size);
        if (selector == PVG_UC_READ_RANGE) return _transport->readRange(id, offset, size);
        return _transport->watchRange(id, offset, size);
    }
    case PVG_UC_EXECBUFFER: {
        // Header and command bytes arrive together; large command streams come as a descriptor.
        IOMemoryDescriptor *d = a->structureInputDescriptor;
        const uint64_t length = d ? d->getLength() : a->structureInputSize;
        if (length < sizeof(pvg_uc_exec) || length > sizeof(pvg_uc_exec) + PVG_MAX_MESSAGE || a->scalarOutputCount < 1) return kIOReturnBadArgument;
        void *buffer = IOMallocData(static_cast<vm_size_t>(length));
        if (!buffer) return kIOReturnNoMemory;
        if (d) {
            // Unwired user range (see GET_CAPS): wire, copy once, then validate only the copy.
            IOReturn w = d->prepare(kIODirectionOut);
            const bool copied = w == kIOReturnSuccess && d->readBytes(0, buffer, length) == length;
            if (w == kIOReturnSuccess) d->complete(kIODirectionOut);
            if (!copied) { IOFreeData(buffer, static_cast<vm_size_t>(length)); return w == kIOReturnSuccess ? kIOReturnVMError : w; }
        } else {
            memcpy(buffer, a->structureInput, static_cast<size_t>(length));
        }
        auto *exec = static_cast<pvg_uc_exec *>(buffer);
        IOReturn r = kIOReturnBadArgument;
        uint64_t fence = 0;
        if (sizeof(pvg_uc_exec) + exec->command_size == length) r = _transport->execbuffer(exec, exec + 1, &fence);
        IOFreeData(buffer, static_cast<vm_size_t>(length));
        a->scalarOutput[0] = fence; a->scalarOutputCount = 1;
        return r;
    }
    case PVG_UC_SYNC_CREATE: {
        if (!scalars(1, 1) || a->scalarOutputCount < 1) return kIOReturnBadArgument;
        uint32_t id = 0;
        IOReturn r = _transport->syncCreate(a->scalarInput[0], &id);
        a->scalarOutput[0] = id; a->scalarOutputCount = 1;
        return r;
    }
    case PVG_UC_SYNC_DESTROY:
        if (!scalars(1, 0)) return kIOReturnBadArgument;
        return _transport->syncDestroy(static_cast<uint32_t>(a->scalarInput[0]));
    case PVG_UC_SYNC_READ: {
        if (!scalars(1, 1) || a->scalarOutputCount < 1) return kIOReturnBadArgument;
        uint64_t v = 0;
        IOReturn r = _transport->syncRead(static_cast<uint32_t>(a->scalarInput[0]), &v);
        a->scalarOutput[0] = v; a->scalarOutputCount = 1;
        return r;
    }
    case PVG_UC_SYNC_WRITE:
        if (!scalars(2, 0)) return kIOReturnBadArgument;
        return _transport->syncWrite(static_cast<uint32_t>(a->scalarInput[0]), a->scalarInput[1]);
    case PVG_UC_WAIT:
        if (a->structureInputSize != sizeof(pvg_uc_wait)) return kIOReturnBadArgument;
        return _transport->wait(static_cast<const pvg_uc_wait *>(a->structureInput));
    case PVG_UC_MARK_CONTROL:
        if (!scalars(3, 0) || a->scalarInput[1] > 0xffffffffull) return kIOReturnBadArgument;
        return _transport->markControl(static_cast<uint32_t>(a->scalarInput[0]), a->scalarInput[1], a->scalarInput[2], _task);
    case PVG_UC_FLUSH_MIRRORS:
        if (a->scalarInputCount != 0) return kIOReturnBadArgument;
        return _transport->flushMirrors(_task);
    case PVG_UC_GET_STATS:
        if (a->structureOutputSize < sizeof(pvg_uc_stats)) return kIOReturnBadArgument;
        _transport->getStats(static_cast<pvg_uc_stats *>(a->structureOutput));
        a->structureOutputSize = sizeof(pvg_uc_stats);
        return kIOReturnSuccess;
    default:
        return kIOReturnUnsupported;
    }
}
