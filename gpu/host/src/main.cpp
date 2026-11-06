// Pome Virtual GPU host companion (pvg-host). SPDX-License-Identifier: MIT
//
//   pvg-host --probe [--json]              report what each host GPU can give macOS
//   pvg-host --vm <vm-guid> [--adapter N]   serve one VM over its Hyper-V socket (started by pome)
//   pvg-host --tcp <port>                   serve one development client on 127.0.0.1
//   pvg-host --selftest                     check the Venus back end (libvirglrenderer-1.dll) with no guest
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include "caps.hpp"
#include "renderer.hpp"
#include "session.hpp"
#include "transport.hpp"

using namespace pvg;

// Drives the Venus back end without a guest: library load, capset, one context and a mapped shmem
// blob (the object every Venus ring lives in). Exit code 0 means pvg-host can serve a VM.
static int SelfTest() {
    std::string error;
    auto r = CreateVenusRenderer(0, [](uint32_t, uint32_t, uint64_t) {}, &error);
    if (!r) { std::printf("venus: unavailable: %s\n", error.c_str()); return 1; }
    const auto caps = r->VenusCapset();
    std::printf("venus: capset %zu bytes", caps.size());
    if (caps.size() >= 8) {
        uint32_t wire = 0, xml = 0;
        std::memcpy(&wire, caps.data(), 4); std::memcpy(&xml, caps.data() + 4, 4);
        std::printf(" (wire format %u, vk.xml %u.%u.%u)", wire, xml >> 22, (xml >> 12) & 0x3ff, xml & 0xfff);
    }
    std::printf("\n");
    if (int e = r->CreateContext(1, kCapsetVenus, "pvg-selftest"); e) { std::printf("venus: context: %d\n", e); return 1; }
    BlobMapping m;
    if (int e = r->CreateBlob(1, 1, kBlobMemHost3D, kBlobFlagMappable, 0, 64 * 1024, &m); e || !m.host_ptr) {
        std::printf("venus: shmem blob: %d\n", e); r->DestroyContext(1); return 1;
    }
    auto* bytes = static_cast<volatile uint8_t*>(m.host_ptr);
    bytes[0] = 0x5a; bytes[m.size - 1] = 0xa5;
    const bool ok = bytes[0] == 0x5a && bytes[m.size - 1] == 0xa5;
    std::printf("venus: shmem blob mapped, %llu bytes, map_info %u, %s\n", static_cast<unsigned long long>(m.size), m.map_info, ok ? "read back" : "MISMATCH");
    r->Unref(1);
    if (r->CreateContext(1, 3 /* not Venus */, "x") == 0) { std::printf("venus: accepted a non-Venus capset\n"); return 1; }
    r->DestroyContext(1);
    std::printf("venus: ok\n");
    return ok ? 0 : 1;
}

static void PrintHuman(const HostReport& r) {
    if (!r.error.empty()) std::printf("error: %s\n", r.error.c_str());
    for (const auto& a : r.adapters) {
        const auto& c = a.caps;
        std::printf("[%u]%s %s  (vendor 0x%04x device 0x%04x, Vulkan %u.%u, %s, %.1f GB local)\n", c.index,
                    c.index == r.preferred ? " *" : "  ", c.device_name, c.vendor_id, c.device_id,
                    c.api_version >> 22, (c.api_version >> 12) & 0x3ff, c.driver_name, c.device_local_bytes / 1073741824.0);
        std::printf("     Metal:");
        for (int b = 0; b < 32; ++b) if (c.metal & (1u << b)) if (const char* n = MetalClassName(1u << b)) std::printf(" [%s]", n);
        std::printf("\n");
        for (const auto& n : a.notes) std::printf("     - %s\n", n.c_str());
    }
}

int main(int argc, char** argv) {
    bool probe = false, json = false, verbose = false;
    std::string vm; int tcp = 0; int adapter = -1;
    for (int i = 1; i < argc; ++i) {
        const std::string a = argv[i];
        if (a == "--probe") probe = true;
        else if (a == "--json") json = true;
        else if (a == "--verbose" || a == "-v") verbose = true;
        else if (a == "--vm" && i + 1 < argc) vm = argv[++i];
        else if (a == "--tcp" && i + 1 < argc) tcp = std::atoi(argv[++i]);
        else if (a == "--adapter" && i + 1 < argc) adapter = std::atoi(argv[++i]);
        else if (a == "--selftest") return SelfTest();
        else { std::fprintf(stderr, "usage: pvg-host --probe [--json] | --selftest | --vm <guid> [--adapter N] | --tcp <port>  [-v]\n"); return 2; }
    }
    HostReport report = ProbeHost();
    if (adapter >= 0 && static_cast<size_t>(adapter) < report.adapters.size()) report.preferred = static_cast<uint32_t>(adapter);
    if (probe || (vm.empty() && !tcp)) {
        if (json) std::printf("%s\n", ToJson(report).c_str()); else PrintHuman(report);
        return report.error.empty() ? 0 : 1;
    }
    std::string error;
    if (!InitSockets(&error)) { std::fprintf(stderr, "pvg-host: %s\n", error.c_str()); return 1; }
    std::unique_ptr<Stream> stream = !vm.empty() ? ConnectHyperV(vm, 10 * 60 * 1000, &error) : ListenTcpOnce(static_cast<uint16_t>(tcp), &error);
    if (!stream) { std::fprintf(stderr, "pvg-host: %s\n", error.c_str()); return 1; }
    if (verbose) std::fprintf(stderr, "pvg-host: connected %s\n", stream->Describe().c_str());

    Session* session = nullptr;
    std::string rendererError;
    auto renderer = CreateVenusRenderer(report.preferred,
        [&session](uint32_t c, uint32_t r, uint64_t f) { if (session) session->OnFence(c, r, f); }, &rendererError);
    if (!renderer) {
        if (verbose) std::fprintf(stderr, "pvg-host: Venus unavailable (%s); serving capabilities only\n", rendererError.c_str());
        renderer = CreateNullRenderer();
    }
    Session s(std::move(stream), std::move(report), std::move(renderer), verbose);
    session = &s;
    const std::string reason = s.Run();
    const auto& st = s.Stats();
    std::fprintf(stderr, "pvg-host: session ended (%s): in %llu B, out %llu B, %llu execbuffers, %llu fences, %llu pushes\n", reason.c_str(),
                 static_cast<unsigned long long>(st.bytes_in), static_cast<unsigned long long>(st.bytes_out),
                 static_cast<unsigned long long>(st.execbuffers), static_cast<unsigned long long>(st.fences), static_cast<unsigned long long>(st.pushes));
    return 0;
}
