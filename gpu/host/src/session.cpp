// Pome Virtual GPU host: one guest session. SPDX-License-Identifier: MIT
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif
#include "session.hpp"
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <thread>

namespace pvg {
namespace {
constexpr int32_t kEINVAL = -22, kENOENT = -2, kENODEV = -19, kEPROTO = -71;
template <typename T> bool Take(const std::vector<uint8_t>& p, T* out, size_t at = 0) {
    if (p.size() < at + sizeof(T)) return false;
    std::memcpy(out, p.data() + at, sizeof(T));
    return true;
}
int64_t NowNs() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// Windows rounds ordinary sleeps up to the timer tick (often 15.6 ms); the high-resolution
// waitable timer (Windows 10 1803+) gives sub-millisecond waits without raising the system tick.
void SleepMicros(unsigned us) {
#ifdef _WIN32
#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif
    static thread_local HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (timer) {
        LARGE_INTEGER due; due.QuadPart = -static_cast<LONGLONG>(us) * 10;   // relative, 100 ns units
        if (SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE) && WaitForSingleObject(timer, INFINITE) == WAIT_OBJECT_0) return;
    }
#endif
    std::this_thread::sleep_for(std::chrono::microseconds(us));
}
} // namespace

Session::Session(std::unique_ptr<Stream> stream, HostReport report, std::unique_ptr<Renderer> renderer, bool verbose)
    : stream_(std::move(stream)), report_(std::move(report)), renderer_(std::move(renderer)), verbose_(verbose) {
    adapter_ = report_.preferred;
}

bool Session::Send(uint16_t type, uint32_t sequence, const void* payload, size_t size, const void* tail, size_t tail_size) {
    const size_t total = size + tail_size;
    if (total > PVG_MAX_MESSAGE) return false;
    pvg_msg_header h{PVG_MAGIC, type, 0, static_cast<uint32_t>(total), sequence};
    const size_t pad = (8 - (total % 8)) % 8;
    std::vector<uint8_t> m(sizeof h + total + pad, 0);
    std::memcpy(m.data(), &h, sizeof h);
    if (size) std::memcpy(m.data() + sizeof h, payload, size);
    if (tail_size) std::memcpy(m.data() + sizeof h + size, tail, tail_size);
    {
        std::lock_guard<std::mutex> lock(out_mutex_);
        if (out_failed_ || out_stop_) return false;
        out_bytes_ += m.size();
        out_.push_back(std::move(m));
    }
    out_cv_.notify_one();
    return true;
}

void Session::WriteLoop() {
    size_t warned = 0;
    for (;;) {
        std::vector<uint8_t> m;
        {
            std::unique_lock<std::mutex> lock(out_mutex_);
            out_cv_.wait(lock, [&] { return out_stop_ || !out_.empty(); });
            if (out_.empty()) return;   // stopping, and everything queued was written
            if (verbose_ && out_bytes_ > (256u << 20) && out_bytes_ > warned * 2) {
                warned = out_bytes_;
                std::fprintf(stderr, "pvg: %zu MiB queued for the guest\n", out_bytes_ >> 20);
            }
            m = std::move(out_.front());
            out_.pop_front();
            out_bytes_ -= m.size();
        }
        if (!stream_->WriteAll(m.data(), m.size())) {
            std::lock_guard<std::mutex> lock(out_mutex_);
            out_failed_ = true; out_.clear(); out_bytes_ = 0;
            return;
        }
        stats_.bytes_out += m.size();
    }
}

bool Session::SendError(uint32_t sequence, uint16_t request, int32_t code, const std::string& message) {
    pvg_error e{}; e.code = code; e.request_type = request;
    std::snprintf(e.message, sizeof e.message, "%s", message.c_str());
    if (verbose_) std::fprintf(stderr, "pvg: error %d for 0x%04x: %s\n", code, request, message.c_str());
    return Send(PVG_MSG_ERROR | PVG_MSG_REPLY, sequence, &e, sizeof e);
}

void Session::DiffRange(const uint8_t* current, MirroredResource::Watch& w,
                        std::vector<std::pair<uint64_t, std::vector<uint8_t>>>& out, size_t granule,
                        const std::vector<MirroredResource::Watch>* skip) {
    if (w.shadow.size() != w.size) w.shadow.assign(static_cast<size_t>(w.size), 0xCD); // first push sends everything
    auto skipped = [&](uint64_t pos) {
        if (!skip) return false;
        const uint64_t lo = w.offset + pos, hi = lo + granule;
        for (const auto& c : *skip) if (c.offset < hi && c.offset + c.size > lo) return true;
        return false;
    };
    uint64_t pos = 0;
    while (pos < w.size) {
        const size_t len = static_cast<size_t>(std::min<uint64_t>(granule, w.size - pos));
        if (skipped(pos) || std::memcmp(current + w.offset + pos, w.shadow.data() + pos, len) == 0) { pos += len; continue; }
        // Coalesce consecutive dirty granules into one span.
        uint64_t end = pos + len;
        while (end < w.size && !skipped(end)) {
            const size_t l2 = static_cast<size_t>(std::min<uint64_t>(granule, w.size - end));
            if (std::memcmp(current + w.offset + end, w.shadow.data() + end, l2) == 0) break;
            end += l2;
        }
        std::vector<uint8_t> bytes(current + w.offset + pos, current + w.offset + end);
        std::memcpy(w.shadow.data() + pos, bytes.data(), bytes.size());
        out.emplace_back(w.offset + pos, std::move(bytes));
        pos = end;
    }
}

size_t Session::PushWatches() {
    // A Venus ring's head is written after the replies it covers. Reading every control word first
    // and sending it last means a guest that sees a new head also has the bytes written before it.
    std::lock_guard<std::mutex> push(push_mutex_);
    using Spans = std::vector<std::pair<uint64_t, std::vector<uint8_t>>>;
    std::vector<std::pair<uint32_t, Spans>> data, control;
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        struct Snap { uint32_t id; MirroredResource::Watch* control; std::array<uint8_t, 8> bytes; };
        std::vector<Snap> snaps;
        for (auto& [id, res] : resources_) {
            if (!res.mapping.host_ptr) continue;
            auto* base = static_cast<const uint8_t*>(res.mapping.host_ptr);
            for (auto& c : res.controls) {
                Snap s{id, &c, {}};
                std::memcpy(s.bytes.data(), base + c.offset, static_cast<size_t>(c.size));
                snaps.push_back(s);
            }
        }
        std::atomic_thread_fence(std::memory_order_seq_cst);
        for (auto& [id, res] : resources_) {
            if (!res.mapping.host_ptr || res.watches.empty()) continue;
            Spans spans;
            for (auto& w : res.watches)
                DiffRange(static_cast<const uint8_t*>(res.mapping.host_ptr), w, spans, 64, res.controls.empty() ? nullptr : &res.controls);
            if (!spans.empty()) data.emplace_back(id, std::move(spans));
        }
        for (auto& s : snaps) {
            auto& c = *s.control;
            const size_t n = static_cast<size_t>(c.size);
            if (c.shadow.size() == n && std::memcmp(c.shadow.data(), s.bytes.data(), n) == 0) continue;
            c.shadow.assign(s.bytes.begin(), s.bytes.begin() + n);
            Spans one;
            one.emplace_back(c.offset, std::vector<uint8_t>(s.bytes.begin(), s.bytes.begin() + n));
            control.emplace_back(s.id, std::move(one));
        }
    }
    size_t sent = 0;
    for (auto* batch : {&data, &control})
        for (auto& [id, spans] : *batch)
            for (auto& [offset, bytes] : spans) {
                pvg_range r{}; r.resource_id = id; r.offset = offset; r.size = bytes.size();
                Send(PVG_MSG_RANGE_DATA, 0, &r, sizeof r, bytes.data(), bytes.size());
                ++stats_.pushes; ++sent;
            }
    return sent;
}

void Session::PushLoop() {
    // Fast while the guest is active (it may be spinning on a ring head), slow when idle.
    constexpr int64_t kActiveWindowNs = 250'000'000;
    while (!stop_pushing_.load()) {
        const bool active = NowNs() - last_activity_ns_.load() < kActiveWindowNs;
        SleepMicros(active ? 500 : 5000);
        if (stop_pushing_.load()) break;
        if (PushWatches()) last_activity_ns_.store(NowNs());
    }
}

void Session::OnFence(uint32_t context_id, uint32_t ring_idx, uint64_t fence_id) {
    PushWatches(); // feedback written by the GPU must reach the guest before it sees the fence
    pvg_fence f{context_id, ring_idx, fence_id};
    Send(PVG_MSG_FENCE_SIGNALED, 0, &f, sizeof f);
    ++stats_.fences;
}

std::vector<uint8_t> Session::CurrentBytes(const MirroredResource& res, uint64_t offset, uint64_t size) {
    if (!res.mapping.host_ptr) return {};   // no host copy yet: the first push sends everything
    auto* base = static_cast<const uint8_t*>(res.mapping.host_ptr) + offset;
    return std::vector<uint8_t>(base, base + size);
}

void Session::DropContext(uint32_t context_id) {
    std::vector<uint32_t> unref;
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        if (!contexts_.erase(context_id)) return;
        for (auto it = resources_.begin(); it != resources_.end();)
            if (it->second.context_id == context_id) { unref.push_back(it->first); it = resources_.erase(it); } else ++it;
    }
    if (verbose_) std::fprintf(stderr, "pvg: dropping context %u (%zu resources)\n", context_id, unref.size());
    for (uint32_t id : unref) renderer_->Unref(id);
    renderer_->DestroyContext(context_id);   // also frees anything vkr still holds for it
}

bool Session::Handle(const pvg_msg_header& h, std::vector<uint8_t>& p) {
    const uint16_t type = h.type;
    if (!negotiated_ && type != PVG_MSG_HELLO) return SendError(h.sequence, type, kEPROTO, "HELLO must come first");
    switch (type) {
    case PVG_MSG_HELLO: {
        pvg_hello hello{};
        if (!Take(p, &hello)) return SendError(h.sequence, type, kEINVAL, "short HELLO");
        if (hello.protocol_major != PVG_PROTOCOL_MAJOR)
            return SendError(h.sequence, type, kEPROTO, "unsupported protocol major " + std::to_string(hello.protocol_major));
        if (hello.guest_page_size != PVG_PAGE_SIZE) return SendError(h.sequence, type, kEINVAL, "guest page size must be 4096");
        hello.guest_name[sizeof hello.guest_name - 1] = 0;
        if (verbose_) std::fprintf(stderr, "pvg: guest '%s' build 0x%08x driver 0x%08x features 0x%llx\n", hello.guest_name,
                                   hello.guest_os_build, hello.guest_driver_version, static_cast<unsigned long long>(hello.guest_features));
        uint32_t flags = 0;
        std::vector<uint8_t> capset;
        if (std::string(renderer_->Name()) == "venus") { flags |= PVG_HOST_VENUS_AVAILABLE; capset = renderer_->VenusCapset(); }
        bool hardware = false;
        for (auto& a : report_.adapters) if (a.caps.device_type != 4 /* CPU */) hardware = true;
        if (!hardware) flags |= PVG_HOST_SOFTWARE_ONLY;
        negotiated_ = true;
        auto caps = SerializeCaps(report_, flags, capset);
        return Send(PVG_MSG_CAPS | PVG_MSG_REPLY, h.sequence, caps.data(), caps.size());
    }
    case PVG_MSG_SELECT_ADAPTER: {
        pvg_select_adapter s{};
        if (!Take(p, &s) || s.index >= report_.adapters.size()) return SendError(h.sequence, type, kEINVAL, "no such adapter");
        adapter_ = s.index;
        return Send(type | PVG_MSG_REPLY, h.sequence, &s, sizeof s);
    }
    case PVG_MSG_CONTEXT_INIT: {
        pvg_context_init c{};
        if (!Take(p, &c) || c.context_id == 0) return SendError(h.sequence, type, kEINVAL, "bad CONTEXT_INIT");
        c.debug_name[sizeof c.debug_name - 1] = 0;
        if (verbose_) std::fprintf(stderr, "pvg: CONTEXT_INIT id %u capset %u '%s'\n", c.context_id, c.capset_id, c.debug_name);
        DropContext(c.context_id);   // a reused id: the guest let the old context go without telling us
        const int r = renderer_->CreateContext(c.context_id, c.capset_id, c.debug_name);
        if (r) return SendError(h.sequence, type, r, r == kENODEV ? "no rendering back end on this host" : "context creation failed");
        { std::lock_guard<std::mutex> lock(state_mutex_); contexts_[c.context_id] = true; }
        int32_t ok = 0;
        return Send(type | PVG_MSG_REPLY, h.sequence, &ok, sizeof ok);
    }
    case PVG_MSG_CONTEXT_DESTROY: {
        pvg_context_ref c{};
        if (!Take(p, &c)) return SendError(h.sequence, type, kEINVAL, "bad CONTEXT_DESTROY");
        if (verbose_) std::fprintf(stderr, "pvg: CONTEXT_DESTROY id %u\n", c.context_id);
        DropContext(c.context_id);
        return true;
    }
    case PVG_MSG_RESOURCE_BLOB: {
        pvg_resource_blob b{};
        if (!Take(p, &b) || b.resource_id == 0 || b.size == 0 || b.size > (64ull << 30)) return SendError(h.sequence, type, kEINVAL, "bad RESOURCE_BLOB");
        MirroredResource res; res.context_id = b.context_id;
        const int r = renderer_->CreateBlob(b.context_id, b.resource_id, b.blob_mem, b.blob_flags, b.blob_id, b.size, &res.mapping);
        pvg_resource_reply reply{r, res.mapping.map_info, res.mapping.size};
        if (r == 0) { std::lock_guard<std::mutex> lock(state_mutex_); resources_[b.resource_id] = std::move(res); }
        return Send(type | PVG_MSG_REPLY, h.sequence, &reply, sizeof reply);
    }
    case PVG_MSG_RESOURCE_UNREF: {
        pvg_resource_ref r{};
        if (!Take(p, &r)) return SendError(h.sequence, type, kEINVAL, "bad RESOURCE_UNREF");
        renderer_->Unref(r.resource_id);
        std::lock_guard<std::mutex> lock(state_mutex_);
        resources_.erase(r.resource_id);
        return true;
    }
    case PVG_MSG_WRITE_RANGE: {
        pvg_range r{};
        if (!Take(p, &r) || p.size() < sizeof r + r.size) return SendError(h.sequence, type, kEINVAL, "short WRITE_RANGE");
        std::lock_guard<std::mutex> lock(state_mutex_);
        auto it = resources_.find(r.resource_id);
        if (it == resources_.end() || !it->second.mapping.host_ptr || r.offset + r.size > it->second.mapping.size)
            return SendError(h.sequence, type, kENOENT, "WRITE_RANGE outside a mapped resource");
        std::memcpy(static_cast<uint8_t*>(it->second.mapping.host_ptr) + r.offset, p.data() + sizeof r, static_cast<size_t>(r.size));
        // The guest already has these bytes: keep watched shadows in step so they are not echoed back.
        for (auto* list : {&it->second.watches, &it->second.controls})
        for (auto& w : *list) {
            if (w.shadow.size() != w.size) continue;
            const uint64_t lo = std::max(w.offset, r.offset), hi = std::min(w.offset + w.size, r.offset + r.size);
            if (lo < hi) std::memcpy(w.shadow.data() + (lo - w.offset), p.data() + sizeof r + (lo - r.offset), static_cast<size_t>(hi - lo));
        }
        return true;
    }
    case PVG_MSG_READ_RANGE: {
        pvg_range r{};
        if (!Take(p, &r)) return SendError(h.sequence, type, kEINVAL, "bad READ_RANGE");
        std::vector<uint8_t> bytes;
        {
            std::lock_guard<std::mutex> lock(state_mutex_);
            auto it = resources_.find(r.resource_id);
            if (it == resources_.end() || !it->second.mapping.host_ptr || r.offset + r.size > it->second.mapping.size)
                return SendError(h.sequence, type, kENOENT, "READ_RANGE outside a mapped resource");
            auto* base = static_cast<const uint8_t*>(it->second.mapping.host_ptr) + r.offset;
            bytes.assign(base, base + r.size);
        }
        return Send(PVG_MSG_RANGE_DATA | PVG_MSG_REPLY, h.sequence, &r, sizeof r, bytes.data(), bytes.size());
    }
    case PVG_MSG_WATCH_RANGE: {
        pvg_range r{};
        if (!Take(p, &r)) return SendError(h.sequence, type, kEINVAL, "bad WATCH_RANGE");
        std::lock_guard<std::mutex> lock(state_mutex_);
        auto it = resources_.find(r.resource_id);
        if (it == resources_.end() || r.offset + r.size > it->second.mapping.size || r.size > (1u << 20))
            return SendError(h.sequence, type, kENOENT, "WATCH_RANGE outside a resource or larger than 1 MiB");
        if (r.flags & PVG_RANGE_CONTROL) {
            if (r.size == 0 || r.size > 8) return SendError(h.sequence, type, kEINVAL, "control words are 1 to 8 bytes");
            auto& cs = it->second.controls;   // a cached shmem reused for a new ring marks again
            if (std::none_of(cs.begin(), cs.end(), [&](const MirroredResource::Watch& c) { return c.offset == r.offset; }))
                cs.push_back({r.offset, r.size, CurrentBytes(it->second, r.offset, r.size)});
        } else {
            // Start from what the guest already holds (it read the resource when it was created),
            // so the first push carries only later changes, not the whole range again.
            it->second.watches.push_back({r.offset, r.size, CurrentBytes(it->second, r.offset, r.size)});
        }
        return true;
    }
    case PVG_MSG_EXECBUFFER: {
        pvg_execbuffer e{};
        if (!Take(p, &e) || p.size() < sizeof e + e.command_size) return SendError(h.sequence, type, kEINVAL, "short EXECBUFFER");
        std::vector<uint8_t> commands(p.begin() + sizeof e, p.begin() + sizeof e + e.command_size);
        int r = renderer_->Submit(e.context_id, commands.data(), e.command_size);
        if (r == 0 && e.fence_id) r = renderer_->CreateFence(e.context_id, e.ring_idx, e.fence_id);
        ++stats_.execbuffers;
        renderer_->Poll();
        PushWatches();
        if (r) return SendError(h.sequence, type, r, "submit failed");
        if (h.flags & PVG_FLAG_NEEDS_REPLY) { int32_t ok = 0; return Send(type | PVG_MSG_REPLY, h.sequence, &ok, sizeof ok); }
        return true;
    }
    case PVG_MSG_WAIT: {
        renderer_->Poll();
        PushWatches();
        int32_t ok = 0;
        return Send(type | PVG_MSG_REPLY, h.sequence, &ok, sizeof ok);
    }
    case PVG_MSG_PRESENT:
        // Host-window presentation is implemented by pome's console; until then the guest copies
        // frames back with READ_RANGE and this message is acknowledged without effect.
        return true;
    default:
        return SendError(h.sequence, type, kEINVAL, "unknown message type");
    }
}

std::string Session::Run() {
    stop_pushing_ = false;
    last_activity_ns_ = NowNs();
    { std::lock_guard<std::mutex> lock(out_mutex_); out_stop_ = false; out_failed_ = false; }
    std::thread writer([this] { WriteLoop(); });
    std::thread pusher([this] { PushLoop(); });
    std::string reason = Serve();
    stop_pushing_ = true;
    pusher.join();
    { std::lock_guard<std::mutex> lock(out_mutex_); out_stop_ = true; }
    out_cv_.notify_all();
    writer.join();
    return reason;
}

std::string Session::Serve() {
    std::vector<uint8_t> payload;
    for (;;) {
        pvg_msg_header h{};
        if (!stream_->ReadExact(&h, sizeof h)) return "guest disconnected";
        last_activity_ns_.store(NowNs());
        if (h.magic != PVG_MAGIC || h.length > PVG_MAX_MESSAGE) return "protocol error: bad header";
        const size_t padded = h.length + (8 - (h.length % 8)) % 8;
        payload.resize(padded);
        if (padded && !stream_->ReadExact(payload.data(), padded)) return "guest disconnected mid-message";
        payload.resize(h.length);
        stats_.bytes_in += sizeof h + padded;
        if (!Handle(h, payload)) return "stream closed while replying";
    }
}

} // namespace pvg
