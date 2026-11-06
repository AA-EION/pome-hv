// Pome Virtual GPU host: capability-only renderer. SPDX-License-Identifier: MIT
#include "renderer.hpp"

namespace pvg {
namespace {
constexpr int kENODEV = -19;
class NullRenderer final : public Renderer {
public:
    const char* Name() const override { return "none"; }
    std::vector<uint8_t> VenusCapset() override { return {}; }
    int CreateContext(uint32_t, uint32_t, const std::string&) override { return kENODEV; }
    void DestroyContext(uint32_t) override {}
    int CreateBlob(uint32_t, uint32_t, uint32_t, uint32_t, uint64_t, uint64_t, BlobMapping*) override { return kENODEV; }
    void Unref(uint32_t) override {}
    int Submit(uint32_t, const void*, uint32_t) override { return kENODEV; }
    int CreateFence(uint32_t, uint32_t, uint64_t) override { return kENODEV; }
    void Poll() override {}
};
} // namespace
std::unique_ptr<Renderer> CreateNullRenderer() { return std::make_unique<NullRenderer>(); }
} // namespace pvg
