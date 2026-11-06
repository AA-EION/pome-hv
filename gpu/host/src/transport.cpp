// Pome Virtual GPU host: Hyper-V socket and TCP transports. SPDX-License-Identifier: MIT
#include "transport.hpp"
#include "pvg_wire.h"
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <hvsocket.h>
#include <rpc.h>
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "rpcrt4.lib")
using socket_t = SOCKET;
static constexpr socket_t kBadSocket = INVALID_SOCKET;
static void CloseSocket(socket_t s) { closesocket(s); }
static std::string LastError() { return std::to_string(WSAGetLastError()); }
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <unistd.h>
#include <cerrno>
#include <cstring>
using socket_t = int;
static constexpr socket_t kBadSocket = -1;
static void CloseSocket(socket_t s) { ::close(s); }
static std::string LastError() { return std::strerror(errno); }
#endif
#include <chrono>
#include <thread>

namespace pvg {
namespace {

class SocketStream final : public Stream {
public:
    SocketStream(socket_t s, std::string what) : s_(s), what_(std::move(what)) {}
    ~SocketStream() override { Close(); }
    bool ReadExact(void* data, size_t size) override {
        auto* p = static_cast<char*>(data);
        while (size) {
            const int chunk = static_cast<int>(size > (1u << 30) ? (1u << 30) : size);
            const int n = ::recv(s_, p, chunk, 0);
            if (n <= 0) return false;
            p += n; size -= static_cast<size_t>(n);
        }
        return true;
    }
    bool WriteAll(const void* data, size_t size) override {
        auto* p = static_cast<const char*>(data);
        while (size) {
            const int chunk = static_cast<int>(size > (1u << 30) ? (1u << 30) : size);
            const int n = ::send(s_, p, chunk, 0);
            if (n <= 0) return false;
            p += n; size -= static_cast<size_t>(n);
        }
        return true;
    }
    void Close() override { if (s_ != kBadSocket) { CloseSocket(s_); s_ = kBadSocket; } }
    std::string Describe() const override { return what_; }
private:
    socket_t s_;
    std::string what_;
};

void TuneTcp(socket_t s) {
    int one = 1;
    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&one), sizeof one);
    int buf = 8 * 1024 * 1024;
    setsockopt(s, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char*>(&buf), sizeof buf);
    setsockopt(s, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&buf), sizeof buf);
}

} // namespace

bool InitSockets(std::string* error) {
#ifdef _WIN32
    WSADATA data;
    if (int r = WSAStartup(MAKEWORD(2, 2), &data); r != 0) { if (error) *error = "WSAStartup failed: " + std::to_string(r); return false; }
#endif
    (void)error;
    return true;
}

std::unique_ptr<Stream> ConnectHyperV(const std::string& vm_id, uint32_t timeout_ms, std::string* error) {
#ifdef _WIN32
    SOCKADDR_HV address{};
    address.Family = AF_HYPERV;
    if (UuidFromStringA(reinterpret_cast<RPC_CSTR>(const_cast<char*>(vm_id.c_str())), &address.VmId) != RPC_S_OK) {
        if (error) *error = "Invalid VM id: " + vm_id; return nullptr;
    }
    if (UuidFromStringA(reinterpret_cast<RPC_CSTR>(const_cast<char*>(PVG_SERVICE_GUID_STRING)), &address.ServiceId) != RPC_S_OK) {
        if (error) *error = "Invalid service GUID"; return nullptr;
    }
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    std::string last;
    do {
        socket_t s = ::socket(AF_HYPERV, SOCK_STREAM, HV_PROTOCOL_RAW);
        if (s == kBadSocket) { if (error) *error = "AF_HYPERV socket failed: " + LastError(); return nullptr; }
        // A short connect timeout lets the loop retry while the guest is still booting.
        DWORD connectTimeout = 2000;
        setsockopt(s, HV_PROTOCOL_RAW, HVSOCKET_CONNECT_TIMEOUT, reinterpret_cast<const char*>(&connectTimeout), sizeof connectTimeout);
        if (::connect(s, reinterpret_cast<sockaddr*>(&address), sizeof address) == 0)
            return std::make_unique<SocketStream>(s, "hvsock:" + vm_id);
        last = LastError();
        CloseSocket(s);
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
    } while (std::chrono::steady_clock::now() < deadline);
    if (error) *error = "The guest did not accept the PVG service (last error " + last + "). Is the PVG kext loaded?";
    return nullptr;
#else
    (void)vm_id; (void)timeout_ms;
    if (error) *error = "Hyper-V sockets exist only on Windows hosts";
    return nullptr;
#endif
}

std::unique_ptr<Stream> ListenTcpOnce(uint16_t port, std::string* error) {
    socket_t l = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (l == kBadSocket) { if (error) *error = "socket failed: " + LastError(); return nullptr; }
    int one = 1;
    setsockopt(l, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&one), sizeof one);
    sockaddr_in a{}; a.sin_family = AF_INET; a.sin_port = htons(port); a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (::bind(l, reinterpret_cast<sockaddr*>(&a), sizeof a) != 0 || ::listen(l, 1) != 0) {
        if (error) *error = "bind/listen on 127.0.0.1:" + std::to_string(port) + " failed: " + LastError(); CloseSocket(l); return nullptr;
    }
    socket_t s = ::accept(l, nullptr, nullptr);
    CloseSocket(l);
    if (s == kBadSocket) { if (error) *error = "accept failed: " + LastError(); return nullptr; }
    TuneTcp(s);
    return std::make_unique<SocketStream>(s, "tcp:127.0.0.1:" + std::to_string(port));
}

std::unique_ptr<Stream> ConnectTcp(const std::string& host, uint16_t port, std::string* error) {
    socket_t s = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == kBadSocket) { if (error) *error = "socket failed: " + LastError(); return nullptr; }
    sockaddr_in a{}; a.sin_family = AF_INET; a.sin_port = htons(port);
    if (inet_pton(AF_INET, host.c_str(), &a.sin_addr) != 1) { if (error) *error = "bad address " + host; CloseSocket(s); return nullptr; }
    for (int attempt = 0; attempt < 50; ++attempt) {
        if (::connect(s, reinterpret_cast<sockaddr*>(&a), sizeof a) == 0) { TuneTcp(s); return std::make_unique<SocketStream>(s, "tcp:" + host + ":" + std::to_string(port)); }
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    if (error) *error = "connect failed: " + LastError();
    CloseSocket(s);
    return nullptr;
}

} // namespace pvg
