# ExHyperV comparison and pome changes — 2026-10-27

Scope: improve macOS creation/installation and bring useful VM management and ExHyperV's UI organization to pome. Reference repository: `C:\Users\eion\Code\ExHyperV`, commit `f51e0667a8499e88e3f93d4beb221dc48e14f9b5`. Both repositories were clean at the beginning. The implementation uses pome's existing Core/Infrastructure/WinUI/helper boundaries. No ExHyperV source, artwork, DLLs or WPF dependencies were copied into the distribution.

2026-10-28 application completion pass: automatic creation now restores signed/hash-reviewed Apple recovery to its own disk through a bounded managed decoder, converts it before VM registration, and attaches EFI/destination/recovery at SCSI 0:0/0:1/0:2. OpenCore disables DMG RAM loading and enables file/panic logging. The new Diagnostics tab exports bounded EFI logs read-only; wizard validation happens earlier; console elevation uses the pome owner window and runs off the UI thread. The decoded real recovery payload matches the disk that reached the installer. Release build, 90 optional-media C# checks and 30 script scenarios pass. Historical validation paragraphs below describe their original audit date; current evidence is in TESTING.md and BOOT_COMPATIBILITY_2026-10-28.md.

## Findings addressed

| Finding | Change and evidence |
|---|---|
| Discovery overwrote CPU/RAM/network edits, including an intentional disconnected selection | `CreationDraft` tracks edited fields; explicit recommendation reset. Tests cover asynchronous rediscovery and disconnection. |
| Duplicate names were found after media preparation/copy | Case-insensitive name suggestions and checks before downloading and again in the helper/script. No silent renaming of a user-edited name. |
| Invalid paths and insufficient storage were reported late | Existing local folder/media checks and full-capacity space estimate before downloads. Helper retains actual-size checks. |
| Empty/fractional NumberBox values could be silently cast to integers | Finite whole-number validation before building creation/configuration/growth requests. |
| Manual boot media required typing long paths and hashes | Native HWND-owned folder/VHDX pickers; explicit digest calculation; clear previous digest on path change and explain provenance. |
| Creation required separate start and console actions | Reviewed opt-in start/console handoff; single Library start/console action; start failure preserves the VM. |
| Installation steps were easy to lose after creation | Persistent Library guide covering target disk selection, APFS, OpenCore restarts and the known VMBus stall. |
| Configuration changes required external Hyper-V tools | Stopped-VM name/CPU/static-memory/network editor, with installed-resource validation, live state checks and independent best-effort rollback. |
| Lifecycle was limited to start/shutdown | Pause, resume, save state and saved-VM start, with state-specific buttons and server-side checks. |
| No inspectable storage/firmware/checkpoint view | Elevated details readout and expandable settings; EFI-first repair without security changes. |
| No disk-growth route | Strict original-disk ownership/path/SCSI checks, no checkpoints, off-state recheck, available-space reserve, growth only; guest APFS changes remain manual. |
| No pre-change recovery points or standalone VM export | Stopped-VM standard checkpoint create/restore/delete; separate export backup with preserved partial output and durable markers. |
| Earlier layout did not match the requested ExHyperV composition | Compact rail, searchable master/detail library, expandable settings and creation groups, responsive stacking, Mica/custom caption and persisted themes. |
| Documentation contradicted recorded boot evidence and bundled Python | Updated guide, status, security and README; partial boot does not establish Recovery GUI or completed installation. |
| Parallel restore failed without reporting a compiler error in this environment | Build entrypoint uses single-node restore and adds PowerShell parser/scenario checks. |

Reference areas inspected: `VirtualMachinesPageViewModel.Create.cs`, `VirtualMachinesPageViewModel.Memory.cs`, `VmCreateService.cs`, `VmBootService.cs`, `VirtualMachinesPage.xaml`, `VmCreateView.xaml`, `MainWindow.xaml`, `AppPalette.xaml` and committed screenshots. ExHyperV's preservation of edited paths, name allocation, optional post-create start and grouped detail layout informed the port.

## Deliberately unavailable and remaining ports

This is not complete ExHyperV feature parity. Import/adoption, cloning, additional disk/ISO attachment, arbitrary firmware reordering, multi-select/batch creation, drag/drop grouping, localization, guest services and background usage charts remain unimplemented. A macOS clone/import path needs explicit handling of guest identities, external disk references, ownership and restore safety. Existing Hyper-V exports can be imported through Hyper-V Manager, but this session did not validate such a restore.

Host switch creation and host scheduler/security modifications were not ported. GPU-P, DDA/PCIe reassignment, consumer-DDA bypasses, USB forwarding and Windows guest-driver injection are not enabled: pome lacks a validated macOS path for those capabilities. Ordinary macOS setup uses the existing switch inventory and synthetic devices. These are recorded as unavailable rather than offered as apparently working controls.

## Validation and limits

The build entrypoint runs the executable C# suite, parses all embedded PowerShell programs, and runs deterministic cmdlet-double scenarios against the actual management/lifecycle scripts. Scenarios cover external ownership rejection, stale VM state, settings rollback, duplicate names, checkpoint-policy restoration, checkpoint ownership, saved-VM RAM gating, exact-disk growth and no-shrink/checkpoint guards. These tests do not execute Hyper-V mutations.

The native UI was inspected with the computer-use skill. Hardware discovery was read-only. No VM was created, started, shut down, checkpointed, exported or resized during this audit. New Hyper-V operations still require real integration validation. Build logs may report NU1900 because the NuGet vulnerability endpoint is unreachable; dependency versions and lockfiles are unchanged.

The existing macOS boot investigation records a partial XNU/userspace boot followed by a VMBus stall. This work applies the new-VM thread-topology recommendation; it does not produce or install a fixed MacHyperVSupport kext, validate Recovery GUI, or complete macOS installation. Signing, publisher pinning and handle-based path-race hardening remain release gates.
