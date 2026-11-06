// Pome Virtual GPU host tests: policy, mirroring and a loopback guest. SPDX-License-Identifier: MIT
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>
#include "caps.hpp"
#include "renderer.hpp"
#include "session.hpp"
#include "transport.hpp"

using namespace pvg;
static int failures = 0, passes = 0;
#define CHECK(cond, what) do { if (cond) { ++passes; } else { ++failures; std::printf("FAIL %s (%s:%d)\n", what, __FILE__, __LINE__); } } while (0)

static void TestMetalPolicy() {
    pvg_adapter_caps c{};
    c.api_version = (1u << 22) | (3u << 12); c.device_type = 2; // discrete
    c.features = PVG_FEAT_BC_TEXTURES | PVG_FEAT_IMAGE_CUBE_ARRAY | PVG_FEAT_DESCRIPTOR_INDEXING | PVG_FEAT_DEPTH_CLAMP;
    uint32_t m = MetalClassesFor(c, nullptr);
    CHECK((m & PVG_METAL_MAC2) && !(m & PVG_METAL_METAL3) && !(m & PVG_METAL_RAYTRACING), "Mac2-only GPU gets no Metal3 or RT");
    c.features |= PVG_FEAT_BUFFER_DEVICE_ADDR | PVG_FEAT_TIMELINE_SEMAPHORE | PVG_FEAT_SYNCHRONIZATION2 | PVG_FEAT_DYNAMIC_RENDERING | PVG_FEAT_MESH_SHADER;
    m = MetalClassesFor(c, nullptr);
    CHECK((m & PVG_METAL_METAL3) && (m & PVG_METAL_MESH) && (m & PVG_METAL_ARGBUF_TIER2), "Vulkan 1.3 + mesh + BDA gets Metal3");
    c.features |= PVG_FEAT_ACCEL_STRUCTURE | PVG_FEAT_RAY_QUERY;
    m = MetalClassesFor(c, nullptr);
    CHECK((m & PVG_METAL_RAYTRACING) && !(m & PVG_METAL_FUNCTION_POINTERS), "ray query without RT pipelines: RT but no function tables");
    c.features |= PVG_FEAT_RAY_TRACING_PIPELINE | PVG_FEAT_COOPERATIVE_MATRIX | PVG_FEAT_FLOAT16_INT8 | PVG_FEAT_MUTABLE_DESCRIPTOR;
    m = MetalClassesFor(c, nullptr);
    CHECK((m & PVG_METAL_METAL4_CANDIDATE) && (m & PVG_METAL_TENSOR_ML) && (m & PVG_METAL_FUNCTION_POINTERS), "full feature set reaches Metal 4 candidate");
    c.api_version = (1u << 22) | (2u << 12);
    m = MetalClassesFor(c, nullptr);
    CHECK(!(m & PVG_METAL_METAL3) && !(m & PVG_METAL_METAL4_CANDIDATE), "Vulkan 1.2 caps at Mac2 even with features");
}

static void TestPreferred() {
    std::vector<AdapterReport> a(3);
    a[0].caps.device_type = 1; a[0].caps.metal = PVG_METAL_MAC2;   // integrated
    a[1].caps.device_type = 4; a[1].caps.metal = 0;                // CPU
    a[2].caps.device_type = 2; a[2].caps.metal = PVG_METAL_MAC2;   // discrete
    CHECK(PreferredAdapter(a) == 2, "discrete GPU preferred over integrated and CPU");
}

static void TestDiff() {
    std::vector<uint8_t> mem(4096, 0);
    MirroredResource::Watch w{512, 1024, {}};
    std::vector<std::pair<uint64_t, std::vector<uint8_t>>> out;
    Session::DiffRange(mem.data(), w, out);
    CHECK(out.size() == 1 && out[0].first == 512 && out[0].second.size() == 1024, "first push sends the whole watch");
    out.clear(); Session::DiffRange(mem.data(), w, out);
    CHECK(out.empty(), "unchanged range sends nothing");
    mem[600] = 7; mem[601] = 8; mem[1400] = 9;
    out.clear(); Session::DiffRange(mem.data(), w, out);
    CHECK(out.size() == 2 && out[0].first == 576 && out[0].second.size() == 64 && out[1].first == 1344, "only dirty 64-byte granules travel");
}

// Plays the guest kext's role over loopback TCP.
static bool SendMsg(Stream& s, uint16_t type, uint32_t seq, const void* p, uint32_t n) {
    pvg_msg_header h{PVG_MAGIC, type, PVG_FLAG_NEEDS_REPLY, n, seq};
    static const uint8_t zeros[8] = {};
    const uint32_t pad = (8 - n % 8) % 8;
    return s.WriteAll(&h, sizeof h) && (!n || s.WriteAll(p, n)) && (!pad || s.WriteAll(zeros, pad));
}
static bool ReadMsg(Stream& s, pvg_msg_header& h, std::vector<uint8_t>& p) {
    if (!s.ReadExact(&h, sizeof h) || h.magic != PVG_MAGIC) return false;
    p.resize(h.length + (8 - h.length % 8) % 8);
    if (!p.empty() && !s.ReadExact(p.data(), p.size())) return false;
    p.resize(h.length);
    return true;
}

static void TestLoopback() {
    std::string err;
    CHECK(InitSockets(&err), "sockets initialise");
    const uint16_t port = 47231;
    HostReport report = ProbeHost();   // real host GPUs, read-only
    std::thread server([&] {
        auto stream = ListenTcpOnce(port, &err);
        if (!stream) return;
        Session s(std::move(stream), report, CreateNullRenderer(), false);
        s.Run();
    });
    auto c = ConnectTcp("127.0.0.1", port, &err);
    CHECK(c != nullptr, "loopback client connects");
    if (!c) { server.join(); return; }
    pvg_msg_header h{}; std::vector<uint8_t> p;

    pvg_context_init early{}; early.context_id = 1; early.capset_id = kCapsetVenus;
    SendMsg(*c, PVG_MSG_CONTEXT_INIT, 1, &early, sizeof early);
    CHECK(ReadMsg(*c, h, p) && h.type == (PVG_MSG_ERROR | PVG_MSG_REPLY), "requests before HELLO are refused");

    pvg_hello hello{}; hello.protocol_major = PVG_PROTOCOL_MAJOR; hello.guest_page_size = PVG_PAGE_SIZE;
    hello.guest_os_build = 25u << 16; hello.guest_features = PVG_GUEST_METAL_PLUGIN | PVG_GUEST_VENUS;
    std::snprintf(hello.guest_name, sizeof hello.guest_name, "host_tests");
    SendMsg(*c, PVG_MSG_HELLO, 2, &hello, sizeof hello);
    const bool gotCaps = ReadMsg(*c, h, p) && h.type == (PVG_MSG_CAPS | PVG_MSG_REPLY) && h.sequence == 2;
    CHECK(gotCaps, "HELLO answered with CAPS");
    if (gotCaps) {
        pvg_caps_header ch{}; std::memcpy(&ch, p.data(), sizeof ch);
        CHECK(ch.adapter_count == report.adapters.size(), "CAPS carries every host adapter");
        CHECK(p.size() == sizeof ch + ch.adapter_count * sizeof(pvg_adapter_caps) + ch.venus_capset_size, "CAPS size is exact");
        CHECK(!(ch.host_flags & PVG_HOST_VENUS_AVAILABLE), "null renderer does not claim Venus");
        for (uint32_t i = 0; i < ch.adapter_count; ++i) {
            pvg_adapter_caps a{}; std::memcpy(&a, p.data() + sizeof ch + i * sizeof a, sizeof a);
            std::printf("  guest sees adapter %u: %s (metal 0x%x, features 0x%llx)\n", i, a.device_name, a.metal, static_cast<unsigned long long>(a.features));
        }
    }
    SendMsg(*c, PVG_MSG_CONTEXT_INIT, 3, &early, sizeof early);
    CHECK(ReadMsg(*c, h, p) && h.type == (PVG_MSG_ERROR | PVG_MSG_REPLY), "context refused without a renderer");
    pvg_error e{}; if (p.size() >= sizeof e) std::memcpy(&e, p.data(), sizeof e);
    CHECK(e.code == -19, "refusal reports ENODEV");
    c->Close();
    server.join();
}

int main() {
    TestMetalPolicy();
    TestPreferred();
    TestDiff();
    TestLoopback();
    HostReport r = ProbeHost();
    std::printf("%s\n", ToJson(r).c_str());
    std::printf("%d/%d host checks passed\n", passes, passes + failures);
    return failures ? 1 : 0;
}
