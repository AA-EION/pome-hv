// Pome Virtual GPU host: one guest session. SPDX-License-Identifier: MIT
#pragma once
#include <atomic>
#include <condition_variable>
#include <deque>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
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
    // Control words (PVG_RANGE_CONTROL, e.g. a Venus ring's head and status): read before and sent
    // after every other watched byte, and skipped by the ordinary watches.
    std::vector<Watch> controls;
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
    // Granules holding one of the skip offsets (control words) are left out.
    static void DiffRange(const uint8_t* current, MirroredResource::Watch& watch,
                          std::vector<std::pair<uint64_t, std::vector<uint8_t>>>& out, size_t granule = 64,
                          const std::vector<MirroredResource::Watch>* skip = nullptr);

private:
    bool Send(uint16_t type, uint32_t sequence, const void* payload, size_t size, const void* tail = nullptr, size_t tail_size = 0);
    bool SendError(uint32_t sequence, uint16_t request, int32_t code, const std::string& message);
    bool Handle(const pvg_msg_header& h, std::vector<uint8_t>& payload);
    std::string Serve();
    // Sends what the host changed in watched ranges; returns the number of messages sent.
    // full = false only checks control words and diffs the rest when one of them moved.
    size_t PushWatches(bool full = true);
    // Pushes on its own so ring heads and statuses reach the guest while it only waits.
    void PushLoop();
    // Destroys a context and the resources it holds, if the session has it (no-op otherwise).
    void DropContext(uint32_t context_id);
    static std::vector<uint8_t> CurrentBytes(const MirroredResource& res, uint64_t offset, uint64_t size);

    std::unique_ptr<Stream> stream_;
    HostReport report_;
    std::unique_ptr<Renderer> renderer_;
    bool verbose_;
    bool negotiated_ = false;
    uint32_t adapter_ = 0;
    // Outgoing messages. The reader thread must never block on a write: if it did while the guest
    // also blocked writing to us, each side's ring would stay full (seen on Tahoe as a hung restart).
    void WriteLoop();
    std::mutex out_mutex_;
    std::condition_variable out_cv_;
    std::deque<std::vector<uint8_t>> out_;
    size_t out_bytes_ = 0;
    bool out_stop_ = false, out_failed_ = false;
    std::mutex state_mutex_;
    std::mutex push_mutex_;   // one push at a time, so control words never overtake data
    std::atomic<bool> stop_pushing_{false};
    std::atomic<int64_t> last_activity_ns_{0};
    std::map<uint32_t, MirroredResource> resources_;
    std::map<uint32_t, bool> contexts_;
    std::map<uint32_t, std::string> control_dump_;   // -v: last ring words printed per resource
    std::map<uint64_t, uint64_t> exec_ops_;           // -v: (context << 32 | first command type) -> batches
    std::string ops_dump_;
    void DumpControls();
    SessionStats stats_;
};

} // namespace pvg
