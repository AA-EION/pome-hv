// Pome Virtual GPU host: Vulkan capability probe. SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <string>
#include <vector>
#include "pvg_wire.h"

namespace pvg {

struct AdapterReport {
    pvg_adapter_caps caps{};
    std::vector<std::string> extensions;
    std::vector<std::string> notes;   // why a Metal class was or was not granted
};

struct HostReport {
    std::vector<AdapterReport> adapters;
    uint32_t preferred = 0;
    uint32_t loader_api_version = 0;
    std::string error;                // empty on success
};

// Queries every Vulkan physical device through the system loader (vulkan-1.dll).
HostReport ProbeHost();

// Pure policy, unit-tested: Vulkan features -> Metal capability classes.
uint32_t MetalClassesFor(const pvg_adapter_caps& caps, std::vector<std::string>* notes);
uint32_t PreferredAdapter(const std::vector<AdapterReport>& adapters);

std::string ToJson(const HostReport& report);
std::vector<uint8_t> SerializeCaps(const HostReport& report, uint32_t host_flags, const std::vector<uint8_t>& venus_capset);

const char* FeatureName(uint64_t bit);
const char* MetalClassName(uint32_t bit);

} // namespace pvg
