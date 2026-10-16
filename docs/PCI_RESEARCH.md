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

`0003-reserve-bars-before-programming.patch`:

- Refuses to reprogram BARs when memory decoding or bus mastering is already active in `kIOPCIConfigCommand` (`0x0006`), returning `kIOReturnBusy`.
- Rejects unsupported PCI memory BAR type encodings (`memoryType != 0 && memoryType != 4`).
- Reserves all required BAR apertures from `HyperVPCIRoot` before writing any BAR register to the device, and frees all reserved ranges cleanly if any BAR allocation fails.

`0004-acpi-admission-locking-and-teardown.patch`:

- Validates ACPI memory descriptors in `HyperVPCIRoot::start` before admitting them to the low or high `IORangeAllocator`: rejects zero base/length, `UInt64` addition overflow, `maxAddressRange < endAddr`, ranges crossing the 4 GiB boundary (`minAddr <= UINT32_MAX && endAddr > UINT32_MAX`), and overlapping descriptors.
- Protects `_rangeAllocatorLow` and `_rangeAllocatorHigh` allocations and deallocations under `_pciLock`, and releases both allocators in `HyperVPCIRoot::stop`.
- Preserves low attribute bits (`pktReqResponse.probedBARs[i] & 0x0FU`) when writing BARs, verifies low and high DWORD readback against the programmed address and memory type flags, and zeroes/frees BARs on readback mismatch.
- Implements `HyperVPCIBridge::stop` to disable memory/bus-master decoding, return allocated BAR and config-window ranges to `HyperVPCIRoot`, and release descriptors, function tables, locks and VMBus device references.

All four patches apply sequentially to fresh copies of the pinned source (`cafcd34daea5e58633112a1e63cef1f07bbc8404`). `tools/Validation/TestPciPatches.py` verifies `git apply --check`, applies the series, compiles the extracted patched `HyperVPCIBridge::queryResourceRequirements` and `getBarSize` C++ implementation with MSVC (`cl.exe`), and passes **21 allocation, fault-injection, active-decoder, and BAR readback verification scenarios** against fake IOKit/VMBus boundaries. Full kext compilation still requires a macOS Xcode/Kernel SDK, and physical hardware passthrough remains unverified. The default packaged EFI (`OSX-Hyper-V 0.4.0`, carrying `MacHyperVSupport 0.9.10`) does not contain these patches.

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

Before any patched driver is considered usable on hardware, validate VMBus host resource-release acknowledgement (`kHyperVPCIBridgeMessageTypeBusD0Exit` / `ResourcesReleased`) during live device stop, multi-function (`slot != 0`) enumeration and config-window routing, full Xcode/macOS SDK compilation, and actual Hyper-V Server DDA behavior. The opt-in patch series is a source-level engineering candidate, not a certified passthrough release.

Instrument the exact advertised ACPI ranges, requested probe masks, allocated GPA intervals, BAR low/high readback, ResourcesAssigned device status, `addBridgeMemoryRange` result, MSI address/data, IOMemoryDescriptor mappings and driver matching. Avoid broad telemetry dumps containing machine identifiers.

Then test in this order:

1. Build against the pinned upstream source with Xcode/SDK; preserve the unmodified EFI and boot entry.
2. With an eligible Server test host and explicit device approval, capture SurveyDDA and the complete GPU function/resource inventory. Verify a separate host display and recovery access.
3. Begin with a small aperture and the original guest driver, recording the failure stage.
4. Test the correctness patch alone, then the high-MMIO patch with its flag absent, then present. Restore the original boot entry between experiments.
5. Verify ACPI high aperture, BAR readback and guest PCI enumeration. Then independently verify interrupt delivery, DMA, GSP firmware, NVRM/NVAccel, Metal enumeration, a real rendering workload and stability.
6. Exercise failure at allocation, resource acknowledgement, driver match and shutdown. Prove host device restoration before expanding the matrix.

NullMoth's [OS shim](https://github.com/nullmoth/nvidia-macos-driver/blob/d96eaca3f47e748ce5b1f5fa3913a85e48f1f5fb/kexts/NVRM/os-xnu.cpp) uses IOPCIDevice discovery and GPU memory mapping. Its reported KVM success does not validate Hyper-V's topology, resource assignment or reset path. Lilu's [bridge issue](https://github.com/acidanthera/bugtracker/issues/2299) remains a separate detection concern; the reviewed traversal keys on PCI bridge class information. No fake topology injection is included.
