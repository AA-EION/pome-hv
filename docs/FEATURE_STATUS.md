# Feature status

`Implemented` describes code, not proof that a guest works. Tests and hardware evidence are separate.

| Capability | Code | Validation / remaining gate |
|---|---|---|
| Native WinUI app and navigation | Implemented | Assembled in `artifacts/app`; automatic/manual media toggling and installation guidance wired |
| Host CIM / GPU / Hyper-V discovery | Implemented | Verified unelevated and elevated (`Inspect`) on Lenovo 82XV; unknown topology remains explicit |
| Resource and capability policies | Implemented | 67/67 automated unit/IPC/storage/policy tests pass |
| VM creation / CPU / static RAM / disks / firmware | Implemented | Blocked only on user guest-security consent and freeing ≥10 GiB host RAM |
| Lifecycle / console | Implemented | Needs a pome-managed test VM |
| Existing VM management | Read-only discovery | Adoption/import not implemented |
| Prerequisite enablement | Implemented | Requires explicit UI review/UAC; Hyper-V already enabled on local host |
| EFI download and OpenCore configuration | Implemented | Published digest checked; generated config accepted by real `ocvalidate` |
| Apple recovery acquisition | Implemented via pinned upstream utility | Verified against Apple (`696-28424`, chunklist + DMG verified by `macrecovery.py`) |
| EFI VHDX construction | Implemented | Ready to execute once guest-security consent and host RAM budget are satisfied |
| macOS boot / installation | Unverified | No boot or installation evidence yet |
| SQLite journal / failure records | Implemented | Persists preparation/execution stages and reconciles per-VM `operation.json` markers on reload |
| Full backup / update / rollback | Not implemented | Trust metadata, transactional updates and cold-boot tests required |
| GPU inventory / gating / MMIO planning | Implemented | Unit tests; resource sizes and display ownership are unknown |
| NullMoth separate download | Implemented | Simulated downloader tests; no driver install or execution |
| PCI correctness / high-MMIO patches (`0001`–`0004`) | Source candidates | Sequential apply + 21 MSVC allocation/fault/readback scenarios pass; macOS SDK build and hardware tests pending |
| Reims vGPU / Windows Vulkan paravirt research | Audited (`docs/GPU_ACCELERATION_RESEARCH.md`) | `c099dd5` lacks Windows OS/surface modules; requires separate QEMU/WHPX backend port |
| DDA, GPU-P, Metal | Disabled / unverified | All hardware milestones pending |
| Diagnostics | Allowlisted JSON export | Local-only; Event Log integration not implemented |
| Installer | Definition provided | Unsigned, not compiled or installed in this session |

The master brief's production acceptance criteria are not all met. This repository is a buildable engineering preview with a documented route to the remaining gates.
