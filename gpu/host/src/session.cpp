// Pome Virtual GPU host: one guest session. SPDX-License-Identifier: MIT
#include "session.hpp"
#include <cstdio>
#include <cstring>

namespace pvg {
namespace {
constexpr int32_t kEINVAL = -22, kENOENT = -2, kENODEV = -19, kEPROTO = -71;
template <typename T> bool Take(const std::vector<uint8_t>& p, T* out, size_t at = 0) {
    if (p.size() < at + sizeof(T)) return false;
    std::memcpy(out, p.data() + at, sizeof(T));
    return true;
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
    static const uint8_t zeros[8] = {};
    std::lock_guard<std::mutex> lock(write_mutex_);
    bool ok = stream_->WriteAll(&h, sizeof h) && (!size || stream_->WriteAll(payload, size))
           && (!tail_size || stream_->WriteAll(tail, tail_size)) && (!pad || stream_->WriteAll(zeros, pad));
    if (ok) stats_.bytes_out += sizeof h + total + pad;
    return ok;
}

bool Session::SendError(uint32_t sequence, uint16_t request, int32_t code, const std::string& message) {
    pvg_error e{}; e.code = code; e.request_type = request;
    std::snprintf(e.message, sizeof e.message, "%s", message.c_str());
    if (verbose_) std::fprintf(stderr, "pvg: error %d for 0x%04x: %s\n", code, request, message.c_str());
    return Send(PVG_MSG_ERROR | PVG_MSG_REPLY, sequence, &e, sizeof e);
}

void Session::DiffRange(const uint8_t* current, MirroredResource::Watch& w,
                        std::vector<std::pair<uint64_t, std::vector<uint8_t>>>& out, size_t granule) {
    if (w.shadow.size() != w.size) w.shadow.assign(static_cast<size_t>(w.size), 0xCD); // first push sends everything
    uint64_t pos = 0;
    while (pos < w.size) {
        const size_t len = static_cast<size_t>(std::min<uint64_t>(granule, w.size - pos));
        if (std::memcmp(current + w.offset + pos, w.shadow.data() + pos, len) == 0) { pos += len; continue; }
        // Coalesce consecutive dirty granules into one span.
        uint64_t end = pos + len;
        while (end < w.size) {
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

void Session::PushWatches() {
    std::vector<std::pair<uint32_t, std::vector<std::pair<uint64_t, std::vector<uint8_t>>>>> pending;
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        for (auto& [id, res] : resources_) {
            if (!res.mapping.host_ptr || res.watches.empty()) continue;
            std::vector<std::pair<uint64_t, std::vector<uint8_t>>> spans;
            for (auto& w : res.watches) DiffRange(static_cast<const uint8_t*>(res.mapping.host_ptr), w, spans);
            if (!spans.empty()) pending.emplace_back(id, std::move(spans));
        }
    }
    for (auto& [id, spans] : pending)
        for (auto& [offset, bytes] : spans) {
            pvg_range r{}; r.resource_id = id; r.offset = offset; r.size = bytes.size();
            Send(PVG_MSG_RANGE_DATA, 0, &r, sizeof r, bytes.data(), bytes.size());
            ++stats_.pushes;
        }
}

void Session::OnFence(uint32_t context_id, uint32_t ring_idx, uint64_t fence_id) {
    PushWatches(); // feedback written by the GPU must reach the guest before it sees the fence
    pvg_fence f{context_id, ring_idx, fence_id};
    Send(PVG_MSG_FENCE_SIGNALED, 0, &f, sizeof f);
    ++stats_.fences;
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
        for (auto& w : it->second.watches) {
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
        it->second.watches.push_back({r.offset, r.size, {}});
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
    std::vector<uint8_t> payload;
    for (;;) {
        pvg_msg_header h{};
        if (!stream_->ReadExact(&h, sizeof h)) return "guest disconnected";
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
