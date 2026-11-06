// Pome Virtual GPU host: one guest session. SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>
#include "caps.hpp"
#include "renderer.hpp"
#include "transport.hpp"

namespace pvg {

struct SessionStats { uint64_t bytes_in = 0, bytes_out = 0, execbuffers = 0, fences = 0, pushes = 0; };

// Mirrored resource: the host copy is authoritative for GPU-written data, the guest copy for
// CPU-written data. Watched ranges are diffed against a shadow so only changed bytes travel.
struct MirroredResource {
    uint32_t context_id = 0;
    BlobMapping mapping;
    struct Watch { uint64_t offset, size; std::vector<uint8_t> shadow; };
    std::vector<Watch> watches;
};

class Session {
public:
    Session(std::unique_ptr<Stream> stream, HostReport report, std::unique_ptr<Renderer> renderer, bool verbose);
    // Runs until the guest disconnects or a protocol error occurs. Returns a reason.
    std::string Run();
    const SessionStats& Stats() const { return stats_; }
    // Called by the renderer (possibly from its fence thread).
    void OnFence(uint32_t context_id, uint32_t ring_idx, uint64_t fence_id);

    // Exposed for tests: diff a watched range and append changed spans to out (offset, bytes).
    static void DiffRange(const uint8_t* current, MirroredResource::Watch& watch,
                          std::vector<std::pair<uint64_t, std::vector<uint8_t>>>& out, size_t granule = 64);

private:
    bool Send(uint16_t type, uint32_t sequence, const void* payload, size_t size, const void* tail = nullptr, size_t tail_size = 0);
    bool SendError(uint32_t sequence, uint16_t request, int32_t code, const std::string& message);
    bool Handle(const pvg_msg_header& h, std::vector<uint8_t>& payload);
    void PushWatches();

    std::unique_ptr<Stream> stream_;
    HostReport report_;
    std::unique_ptr<Renderer> renderer_;
    bool verbose_;
    bool negotiated_ = false;
    uint32_t adapter_ = 0;
    std::mutex write_mutex_;
    std::mutex state_mutex_;
    std::map<uint32_t, MirroredResource> resources_;
    std::map<uint32_t, bool> contexts_;
    SessionStats stats_;
};

} // namespace pvg
