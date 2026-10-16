# Metal acceleration with ordinary host GPU drivers

Research checkpoint: 2026-10-15. No accelerator was installed or tested locally.

There is a credible experimental route using a normal Windows Vulkan driver. It needs an emulated Apple GPU device and a translation layer; installing a Windows NVIDIA driver inside macOS cannot provide that interface. It is not currently integrated with Pome's Hyper-V VM backend.

| Route | Evidence | Pome implication |
|---|---|---|
| Apple paravirtual graphics | Apple's framework provides host acceleration to the graphics driver included in macOS guests. | Establishes a guest-driver interface; Apple's host framework is not a Windows implementation. [Apple documentation](https://developer.apple.com/documentation/paravirtualizedgraphics) |
| Reims vGPU | Replaces the host side of that interface and translates guest GPU work through Metal or Vulkan. Its current README describes QEMU/KVM on Linux and HVF on macOS. | A custom virtual device plus host backend is necessary. A Hyper-V synthetic framebuffer is not that device. [Project](https://github.com/steelbrain/reims-vgpu) |
| Windows Reims port | PR #57 was merged August 31, 2026. The author demonstrated QEMU/WHPX, native NVIDIA Vulkan initialization, a presented frame and firmware-to-kernel handoff; that PR also reported a WHPX exit-code-4 boot blocker. | This is a concrete Windows attempt, with explicitly incomplete boot evidence at that revision. [Merged PR](https://github.com/steelbrain/reims-vgpu/pull/57) |
| Metal API emulator experiment | A later September 19 report describes a live Ventura guest on Windows 11/QEMU/WHPX through a Reims fork. It reports about 96% of sampled draw records handled by its provider, remaining work falling back, and roughly 1.1-second presentation intervals in the census workload. | Evidence of active Windows guest work, not general Metal conformance or acceptable interactive performance. Its provider architecture is separate from upstream Reims. [Author's report](https://github.com/Hi-Jiajun/metal-api-emulator#live-vm-path-windows--whpx--reims-vgpu) |
| Hyper-V GPU-P / DDA | Microsoft's supported GPU-P guest list does not include macOS; its documentation restricts supported DDA/GPU-P deployments to the stated server/hardware matrix. | Existing Windows guest driver-copy techniques do not establish a macOS driver. Physical passthrough additionally needs a compatible macOS GPU driver, high-MMIO, interrupts, DMA and safe device ownership. [Microsoft guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-server/virtualization/troubleshoot-hyper-v-gpu-assignment-partitioning-passthrough-issues) |

The local Reims source snapshot (`c099dd5a4d61107a5527efc64cf1d1ab2f3a9b85`) was audited directly at the crate level:

- `crates/reims-vgpu/src/lib.rs` defines `DefaultPlatform` only for `#[cfg(target_os = "macos")]` (`MetalPlatform`) and `#[cfg(target_os = "linux")]` (`VulkanPlatform`).
- `crates/reims-vgpu-vulkan/src/os.rs` compiles `mod linux` or `mod macos` only, and depends on `std::os::fd::OwnedFd` (`mmap` shared file descriptors for `GuestRamFile`), `CLOCK_BOOTTIME` / `CLOCK_REALTIME`, and Linux/macOS surface extensions. There is no `mod windows` or Win32 file-mapping (`CreateFileMapping` / `MapViewOfFile`) implementation in this revision.
- `crates/reims-vgpu-window/src/native.rs` compiles `mod linux` (Wayland/X11 SDL3) or `mod macos` (AppKit/Cocoa) only; no Win32 surface (`VK_KHR_win32_surface`) or window module is present in `c099dd5`.
- Its license offers `GPL-2.0-or-later OR LGPL-3.0-or-later`; QEMU and dependencies retain their own terms.

Therefore, the refactored `c099dd5` tree does **not** build on `x86_64-pc-windows-msvc` out of the box. A Windows QEMU/WHPX backend requires a dedicated Windows OS/memory-mapping (`HANDLE` view aliasing) and `VK_KHR_win32_surface` port in `reims-vgpu-vulkan` and `reims-vgpu-window`, plus resolution of the WHPX exit-code-4 kernel handoff issue, and must remain isolated from Pome's MIT Hyper-V backend. Keep that investigation separate from NullMoth's physical NVIDIA driver and the MacHyperVSupport high-MMIO patches. The latter still matter for physical passthrough and do not supply a virtual Metal renderer.

Before integration: pin a working Windows-capable Reims/QEMU/translator branch; audit LGPL/GPL dynamic-linking and process isolation boundaries; reproduce Windows build and kernel boot; measure real rendering and compute output, UI responsiveness, resets and memory safety; document guest-version limitations. No acceleration capability should be advertised until those checks pass.
