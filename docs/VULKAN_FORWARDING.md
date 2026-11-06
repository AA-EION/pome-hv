# Vulkan forwarding for Metal in Hyper-V macOS guests

> Implemented as the Pome Virtual GPU: see [gpu/README.md](../gpu/README.md). This page is the
> research that led there. Since then the plan changed: no Linux guest first; the accelerator is
> `PVGAccel` (not `NMVAccel`); the guest talks to the kext through `vn_renderer_pvg` instead of a DRM
> node shim; and the host loads vkr in-process instead of porting the render server.

Research checkpoint: 2026-11-05. The goal is real Metal in a Tahoe guest, including ray/path tracing and ML, on the host's NVIDIA GPU, without touching NullMoth's real-card paths. Sources were read locally: Mesa `0c10684` (`src/virtio/vulkan`), virglrenderer `244fc1a` (`src/venus`), gfxstream `77190df`, NullMoth `nvidia-macos-driver` v2.7.2 (`plugin/nvmtl_vk.c`, `kexts/NMDRM`) and MacHyperVSupport 1.0.0.

## Existing forwarding projects

| Project | What it forwards | Ray tracing | Host on Windows | License | Fit |
|---|---|---|---|---|---|
| **Mesa Venus + virglrenderer `vkr`** | Full Vulkan over virtio-gpu (or the `vtest` socket renderer) | **Yes.** The guest encodes `vkCmdBuildAccelerationStructures*KHR` and `vkCmdTraceRays*KHR` ([Mesa 25.1](https://www.phoronix.com/news/Mesa-25.1-Venus-Vulkan-RT)), plus ray query, mesh shaders and `KHR_cooperative_matrix` | **No.** Linux and macOS hosts; `src/venus` has no `_WIN32` paths and uses fds/memfd/dma-buf about 80 times | MIT | Best protocol; the host needs a Windows port |
| **gfxstream** (Google, formerly Vulkan Cereal; guest in Mesa) | GLES + Vulkan, VM→host, IPC or sockets | **No.** The guest encoder has no ray-tracing commands; they appear only in generated dispatch tables | **Yes.** Documented CMake/ClangCL Windows build of `gfxstream_backend` (the Android Emulator renderer) | Apache-2.0 | Windows host is ready; ray tracing is missing in codegen and decoder |
| [VirtualVulkan](https://github.com/CatalinTermure/VirtualVulkan) | Selected Vulkan calls over gRPC | No | Partial | — | Experimental, single author |
| llama.cpp `ggml-virtgpu` / APIR | Compute only, through virglrenderer API remoting | n/a | No | MIT | Compute only; [upstream pending](https://sources.debian.org/src/llama.cpp/8064%2Bdfsg-1/docs/backend/VirtGPU.md) |

**Recommendation: Venus.** It is the only forwarding stack that already carries everything NullMoth's Metal layer asks Vulkan for: `KHR_acceleration_structure`, `KHR_ray_query`, `KHR_cooperative_matrix`, `KHR_deferred_host_operations`, mutable descriptors and barycentrics (from `plugin/nvmtl_vk.c`). gfxstream's Windows host is tempting, but adding ray tracing there means new codegen and host state tracking for acceleration structures, shader binding tables and buffer device addresses. Porting `vkr` to Windows is narrower: replace fd export and import with `VK_KHR_external_memory_win32`/`_semaphore_win32` and handles.

## Why this fits NullMoth without disturbing other cards

`plugin/nvmtl_vk.c` already selects its Vulkan ICD by accelerator: `NVAccel` → NVK, `NMXAccel` + PCI vendor `0x1002`/`0x8086` → RADV/ANV. `kexts/NMDRM` exposes Linux-style `/dev/dri/renderD128` nodes so Mesa drivers run unmodified. A VM back end is one more case:

```
Metal app → NVMTLDriver.bundle (unchanged Metal-on-Vulkan, AIR→SPIR-V translator)
          → Venus ICD (Mesa vn, built for macOS)           [new build; MIT]
          → /dev/dri/renderD128 virtio-gpu ioctls          [new NMDRM-style kext]
          → Hyper-V socket / VMBus                         [new: MacHyperVSupport has VMBus + GPADL, no sockets]
Windows host: pome render service = virglrenderer vkr      [Windows port]
          → NVIDIA Vulkan driver: RT cores, tensor cores, mesh shaders
```

It is selected only when a new `NMVAccel` accelerator is present (no NVIDIA, AMD or Intel PCI GPU). NVRM, NVK, RADV and ANV on real hardware are untouched.

## Feature mapping

- **Ray tracing / path tracing.** Metal `MTLAccelerationStructure`, intersection functions and ray queries already map to Vulkan RT in NullMoth (37 plugin/translator files). Venus forwards those commands; BVH builds and traversal run on the host GPU.
- **ML.** Core ML, MPS/MPSGraph, MLX and PyTorch's MPS backend are Metal compute. They work wherever the Metal driver works. Metal simdgroup matrices reach tensor cores through `VK_KHR_cooperative_matrix`, which Venus advertises. Intel-Mac macOS has no Neural Engine, so Core ML uses the GPU path.
- **CUDA.** There is no CUDA toolkit for current macOS (the last was 10.2). Options, in order of usefulness:
  1. ML through Metal (above), which covers most real workloads;
  2. CUDA→Metal compilation such as [CuMetal](https://github.com/Lulzx/cuda-metal) (Apache-2.0, CUDA C++/PTX → MSL). It targets Apple Silicon and its unified memory, so it needs x86 adaptation; its MSL output would then run through NullMoth's Metal layer;
  3. CUDA API remoting ([GVirtuS](https://github.com/gvirtus/GVirtuS), [Cricket](https://github.com/mghgm/cricket)) to the host's real CUDA. This is exact, but it needs macOS-built CUDA client applications, and both send one call per round trip.

  Full transparent CUDA in macOS is not realistic. Compiling CUDA to Metal is the closest.

## The hard parts

1. **Transport bandwidth.** Hyper-V sockets have measured about 0.3–1.1 GB/s ([FreeBSD hvsock review](https://reviews.freebsd.org/D24061)). That is enough for Venus command streams, but not for large uploads or for copying frames back. Venus expects mappable host-visible memory (blob resources). Shared memory on Hyper-V needs GPADL pages mapped by a host **kernel** VSP (VMBus kernel-mode client library), which means a signed Windows driver. Start with sockets plus copy-on-flush, then add the VSP.
2. **Presentation.** WindowServer composites through an `IOAccelerator`. `NMVAccel` must back IOSurfaces with forwarded images. The fastest display is gfxstream-style: present on the host into pome's own console window (no readback), instead of VMConnect's synthetic framebuffer.
3. **Guest builds.** Mesa Venus is built for Linux/Android DRM; NMDRM's approach (libdrm-compatible nodes) avoids porting the Mesa renderer layer. `vn_renderer_vtest.c` (AF_UNIX + SCM_RIGHTS + mmap) is the model for a socket-based renderer if a DRM shim is not wanted.
4. **Sockets in the guest.** Port FreeBSD's BSD-licensed hvsock (the Linux `hyperv_transport.c` is GPL) into a MacHyperVSupport-style kext. Register the pome service GUID under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices`.

## Plan

1. **De-risk with a Linux guest first.** On this host, run a Linux Hyper-V VM with stock Venus over a vtest-over-hvsock bridge to a Windows `vkr` port. That proves Windows-host RT forwarding (vkcube, `vk_ray_tracing` samples, a path tracer) and measures throughput, with no macOS work.
2. **macOS transport.** hvsock kext plus an NMDRM-style virtio-gpu node; run headless Vulkan compute and RT tests in Tahoe.
3. **Metal.** Add an `NMVAccel` case to the plugin's ICD switch; run Metal compute, MPS and RT samples headless.
4. **Display.** `NMVAccel` IOSurfaces, with host-side presentation in pome's window.
5. **Performance.** A GPADL shared-memory VSP (signed host driver), and staging-ring tuning.

This does not use Hyper-V GPU-P at all, so it works with any host GPU, alongside a GPU partition or without one.
