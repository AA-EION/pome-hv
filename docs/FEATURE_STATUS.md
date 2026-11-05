# Feature status

`Implemented` describes code, not proof that a guest works. Tests and hardware evidence are separate.

| Capability | Code | Validation / remaining gate |
|---|---|---|
| Native WinUI app and navigation | Implemented | Compact rail, searchable master/detail library, four-step wizard, six VM task tabs and persisted themes; rebuilt Library, creation/media/review screens inspected natively on 2026-10-28 |
| Host CIM / GPU / Hyper-V discovery | Implemented | Verified unelevated and elevated (`Inspect`) on Lenovo 82XV; unknown topology remains explicit |
| Resource and capability policies | Implemented | 88/88 default C# checks; 90/90 with real pinned archive/recovery; 13 management/initialization, 14 diagnostics/repair/export and 3 creation-workflow scenarios pass |
| VM creation / CPU / static RAM / disks / firmware | Implemented | Early preflight, preserved edits, separate EFI/recovery/destination disks, bundled Python and reviewed start/console option; real original creation and repair reached the installer. New integrated creation has script-double coverage |
| Lifecycle / console | Implemented | Start/saved-state start, graceful shutdown, pause/resume/save, VMConnect; state/ownership tests pass, new real-VM operations not exercised |
| Managed VM settings / boot / disk growth | Implemented | Stopped-VM name/CPU/static-memory/network editing, EFI-first repair and guarded disk growth; cmdlet-double tests, real integration pending |
| Existing VM management | Read-only discovery | Adoption/import not implemented |
| Prerequisite enablement | Implemented | Requires explicit UI review/UAC; Hyper-V already enabled on local host |
| EFI download and OpenCore configuration | Implemented | Published digest checked; generated config accepted by real `ocvalidate` |
| Apple recovery acquisition | Implemented via pinned upstream utility | Verified against Apple (`696-28424`, chunklist + DMG verified by `macrecovery.py`) |
| EFI / recovery VHDX construction | Implemented | Automatic media uses a managed UDIF-to-fixed-VHD decoder followed by Hyper-V conversion. Real decoded sectors match the boot-tested recovery disk; Windows' read-only virtual-disk parser accepts the result |
| macOS version selection | Implemented | OS X 10.7–macOS 26 catalog with per-version recovery, SMBIOS and CPU profile; Tahoe swaps in MacHyperVSupport 1.0.0. Unit-tested; only Sequoia has guest evidence |
| AMD automatic profile | Implemented | AMD_Vanilla patches (commit-pinned download) with the VM's processor count, DummyPowerManagement and the Hyper-V `_cpu_syscall_init` wrmsr patch; macOS 10.13+; tested against the real upstream patch file, no AMD host available |
| Multi-CPU guest driver | Build pipeline | `.github/workflows/build-machypervsupport.yml` builds MacHyperVSupport 1.0.0 + patch 0005 (optional #13–#15). `GuestDriverPolicy.PinnedDriverFixesSmp` stays false until a pinned build passes `docs/MULTI_CPU_DRIVER.md` |
| Power off / reset / removal | Implemented | Turn off, Reset, guest-tools-aware Shut down, Remove VM (optional verified folder deletion), Open VM folder; cmdlet-double and policy tests, no real VM run |
| Hyper-V Administrators path | Implemented | Optional one-time group join; unelevated power actions, console and auto-refresh for members. Not exercised on a real host |
| macOS boot / installation | Recovery and installer entry observed | Real 4 GiB Sequoia VM reached Recovery and active installation after disk-based recovery repair. Full installation, first normal boot and multi-CPU operation remain unverified; see current boot compatibility notes |
| SQLite journal / failure records | Implemented | Persists preparation/execution stages and reconciles per-VM `operation.json` markers on reload |
| Checkpoints / export backup | Implemented | Stopped-VM standard checkpoint create/restore/delete and export with partial-file markers; no real snapshot/export/restore validation yet |
| Import / clone / update | Not implemented | Guest identity, ownership, external-path validation, trust metadata and cold-boot tests required |
| GPU inventory / gating / MMIO planning | Implemented | Unit tests; resource sizes and display ownership are unknown |
| NullMoth separate download | Implemented | Simulated downloader tests; no driver install or execution |
| PCI correctness / high-MMIO patches (`0001`–`0004`) | Source candidates | Sequential apply + 21 MSVC allocation/fault/readback scenarios pass; macOS SDK build and hardware tests pending |
| Reims vGPU / Windows Vulkan paravirt research | Audited (`docs/GPU_ACCELERATION_RESEARCH.md`) | `c099dd5` lacks Windows OS/surface modules; requires separate QEMU/WHPX backend port |
| DDA, GPU-P, Metal | Disabled / unverified | All hardware milestones pending |
| Diagnostics | Host JSON report + VM boot-log export | Host report remains privacy-filtered. Off-state, owned, non-checkpointed EFI log export is read-only, bounded and local; file logging enabled on new automatic media. Event Log integration remains unavailable |
| Installer | Definition provided | Unsigned, not compiled or installed in this session |

The master brief's production acceptance criteria are not all met. This repository is a buildable engineering preview with a documented route to the remaining gates.
