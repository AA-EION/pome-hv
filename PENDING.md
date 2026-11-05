# Pome checkpoint — 2026-11-05

Upstream review: ExHyperV was already at upstream `f51e066`. OSX-Hyper-V has no release after 0.4.0 (MacHyperVSupport 0.9.9 inside). MacHyperVSupport 1.0.0 (2026-09-27) carries the MacKernelSDK#25 IOPCIHostBridge layout fix that ends the Tahoe `HyperVGraphicsBridge` spinlock panic. Open upstream PR acidanthera/MacHyperVSupport#12 is the same SynIC EOM fix as patch `0005` and booted 8/12/16 vCPUs on Sequoia 15.8; #13–#15 (VMBus retry backoff, software cursor, network start failure) were tested on AMD Zen 5 with macOS 15/26. OpenCore is at 1.0.8.

Implemented this session (not yet guest-tested):

- **macOS version picker** (`src/Core/MacOsCatalog.cs`): OS X 10.7 through macOS 26 with per-version Apple recovery board/MLB (OpenCore `recovery_urls.txt`), per-version SMBIOS (iMac20,1 for 11+, iMac19,1/18,3/17,1/14,2/12,2 for older), per-version recovery cache, legacy booter quirks for 10.7, `-os latest` for Sequoia/Tahoe. Tahoe replaces the EFI's MacHyperVSupport kexts and guest Tools with the pinned 1.0.0 release (SHA-256 from GitHub release metadata).
- **CPU profiles** (`CpuCompatibility`): Sandy Bridge–Arrow Lake/Lunar Lake/Sapphire Rapids; Comet Lake, Haswell or Ivy Bridge CPUID chosen per host and release; AVX2 required for Ventura+; Sandy/Ivy capped at Monterey. AMD still fails closed.
- **Power**: Turn off and Reset; Shut down checks the guest shutdown integration service and explains Turn off instead of hanging.
- **Removal**: Remove VM (keep files by default; typed-name confirmation to delete only the VM's own verified `pome-…` folder, no checkpoints, no reparse points). Open VM folder.
- **Fewer UAC prompts**: optional one-time Join Hyper-V Administrators; members run power actions and VMConnect unelevated. Auto-refresh polling while guests run or after actions.
- **Guest tools**: `Scripts/` and `Tools/` from the upstream archive now go onto POME-BOOT, with an Install macOS step for `post-install.sh` (shutdown/time-sync/file-copy daemons, framebuffer resolutions).
- **Fixes**: EFI volume mounted to a private folder (no drive letter), 2-vCPU recommendation plus power-of-two hint, OneDrive-safe default storage, consistent "Start & connect", 4 GB minimum in compatibility data, validation folders cleaned after preparation.

Validation: Release build 0 warnings/0 errors; 109/109 C# tests, 16 management/initialization, 14 diagnostics and 3 creation-script scenarios. New wizard and This computer screens inspected natively. No VM was created, started or changed. Not yet validated: any guest other than Sequoia, the Tahoe driver swap against the real 1.0.0 archive, removal and Hyper-V Administrators flows on a real host.

Follow-up the same day:

- **Real media validation** for 26, 15, 12, 10.15, 10.13, 10.11, 10.10, 10.8 and 10.7 passed (see TESTING.md). Fixed along the way: automatic re-download of interrupted or rejected Apple recoveries, Apple Partition Map (2 KB block) recoveries for 10.7–10.10, and Tahoe-compressed files in `InspectRecovery.py`.
- **AMD profile**: families 15h–1Ah, macOS 10.13+, with commit-pinned AMD_Vanilla patches (runtime download), the VM's core count, DummyPowerManagement and the Hyper-V `wrmsr` patch. AMD VMs need power-of-two processor counts and cannot change them after creation.
- **Multi-CPU driver**: `.github/workflows/build-machypervsupport.yml` and `docs/MULTI_CPU_DRIVER.md`; `GuestDriverPolicy.PinnedDriverFixesSmp` drops `cpus=1` once a pinned build passes the boot gate.

Remaining gates, all needing hardware or a push: run the driver workflow on GitHub (requires pushing it), then pin and boot-test the output; guest installs from the TESTING.md checklist (needs about 6 GB free RAM); an AMD host test; OpenCore 1.0.8 integration pin.

# Pome checkpoint — 2026-10-28

The rebuilt application is at `artifacts/app/Pome.UI.exe`. The ExHyperV-inspired library, four-step wizard, lifecycle/settings/storage/recovery workflows and Diagnostics tab are assembled there with the matching helper and bundled Python.

Automatic creation now restores Apple recovery into a separate GPT/HFS+ disk, converts it to VHDX before VM registration and attaches EFI, destination and recovery as SCSI 0:0, 0:1 and 0:2. This integrates the repair that cleared the 4 GiB RAM-disk allocation failure. New OpenCore configurations disable DMG loading and enable file/panic logs. The managed decoder's entire real recovery payload matches the boot-tested Python restore; Windows' read-only virtual-disk parser accepts it.

The user-approved Sequoia VM reached Recovery and active installation with 4 GiB, then the user shut it down. The app work performed no further VM start, stop, mount or mutation. Installation completion, first normal boot and multi-CPU operation remain unverified. The provisional `cpus=1` mode remains; the source-level SynIC patch has not been built into a guest kext.

Release build: 88/88 default C# tests, 90/90 including real pinned EFI/recovery, plus 13 management/initialization, 14 diagnostics/repair/export and 3 creation-workflow scenarios. Native Library and creation/media/review screens inspected. Console elevation now leaves the UI responsive and supplies the owner HWND. The wizard checks name/folder/resource values before advancing; VM boot logs can be exported locally from an off, owned VM without checkpoints.

Remaining engineering gates: a fresh end-to-end automatic creation/export test on a disposable VM; complete macOS installation and normal boot; a rebuilt multi-CPU guest driver; safe import/cloning/adoption and identity handling; additional media management; release signing, publisher pinning and handle-based path-race hardening. Full ExHyperV feature parity and GPU acceleration are not claimed. See `docs/EXHYPERV_PORT_AUDIT.md`, `TESTING.md` and `docs/BOOT_COMPATIBILITY_2026-10-28.md`.

# Historical checkpoint — 2026-10-15 (Post-Audit Update)

This checkpoint reflects the full codebase audit and completion of all software-testable pending items. It remains an engineering preview, not a boot-validated release.

## Completed in this audit

1. **Resolved IPC Pipe Regression & Added Negative Security Tests (`src/Infrastructure/UserPipe.cs`, `tests/Unit/Program.cs`)**
   - Configured 4 KiB asynchronous buffers in `UserPipe.CreateServer` and concurrent read/write ordering in the pipe ACL test, eliminating the `PipeProtocol.WriteAsync` deadlock.
   - Added `UserPipe.RequireOwnerMatches` and negative tests for mismatched owner SIDs, missing helper binaries, malformed helper CLI arguments, and mismatched parent PIDs over a real user pipe.
   - All **67/67** default tests pass, plus **68/68** with `--host-survey` and **68/68** with `--upstream-archive` (`ocvalidate.exe`).

2. **Reassembled Native Application & Improved Creation UI (`src/UI/MainViewModel.cs`, `src/UI/MainWindow.xaml`)**
   - Fixed UTF-8 character encoding (`·` and `…`) in `MainViewModel.cs`.
   - Added `AutomaticMediaVisibility` and `ManualMediaVisibility` so toggling automatic vs. manual boot media cleanly shows/hides the relevant controls.
   - Added post-creation guest installation guidance (`InfoBar`) explaining OpenCore boot selection, Disk Utility APFS erase, and macOS installation.
   - Rebuilt and reassembled `artifacts/app/Pome.UI.exe` and `artifacts/app/helper/Pome.Helper.exe` via `tools/Build/Build.ps1 -Configuration Release` (0 warnings, 0 errors) and verified clean launch.

3. **Completed Journal Persistence & Per-VM Marker Reconciliation (`src/Infrastructure/Journal.cs`, `src/PrivilegedService/Program.cs`, `src/Infrastructure/Scripts/CreateVm.ps1`)**
   - Upgraded `SqliteJournal` (`user_version=2`) to persist `ResourceDirectory` and added `ReconcileMarkers()` to synchronize non-terminal SQLite activity entries with per-VM `operation.json` stage markers (`NeedsAttention`, `Created`, etc.) on startup/refresh.
   - Updated `Pome.Helper` to write `operation.json` atomically (`WriteAtomicAsync`) and record `NeedsAttention` in `operation.json` if media copying or hash verification fails before PowerShell execution.
   - Recorded `PreparingMedia`, `MediaPrepared`, and `Cancelled` stages in the SQLite journal during `PreparePlanAsync`.

4. **Expanded High-MMIO / PCI Bridge Patch Series (`patches/MacHyperVSupport/0004-acpi-admission-locking-and-teardown.patch`, `tools/Validation/GeneratePciPatches.py`, `tools/Validation/TestPciPatches.py`)**
   - Added Patch `0004`: ACPI memory range admission validation (rejects zero base/length, `UInt64` overflow, `maxAddressRange < endAddr`, 4 GiB boundary crossing, and overlapping ranges), `_pciLock` synchronization around `_rangeAllocatorLow` and `_rangeAllocatorHigh`, BAR low-attribute-bit preservation and readback verification with rollback on mismatch, and `HyperVPCIBridge::stop()` resource teardown.
   - Verified all 4 patches apply sequentially to fresh copies of `MacHyperVSupport` (`cafcd34daea5e58633112a1e63cef1f07bbc8404`) and pass **21 MSVC (`cl.exe`) allocation, fault-injection, active-decoder, and BAR readback scenarios**.

5. **Audited Reims vGPU Snapshot (`docs/GPU_ACCELERATION_RESEARCH.md`)**
   - Audited `.research/reims-vgpu` at commit `c099dd5a4d61107a5527efc64cf1d1ab2f3a9b85`. Confirmed that `reims-vgpu`, `reims-vgpu-vulkan`, and `reims-vgpu-window` only define `#[cfg(target_os = "macos")]` and `#[cfg(target_os = "linux")]` modules and rely on POSIX `OwnedFd`/`mmap` and X11/Wayland/AppKit surfaces. Documented the exact engineering requirements for a future isolated Windows QEMU/WHPX backend.

6. **Bundled Private Python 3.12.10 + `venv` & Zero-Touch Review-and-Create (`src/Infrastructure/PythonLocator.cs`, `tools/Build/Build.ps1`, `src/UI/MainWindow.xaml.cs`, `src/UI/MainViewModel.cs`)**
   - `tools/Build/Build.ps1` now provisions an isolated Python 3.12.10 embeddable distribution + private `venv` inside `artifacts/app/python` (`artifacts/app/python/venv/Scripts/python.exe`).
   - `PythonLocator.cs` resolves the bundled runtime first and auto-bootstraps a private `venv` in `%LOCALAPPDATA%\Pome\python` if missing, so the user never has to install or configure Python.
   - Fixed the **Review and create** flow so clicking it from a cold start automatically discovers the host, loads Hyper-V virtual switches via `Pome.Helper`, selects `Default Switch`, prompts inline for guest firmware consent if unchecked, and supports 4–8 GiB RAM allocations on 16 GiB laptops.
   - All **68/68** unit tests pass (`dotnet run --project tests/Unit/Pome.Tests.csproj -c Release`).

7. **Created & Boot-Tested `Sequoia` VM + Root-Caused `'VMBS' (a,4020001)` Stall (`docs/VMBS_BOOT_STALL_FINDINGS.md`)**
   - Created the `Sequoia` Generation 2 VM end-to-end through `Pome.UI.exe` and booted OpenCore + macOS 15.4.1 (Sequoia) XNU kernel into `launchd` and `fskit.msdos`.
   - Diagnosed the IOKit stall `busy timeout[0], (240s): 'VMBS' (a,4020001)` to `HyperVController::sendSynICEOM(UInt32 cpu)` in `MacHyperVSupport/Controller/HyperVControllerInterrupts.cpp` calling `_pmCallbacks.ThreadBind(proc)` (`thread_bind`) with interrupts disabled and without `thread_block(THREAD_CONTINUE_NULL)`, causing `wrmsr64(kHyperVMsrEom, 0)` to execute on the wrong vCPU when `cpu_number() != 0` during `HyperVVMBus::scanVMBus()`.
   - Full analysis and remediation steps are documented in [`docs/VMBS_BOOT_STALL_FINDINGS.md`](docs/VMBS_BOOT_STALL_FINDINGS.md).

## Remaining gates for next session

1. **Apply `HyperVController::sendSynICEOM` Fix & Complete macOS Sequoia Recovery GUI Boot**
   - Implement the `sendSynICEOM` fix/patch, add `Set-VMProcessor -HwThreadCountPerCore 1`, and initialize `macOS.vhdx` as GPT + exFAT in `CreateVm.ps1` as detailed in [`docs/VMBS_BOOT_STALL_FINDINGS.md`](docs/VMBS_BOOT_STALL_FINDINGS.md).
   - Verify macOS Sequoia boots past `VMBS` into `WindowServer` / macOS Recovery GUI (`Install macOS Sequoia` + `Disk Utility`).

2. **Full macOS Kernel SDK Build & Server DDA Hardware Validation**
   - Build `MacHyperVSupport` with patches `0001`–`0004` using Xcode / macOS Kernel SDK, and validate VMBus `BusD0Exit` / `ResourcesReleased` host protocol and multi-function (`slot != 0`) enumeration on an eligible Windows Server DDA test host.

3. **Production Release Packaging & Hardening**
   - Authenticode code signing and publisher pinning between `Pome.UI.exe` and `Pome.Helper.exe`, handle-based TOCTOU/reparse-point hardening, Inno Setup installer compilation and install/repair/uninstall QA.

