# Pome Virtual GPU

Experimental Metal acceleration for macOS 26 Tahoe guests on Hyper-V, on **any** host GPU with a
Vulkan 1.3 driver (NVIDIA, AMD, Intel). The guest's Metal calls become Vulkan, Vulkan is forwarded
over a Hyper-V socket, and the host replays it on the real GPU. No GPU partition, no DDA and no
signed Windows driver are involved.

Status (2026-11-05): host side built and self-tested on this machine (Intel UHD + RTX 4050).
Guest side is written and builds in CI on macOS runners; it has **not yet run in a VM**.

```
macOS guest                                                    Windows host
───────────                                                    ────────────
Metal app / WindowServer-free compositing
  └ PVGMTLDriver.bundle   Metal on Vulkan + AIR→SPIR-V         pvg-host.exe  (gpu/host, MSVC)
      └ libvulkan_virtio  Mesa Venus ICD                         ├ caps: Vulkan probe → PVG_FEAT_* / Metal classes
          └ vn_renderer_pvg (gpu/guest/mesa)                     ├ session: HELLO/CAPS, blobs, mirrors, fences
              └ PVGTransport.kext  IOUserClient ─ Hyper-V socket ┤
                   (gpu/guest/kext)   VMBus pipe, PVG1           └ libvirglrenderer-1.dll  vkr (Venus decoder)
  PVGAccel.kext  IOAccelerator (headless)                              └ vulkan-1.dll → host GPU driver
```

## Pieces

| Path | What | License |
|---|---|---|
| `protocol/pvg_wire.h` | Wire protocol shared by guest and host | MIT |
| `host/` | `pvg-host`: capability probe, session, Venus back end | MIT |
| `host/virglrenderer/apply_windows.py` | Ports virglrenderer's `vkr` to Windows (MinGW-w64), in-process | MIT (virglrenderer: MIT) |
| `guest/kext/PVGTransport/` | VMBus pipe client, IOUserClient, mirrored blobs, timeline syncs | MIT |
| `guest/mesa/` | `vn_renderer_pvg.c` and the script that adds it to Mesa | MIT (Mesa: MIT) |
| `guest/metal/plugin`, `guest/metal/accel` | NullMoth's Metal layer and IOAccelerator, renamed NVMTL→PVGMTL | **PolyForm Noncommercial 1.0.0** |
| `guest/metal/translator` | metal2vulkan AIR→SPIR-V translator | **LGPL-3.0-or-later** |
| `guest/package/` | `install.sh`, `uninstall.sh`, manifests | MIT |

`guest/metal` is imported by `guest/metal/import_from_nullmoth.py` from NullMoth `18ba05f`; see
`guest/metal/NOTICE` for the Required Notice. **The PolyForm terms allow noncommercial use only.**
Building or shipping the guest package therefore carries that restriction; pome's own code stays MIT.

## How it works

**Transport.** pvg-host connects with `AF_HYPERV` to the VM's service
`50564731-facb-11e6-bd58-64006a7986d3` (the vsock template for port `PVG1`; pome registers it under
`HKLM\…\Virtualization\GuestCommunicationServices`). Hyper-V offers the guest a VMBus pipe channel;
PVGTransport matches it by `HVType`, opens 64-page rings and frames bytes as Linux `hv_sock` does
(`{u32 type = 1, u32 size}`, under 16 KiB per packet, zero size = EOF).

**Capabilities.** pvg-host enumerates every Vulkan device (deduplicated by device/driver UUID),
maps 40 Vulkan features to `PVG_FEAT_*` and from there to Metal classes (Mac2, Metal3, Metal 4
candidate, ray tracing, mesh, simdgroup matrix, tensors). The kext publishes them in the IORegistry
(`PVGAdapters`, `PVGPreferredAdapter`, `PVGPreferredDeviceUUID`, `PVGVenusCapset`). The Metal layer
opens the preferred device by UUID and advertises only what that GPU executes; pvg-host never claims
a feature the host driver lacks. Check a host with `pvg-host --probe`.

**Commands.** Venus encodes Vulkan into ring and reply shmems. Every mappable Venus resource exists
twice, guest copy and host copy. Before each submit the kext sends only the 64-byte granules the
guest wrote since the last sync (`FLUSH_MIRRORS`, against a kernel-side shadow); after each fence
pvg-host pushes back the granules the host wrote in watched ranges. Venus gives each shmem byte a
single writer, which is what makes this mirroring correct without shared memory.

**Fences.** Guest timelines live in the kext. A submit records which syncs to signal; pvg-host
reports `FENCE_SIGNALED` after vkr retires the ring fence and after the watched ranges were pushed.

**Host.** virglrenderer's public API reaches Venus only through its render server, which forks and
passes fds. `apply_windows.py` instead builds vkr into one DLL with no server, replaces memfd with
`VirtualAlloc`, exports device memory as fd-less `OPAQUE` resources and adds
`vkr_renderer_map_resource` (vkMapMemory), and pvg-host loads it at run time.

## Build

Host (Windows, VS 2022+ C++, MSYS2 `mingw-w64-ucrt-x86_64-gcc`, Python 3.10+):

```powershell
powershell -ExecutionPolicy Bypass -File gpu\host\build-windows.ps1
gpu\host\out\pvg-host.exe --probe
gpu\host\out\pvg-host.exe --selftest
```

`tools/Build/Build.ps1 -WithVirtualGpu` puts the same output in `artifacts/app/pvg-host`, where pome
looks for it. The `pvg-host` job in `.github/workflows/build.yml` builds it on CI.

Guest (macOS): `.github/workflows/build-pvg-guest.yml` builds the kexts (against MacHyperVSupport),
Mesa's Venus ICD with `vn_renderer_pvg`, the Vulkan loader, the translator and the Metal plugin on
`macos-15-intel`, and packages them with `SHA256SUMS`.

## Use (Tahoe VM, experimental)

1. In pome, select a stopped Tahoe VM → **Virtual GPU → Turn on virtual GPU…**. pome adds
   `amfi_get_out_of_my_way=0x1 amfi=0x80 ipc_control_port_options=0` to boot-args, sets
   `csr-active-config` to `0x0A43` (the values NullMoth's driver was validated with), registers the
   socket service, keeps `config.plist.before-virtual-gpu` and copies the guest package to
   `POME-BOOT/PomeGPU` when it is bundled. **These lower the guest's security.**
2. Start the VM from pome; pome runs `pvg-host --vm <id>` while it runs (log in
   `%LOCALAPPDATA%\pome\logs\pvg-host-<vm>.log`).
3. In macOS: `sudo diskutil mount POME-BOOT`, then `sudo /Volumes/POME-BOOT/PomeGPU/install.sh`,
   and restart. `install.sh --check` reports the kexts, the connection to pvg-host, SIP, boot-args
   and the Metal device.
4. **Turn off virtual GPU** restores the recorded boot-args and SIP value. Run `uninstall.sh` first.

WindowServer stays on the synthetic framebuffer (`pvgmtl-allow.txt` denies it); apps get the Metal
device and their IOSurfaces are copied to the display. Host-side presentation is future work.

## Limits and next steps

- **Bandwidth.** Hyper-V sockets carry roughly 0.3–1.1 GB/s. Command streams fit; large uploads and
  per-frame readback do not. Zero-copy needs guest pages mapped by a host kernel VSP (GPADL), which
  needs a signed Windows driver, the same model VirtualBox and virtio-gpu use for guest memory.
- **Metal 4.** pvg-host reports a *Metal 4 candidate* when the GPU has Metal3 plus cooperative
  matrix, ray query and descriptor buffers or mutable descriptors (RTX 4050: yes; Intel UHD: no).
  The plugin implements Metal 3; `MTL4*` objects (command allocators, argument tables, residency
  sets) are not yet implemented.
- **CUDA.** Not forwarded. ML runs through Metal (MPS, Core ML on GPU, MLX); see
  `docs/VULKAN_FORWARDING.md`.

## Open-source references

| Source | Used for | How |
|---|---|---|
| Mesa `src/virtio/vulkan` (MIT) | Venus guest driver; `vn_renderer_vtest.c` as the model for `vn_renderer_pvg.c` | Built, patched |
| virglrenderer `src/venus`, `server/render_context.c` (MIT) | Host decoder; how to drive vkr without the proxy | Built, patched |
| Linux `include/uapi/drm/virtgpu_drm.h` (MIT) | Blob memory/flag values, ring indices, capset IDs | Values only |
| Linux `net/vmw_vsock/hyperv_transport.c` (GPL-2.0) | vmpipe framing, MTU, ring-size limits, host-initiated offers (`user_def[0]`) | Read only |
| Linux `drivers/gpu/drm/virtio` (GPL-2.0) | Fence contexts per ring, blob lifetime | Read only |
| FreeBSD `sys/dev/hyperv/hvsock` (BSD-2-Clause) | Second hvsock implementation; offer and EOF handling | Read only |
| XNU `iokit/Kernel/IOUserClient.cpp` (APSL-2.0) | OOL structures arrive as unwired `withAddressRange` descriptors; `IOUserClient2022` is kernel-private | Read only; fixed PVGUserClient to wire them |
| VirtualBox guest additions and SVGA3D (GPL-3.0) | Guest-memory regions for zero-copy; macOS guest IOKit patterns | Read only |
| gfxstream (Apache-2.0) | Alternative with a Windows host; lacks ray tracing in its guest encoder | Evaluated, not used |
| NullMoth `nvidia-macos-driver` (PolyForm NC) | Metal on Vulkan, IOAccelerator | Imported into `guest/metal` |
