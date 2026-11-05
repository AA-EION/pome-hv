# GPU partitioning (GPU-P) for macOS guests

Research checkpoint: 2026-11-05. Sources: NullMoth `nvidia-macos-driver` v2.7.2 (`18ba05f`), Microsoft's dxgkrnl (`microsoft/WSL2-Linux-Kernel`, `linux-msft-wsl-6.6.y`, `drivers/hv/dxgkrnl`), ExHyperV `f51e066` (`src/Services/Vm/VmGpuService.cs`, `doc/Linux.md`, `doc/drivermapping.md`), NullMoth issue [#417](https://github.com/nullmoth/nvidia-macos-driver/issues/417) (open, no maintainer reply) and [#228](https://github.com/nullmoth/nvidia-macos-driver/issues/228) (NVIDIA vGPU: "Not planned").

## What a GPU-P guest actually receives

GPU-P is **WDDM GPU paravirtualization**, not a PCI function of the card. The guest gets:

| Interface | Identity | Purpose |
|---|---|---|
| Virtual PCI device (VMBus vPCI) | `1414:008E` Microsoft virtual render device (`1414:008A` compute-only) | Config-space mailbox: guest caps at `DXGK_VMBUS_GUESTCAPS_OFFSET`, interface version, vGPU LUID, host caps (192+ in config space) |
| Global VMBus channel | `{dde9cbc0-5060-4436-9448-ea1254a5d177}` | Adapter enumeration, process/global commands |
| Per-vGPU VMBus channel | `{6e382d18-3336-4f4b-acc4-2b7703d4df4a}` | ~90 D3DKMT commands: devices, contexts, allocations, paging queues, sync objects, submits |

All physical GPU work happens on the Windows host. The guest kernel driver forwards D3DKMT calls; the **host's own WDDM kernel driver** (`nvlddmkm.sys` for NVIDIA) executes them.

The decisive detail is in `dxgvmbus.h`: `CreateAllocation`, `CreateContext`, `SubmitCommand` and `SubmitCommandToHwQueue` all carry **`PrivateDriverData`**, an opaque blob that the host's vendor KMD interprets. Its format is NVIDIA's private UMD↔KMD ABI. Only NVIDIA's closed user-mode drivers produce it.

## How the working guests do it

- **Windows guests** (ExHyperV): copy the host's DriverStore package into `System32\HostDriverStore`, so the guest runs NVIDIA's own Windows UMDs (`nvwgf2umx.dll`, `nvcuda`, …) over Microsoft's virtual render KMD.
- **Linux guests** (ExHyperV, WSL): build Microsoft's GPL-2.0 `dxgkrnl`, then copy `/usr/lib/wsl/lib` and the host package. These are Microsoft's closed `libd3d12.so`/`libdxcore.so` and NVIDIA's closed Linux ELF UMDs (`libnvwgf2umx.so`, `libcuda.so`, `nvidia-smi`). Mesa's `d3d12` (OpenGL) and `dzn` (Vulkan) are open but sit on top of the closed `libd3d12.so`.
- **BSD**: no port exists. FreeBSD's drm-kmod carries only the AMD and Intel DRM drivers ([status report](https://www.freebsd.org/status/report-2026-04-2026-06/drm-drivers/)).
- **Upstream Linux**: the dxgkrnl v4 series (55 patches, March 2026) is still in review ([LKML](https://lkml.iu.edu/2603.2/09609.html)).

**No open-source stack produces NVIDIA's `PrivateDriverData`.** Every working guest runs NVIDIA's closed binaries.

## Why NullMoth's current path cannot use GPU-P

NullMoth is `Metal → NVMTLDriver.bundle → AIR→SPIR-V translator → NVK → NVRM.kext → GSP firmware → PCI GPU`. NVRM claims the NVIDIA PCI device, maps BAR0/BAR1 and boots GSP with `rm_init_adapter()`. NVK submits raw NVIDIA push buffers through NVRM's uAPI.

Under GPU-P there is no NVIDIA PCI device, no BAR, no GSP and no channel to program. The GPU stays owned by the host driver. Skipping GSP boot (issue #417's option 1) therefore has nothing to attach to. SR-IOV VFs would be a different feature (NVIDIA vGPU, refused in #228), not Hyper-V client GPU-P.

## Routes to acceleration, ranked

1. **Vulkan API remoting over VMBus (recommended).** Add a remote Vulkan ICD beside NVK: the same idea as Mesa **Venus** or Android's **gfxstream**. It serializes Vulkan calls over a VMBus pipe or hv_sock to a pome host service that replays them on the host's real NVIDIA Vulkan driver.
   - NullMoth's plugin already targets Vulkan (`nvmtl_vk.c`), so Metal-over-Vulkan and the AIR→SPIR-V translator are reused unchanged.
   - It needs no GPU-P, no private NVIDIA ABI, and works with any host GPU.
   - It does not interfere with other cards: the ICD is chosen only when no NVIDIA PCI device exists and the pome channel is offered.
   - Work: a VMBus pipe/hv_sock device in MacHyperVSupport, the guest ICD, a Windows host renderer, and shared memory via GPADL plus a present path into the guest framebuffer. pome's earlier Reims vGPU audit (`GPU_ACCELERATION_RESEARCH.md`) covers the related QEMU route.
2. **macOS dxgkrnl port ("HyperVDxg.kext"), research only.** A MacHyperVSupport-style kext matching `1414:008E` and both channels, exposing D3DKMT through an IOUserClient. It is feasible (dxgkrnl is about 450 KB of C), but it must be GPL-2.0 and kept separate from pome (MIT) and NullMoth (PolyForm Noncommercial). On its own it gives adapter enumeration, memory, sync objects and scheduling, but **no rendering**: a UMD must still create NVIDIA `PrivateDriverData` and command buffers. The options for that are:
   - NVIDIA's Linux ELF UMDs: not loadable on macOS, and licensed for WSL use.
   - Reverse-engineering NVIDIA's WDDM private ABI so NVK can submit through dxg: version-locked to the host driver, very large, and legally uncertain.
   - Microsoft's `libd3d12.so` under a Metal-on-D3D12 layer: closed Linux binaries, the same blocker.
3. **DDA (whole-GPU passthrough).** NullMoth works unchanged on a real PCI GPU, but Hyper-V supports DDA only on Windows Server. It also needs pome's high-MMIO patches (0002–0004) and IOMMU-safe hardware.

## What pome implements now (Tahoe only)

- Creation writes `guest.json` (catalog version and CPU profile) to the VM's own `pome-…` folder. GPU-P is offered only when it records macOS 26 Tahoe.
- **Hardware → GPU partition (experimental)** lists `Get-VMHostPartitionableGpu` (matched to adapter names) and attaches or removes a partition on a stopped, checkpoint-free VM. This is the same host configuration ExHyperV applies:
  - guest-controlled cache types
  - a 1 GB low and 32 GB high MMIO gap
  - checkpoints disabled
  - `Add-VMGpuPartitionAdapter -InstancePath`
- The previous settings are saved to `gpu-partition.json` and restored on removal.
- Nothing is installed in the guest, and the UI states that graphics stay on the synthetic display.

First guest milestone: boot a Tahoe VM with a partition and run `ioreg -l | grep -i 1414` or `system_profiler SPPCIDataType`. That confirms whether MacHyperVSupport's vPCI bridge exposes `1414:008E` to IOPCIFamily, and whether both VMBus channels appear in its logs. Its high-MMIO limitations (`PCI_RESEARCH.md`) may affect the device's BAR placement.

## Contribution note

NullMoth's `AGENTS.md` asks that pull requests, issues and review comments be written by the contributor personally; AI-written ones are closed. Anything proposed upstream from this research must be authored by you.
