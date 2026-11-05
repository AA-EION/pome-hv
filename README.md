# pome

A native Windows home for macOS virtual machines on Hyper-V.

**Engineering preview.** Pome is a native WinUI 3 application for host assessment, guided macOS VM creation and Hyper-V management. Disk-based recovery reached the Sequoia installer on the reference host with 4 GB RAM. Installation completion, the first normal boot and GPU acceleration remain unverified.

## Build and run

On Windows x64 with .NET SDK 10.0.401:

```powershell
./tools/Build/Build.ps1
./artifacts/app/Pome.UI.exe
```

The script restores locked dependencies, builds every project, runs the executable test suite, and assembles the app and elevated helper. Windows App SDK is included locally; .NET 10 runtime is required. No Windows feature or VM is changed during the build.

## What is implemented

Automatic creation now builds separate OpenCore and Apple recovery disks, avoiding the observed 4 GB RAM-disk allocation failure. Boot logging is on by default and the VM workspace includes a local boot-log export. Compatibility mode retains one active macOS CPU; the source SynIC fix is not a built kext. See [current boot status](docs/BOOT_COMPATIBILITY_2026-10-28.md).

- ExHyperV-inspired compact navigation, searchable VM list/detail layout, expandable creation/settings sections, Networks and persistent system/light/dark appearance.
- Async host assessment, resource advice, preserved creation edits, native path pickers and early name/storage/media checks.
- CIM hardware discovery, GPU inventory, existing Hyper-V switches and VM listing, explicit unknown capability states.
- Automatic media for the reviewed Intel CPU matrix: bundled Python, pinned EFI tools, authenticated Apple recovery, unique identity, `ocvalidate`, GPT/FAT32 EFI, a separate restored recovery VHDX and a blank macOS destination disk. The managed converter is tested against the real cached image; the full new-VM path has deterministic script coverage.
- Custom EFI/recovery VHDX input with SHA-256 checks; static RAM, CPU, disk, network and boot order.
- User-reviewed UAC helper, current-user named pipe with mutual process-ID checks, embedded PowerShell programs and structured data arguments.
- Start/console handoff, pause/resume/save state, graceful shutdown, installation guide and stopped-VM name/CPU/static-memory/network editing; external VMs remain read-only.
- Disk/firmware inspection, EFI-first boot repair, guarded original-disk growth, stopped-VM checkpoints and VM export backups. These new Hyper-V mutations require real integration validation.
- SQLite activity records, per-operation recovery markers, conservative rollback preserving disks, allowlisted host reports and read-only boot-log export from stopped managed VMs.
- Separate opt-in NullMoth 2.0.2 download with integrity verification. No NullMoth code or binaries are bundled, installed or run.
- MMIO allocation planning, kext dependency checks, five MacHyperVSupport source patch candidates, and automated tests.

## Boundaries

Windows client DDA and macOS GPU-P remain disabled. A detected NVIDIA GPU is not proof of acceleration. The PCI patches are uncompiled research candidates, not a working driver. Host display ownership, MUX, BAR sizes, IOMMU isolation and power dependencies remain unknown.

Automatic AMD profiles, validated Tahoe installation, automatic update/rollback, device reassignment, unattended recovery, guest services, cloning and signed production installers remain future work. See [feature status](docs/FEATURE_STATUS.md), [GPU investigation](docs/PCI_RESEARCH.md), and [test evidence](TESTING.md).

Apple's macOS license restricts virtualization to qualifying Apple-branded hardware. Technical operation does not establish permission. The UI links the applicable license and requires acknowledgement. Separate download does not remove NullMoth's noncommercial restrictions.

Start with [BUILDING](BUILDING.md), [USER_GUIDE](USER_GUIDE.md), [ARCHITECTURE](ARCHITECTURE.md), [SECURITY](SECURITY.md), and [upstream research](docs/UPSTREAM_RESEARCH.md).

See the [ExHyperV comparison and remaining ports](docs/EXHYPERV_PORT_AUDIT.md) for scope. [Current boot findings](docs/BOOT_COMPATIBILITY_2026-10-28.md) supersede the earlier VMBus-stall checkpoint: Recovery and installer entry were observed, while installation completion remains pending.
