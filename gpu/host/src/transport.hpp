// Pome Virtual GPU host: byte-stream transports. SPDX-License-Identifier: MIT
#pragma once
#include <cstddef>
#include <cstdint>
#include <memory>
#include <string>

namespace pvg {

// A reliable, ordered byte stream. Implementations: Hyper-V socket (production) and TCP loopback
// (development and tests). All calls block; a false return means the peer is gone.
class Stream {
public:
    virtual ~Stream() = default;
    virtual bool ReadExact(void* data, size_t size) = 0;
    virtual bool WriteAll(const void* data, size_t size) = 0;
    virtual void Close() = 0;
    virtual std::string Describe() const = 0;
};

// Connects to the guest's PVG service inside the VM identified by vm_id (a GUID string), retrying
// until the guest kext accepts the offer or timeout_ms elapses. The host initiates the connection,
// so the guest needs no outbound hv_sock support.
std::unique_ptr<Stream> ConnectHyperV(const std::string& vm_id, uint32_t timeout_ms, std::string* error);

// Development transports.
std::unique_ptr<Stream> ListenTcpOnce(uint16_t port, std::string* error);
std::unique_ptr<Stream> ConnectTcp(const std::string& host, uint16_t port, std::string* error);

bool InitSockets(std::string* error);

} // namespace pvg
