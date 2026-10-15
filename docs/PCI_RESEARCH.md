# Hyper-V PCI / high-MMIO investigation

Audited 2026-10-15 against MacHyperVSupport commit `cafcd34daea5e58633112a1e63cef1f07bbc8404`. This is a source investigation with reviewable patch candidates, **not a validated passthrough driver**.

## Root cause

The issue is not that the entire virtual PCI bus is 32-bit. `HyperVPCIBridge::queryResourceRequirements` combines two probe DWORDs for a 64-bit BAR, stores addresses and lengths in UInt64 arrays, and can write the BAR's high DWORD. However, every BAR allocation currently calls the root allocator with `UINT32_MAX`, including 64-bit BARs. A large GPU aperture consequently competes for the below-4-GiB range or cannot fit.

The [bridge allocation source](https://github.com/acidanthera/MacHyperVSupport/blob/cafcd34daea5e58633112a1e63cef1f07bbc8404/MacHyperVSupport/PCIBridge/HyperVPCIBridgePrivate.cpp) explicitly records the unresolved 64-bit allocation issue. The separate PCI configuration window is also allocated below 4 GiB; moving it is not necessary to experiment with high device BARs.

The [root source](https://github.com/acidanthera/MacHyperVSupport/blob/cafcd34daea5e58633112a1e63cef1f07bbc8404/MacHyperVSupport/PCIRoot/HyperVPCIRoot.cpp) already reads ACPI address-space descriptors and maintains low and high IORangeAllocators. It prefers high memory when `maxAddress > UINT32_MAX` and otherwise falls back low. Crucially, this argument currently chooses an allocator; it does not fully enforce the requested end address. The experimental caller therefore checks address-plus-length without overflow after allocation.

The bridge advertises device ranges to macOS through `addBridgeMemoryRange`, programs the two BAR DWORDs through the mapped PCI configuration window, then sends ResourcesAssigned over VMBus. These operations do not prove macOS accepted the mapping or that DMA, interrupts, reset and firmware loading work. Existing function/slot-zero assumptions remain: this is not a general multi-function PCI implementation.

## Concrete findings and patches

`patches/MacHyperVSupport/0001-pci-response-and-msi-correctness.patch`:

- Corrects the MSI high address DWORD from `address << 32` to `address >> 32`. The previous left-shift followed by UInt32 truncation produces zero. This can be invisible when MSI addresses happen to fit in 32 bits.
- Rejects a 64-bit BAR at the last BAR register before reading beyond the probe array.
- Propagates a failed resource-query or ResourcesAssigned response instead of treating transport success as device success.

`0002-opt-in-high-mmio-research.patch`:

- Enables high-MMIO requests only on x86_64, only for true 64-bit BARs, and only with the explicit guest research boot argument `-hvpcilargemmio`.
- Leaves 32-bit BARs and the PCI config window on the low allocator. Preserves the original default behavior.
- Records BAR width separately from its chosen address; a 64-bit BAR below 4 GiB is still a 64-bit BAR.
- Derives BAR size using the declared width rather than inferring width from its numerical mask; rejects malformed sizes and overflow/range violations.
- Fails bridge configuration if macOS rejects a memory aperture, instead of ignoring that result.

Both patches apply to the pinned source. They have **not** been compiled with the macOS kernel SDK, loaded in a guest or validated on physical hardware. The default packaged EFI does not contain them. The pure C# allocator tests exercise planning invariants, not execution of the kernel patches.

## Workarounds worth testing

| Approach | Evidence and limits |
|---|---|
| Use a smaller BAR aperture through documented vendor/firmware configuration | May allow a 64-bit-capable BAR to fit below 4 GiB. Must measure every function's actual resource requirements. Some driver/firmware combinations may require a larger aperture. Never infer a BAR size from VRAM capacity. |
| Disable ReBAR only through a supported firmware setting, with user consent | A possible experiment, not an automated fix. A MUX is unrelated to BAR capacity. Pome performs no firmware writes. |
| Allocate sufficient low MMIO on an eligible Server VM | Can help only when all required low resources fit. It cannot make an 8/16 GiB BAR fit below 4 GiB. Do not copy tutorial constants. |
| Enable measured high MMIO and test the opt-in guest patch | Architecturally plausible because high ACPI ranges already exist. Host GPA grants, IOPCI relocation and guest driver operation must still be demonstrated. |
| Windows client DDA unlock or substituting GPU-P | No validated solution established. No such bypass is included. GPU-P does not satisfy the physical PCI requirements of this driver. |

NullMoth's current `docs/CARD-SUPPORT.md` explicitly states that its fallback startup timing was exercised on an RTX 5060 with a forced delay, **not a physically small-BAR card**; ReBAR enabled is still its tested setup. Therefore disabling ReBAR is only a controlled research candidate and may remove a configuration on which the driver depends.

## Remaining engineering gates

Before any patched driver is considered usable, implement/review transactional BAR allocation and teardown across all failure paths (the current bridge does not provide sufficient rollback), strict ACPI range admission including crossing/overlapping apertures, allocator concurrency, resource-reservation cleanup, and multiple PCI functions. Verify that endpoint BAR type bits remain correct after programming. The opt-in patch alone is intentionally not promoted as the complete fix.

Instrument the exact advertised ACPI ranges, requested probe masks, allocated GPA intervals, BAR low/high readback, ResourcesAssigned device status, `addBridgeMemoryRange` result, MSI address/data, IOMemoryDescriptor mappings and driver matching. Avoid broad telemetry dumps containing machine identifiers.

Then test in this order:

1. Build against the pinned upstream source with Xcode/SDK; preserve the unmodified EFI and boot entry.
2. With an eligible Server test host and explicit device approval, capture SurveyDDA and the complete GPU function/resource inventory. Verify a separate host display and recovery access.
3. Begin with a small aperture and the original guest driver, recording the failure stage.
4. Test the correctness patch alone, then the high-MMIO patch with its flag absent, then present. Restore the original boot entry between experiments.
5. Verify ACPI high aperture, BAR readback and guest PCI enumeration. Then independently verify interrupt delivery, DMA, GSP firmware, NVRM/NVAccel, Metal enumeration, a real rendering workload and stability.
6. Exercise failure at allocation, resource acknowledgement, driver match and shutdown. Prove host device restoration before expanding the matrix.

NullMoth's [OS shim](https://github.com/nullmoth/nvidia-macos-driver/blob/d96eaca3f47e748ce5b1f5fa3913a85e48f1f5fb/kexts/NVRM/os-xnu.cpp) uses IOPCIDevice discovery and GPU memory mapping. Its reported KVM success does not validate Hyper-V's topology, resource assignment or reset path. Lilu's [bridge issue](https://github.com/acidanthera/bugtracker/issues/2299) remains a separate detection concern; the reviewed traversal keys on PCI bridge class information. No fake topology injection is included.
