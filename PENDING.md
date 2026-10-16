# Pome checkpoint — 2026-10-15 (Post-Audit Update)

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

