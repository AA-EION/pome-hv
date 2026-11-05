# UI completion and boot compatibility — 2026-10-28

The main application is assembled at `artifacts/app/Pome.UI.exe`. The UI now uses a bounded searchable library, a real empty/search state, five task tabs for selected machines, a power menu, four creation steps with fixed navigation, a compact status footer, and separate activity/network/appearance views. License and guest-firmware consent remain explicit on Review. A missing management query is labelled as an unloaded library rather than zero known VMs.

## Shipped boot mitigation

Automatically configured OpenCore media now preserves existing boot arguments, removes conflicting `cpus=` entries and adds exactly one `cpus=1`. This limits **active XNU processors**, retaining the VM's Hyper-V CPU allocation. It is a conservative workaround for the pinned driver's cross-CPU SynIC EOM path, not a full driver replacement or a claim that Recovery boots. Existing VM EFI disks are not modified by this change. Rebuild automatic media to receive it. Do not remove the limit until a corrected driver has passed multi-CPU guest tests.

Apple's [x86 startup implementation](https://github.com/apple-oss-distributions/xnu/blob/main/osfmk/i386/i386_init.c) reads `cpus` and caps the active processor count. The [upstream guest interrupt source](https://github.com/acidanthera/MacHyperVSupport/blob/master/MacHyperVSupport/Controller/HyperVControllerInterrupts.cpp) retains the delayed-binding EOM path. Avoiding that cross-CPU branch is an inference from these sources and the recorded stall, not a new guest observation.

New destination disks are mounted only immediately after their exact VHDX is created. Initialization requires the returned disk number and RAW partition style, creates GPT/exFAT so Recovery can identify the target, and dismounts in `finally`. Existing disks are refused. The user still erases the destination as APFS/GUID in macOS Disk Utility.

## Source fix and evidence

`patches/MacHyperVSupport/0005-synic-eom-owner-cpu.patch` changes EOM handling to use the existing synchronous rendezvous primitive with a CPU-selective callback. It adds a barrier between clearing the message and reading its pending flag, rejects invalid/null CPU data, and removes delayed thread binding from this method. No kext binary was built, installed or redistributed. The rendezvous callback performs only CPU identification and the EOM write. Source tests do not model all XNU interrupt/locking behavior.

REA identified the pinned Monterey kext as x86-64 Mach-O, SHA-256 `283844470ac67f9992cee01549cb7b1d67c384fb4f1a0475aca85492074a0d22`. Native decompilation was unavailable because no suitable provider was configured for this host/format. Ordinary local Capstone disassembly independently showed the binding call and direct EOM write in `sendSynICEOM` at `0x14500`. Its import calls have unresolved relocation operands in the file; arbitrary in-place binary replacement was therefore not used. `tools/Validation/InspectSynicBinary.py` reproduces that read-only inspection with the optional workspace Capstone package.

Validation:

- 84/84 default C# tests passed; 85/85 including the real pinned archive and `ocvalidate`.
- 13/13 deterministic management/creation script scenarios passed, including refusing nonblank destination disks and exact mounted-disk selection.
- 35/35 compiled MSVC CPU/pending-flag/bounds scenarios passed against the actual source-patch callback and method. No real MSR or guest ran.
- All embedded PowerShell programs parsed. NuGet vulnerability querying remains unavailable (NU1900); locked versions are unchanged.

The helper now reports early process exit and IPC connection timeout clearly. Failed UI inspection is shown in a dialog as well as status. A separate BootCheck inspector was restored/built and successfully completed after the user approved Windows UAC. Evidence is in `artifacts/boot-validation-20261028/inspection.json`.

## Real boot gate

After explicit user approval, the helper created `Sequoia` (`7acf40e6-2939-4724-b26f-94be75cdad8b`) with 4 GiB static RAM, 4 configured vCPUs, a 64 GiB destination disk and Default Switch. The per-operation marker reached `Created`, and the returned VM inventory reported `Off`. The generated EFI and macOS VHDX files were preserved under `artifacts/boot-validation-20261028/pome-f2154926697f44c39767a4ba7b267085`. This validates the revised creation/initialization path on this host; it does not validate guest boot. A separate start request follows the user's authorization.

Earlier start retries failed the live RAM-reserve check. After the user ran the foreground launcher as administrator, `artifacts/boot-validation-20261028/start.json` recorded success and Running with 4 GiB assigned. The user's screenshot showed Apple's prohibitory symbol and `support.apple.com/mac/startup`; a subsequent console observation showed OpenCore's picker again. Recovery GUI has **not** been reached. The earlier VMBus stall must not be assumed to explain this earlier boot abort without its actual log. UI helper launches supply pome's HWND and perform ShellExecute off the UI thread, preserving responsiveness while waiting for the user.

The initial inspection preceded VM creation and found 1.54 GiB free RAM. After the user closed applications, availability exceeded the required 6 GiB and the guest started. The existing 2 GiB Windows reserve was retained.

## Prohibitory-screen investigation

`tools/Validation/InspectRecovery.py` reads UDIF/zlib/HFS+ metadata without mounting or changing the image. Against the actual cached recovery, it extracted macOS **15.4.1 (24E263)** and both PlatformSupport lists, which include **iMac20,1**. Evidence is in `artifacts/boot-validation-20261028/recovery-metadata.json`. The generated OpenCore configuration enables automatic SMBIOS, uses iMac20,1, and preserves the reviewed Comet Lake CPUID. This rules out a simple absence of that model from this image's supported-model list; it does not establish the cause of the abort.

The actual configuration has `Misc/Debug/Target=3`, `AppleDebug=false`, and `ApplePanic=false`: logging is enabled only on screen. File logging must be enabled before another attempt to capture the transient failure. The existing VM has not yet received those diagnostic changes.

`tools/BootCheck/BootDiagnostics.ps1` defaults to collecting logs from a powered-off VM's EFI disk read-only. It verifies the exact VM ID, pome ownership marker, journal, EFI path, SCSI location, returned mount disk number, GPT partition type and FAT32 volume label. `-EnableLogging` retains `config.before.plist` and changes only AppleDebug, ApplePanic and Target (67). `-RestartFailedBoot` explicitly powers off the failed guest, enables logging, checks startup RAM plus the existing 2 GiB host reserve, restarts once, and waits for the user to observe the same failure. Pressing Enter then powers the guest off and collects `opencore-*.txt`, `panic-*.txt` and the current config. No target/install disk is mounted or edited. The guest remains off after collection. Diagnostic logs stay local.

The host-specific foreground launcher is `artifacts/boot-validation-20261028/Capture-Sequoia-Boot.cmd`; run it as administrator. It uses the already installed PowerShell 7 runtime without changing execution policy. Seven scenarios exercise real script logic with cmdlet doubles, including read-only behavior, reserved-partition filtering, ownership/live-disk rejection, RAM reserve enforcement, preservation of the original config through the second collection, and actual Windows volume GUID paths for fixture file I/O.

The first elevated capture mounted the real EFI volume and copied its original configuration, but the XML filename overload rejected its `\\?\Volume{...}` path as an invalid URI before changing any settings or restarting. Evidence was retained under `boot-logs-20261028-210533-612`. XML reading and writing now use explicit file streams, avoiding URI parsing. The regression uses the fixture drive's real GUID returned by read-only `mountvol /L`, without mounting a VHD or starting a VM. A fresh real logged boot remains pending the corrected foreground launcher.

## Captured kernel allocation failure and disk-based recovery candidate

The corrected capture succeeded. `boot-logs-20261028-210725-748/opencore-2026-10-28-160733.txt` contains Apple's boot.efi log (its unused preallocated tail is NUL-padded). A readable copy is alongside it. The failure is **before XNU starts**:

```text
EB.MM.AKM: Err(0xE) <- EB.MM.MKP
EB.LD.DS64: SEG! __LINKEDIT
EB.LD.DS64: 0 <- EB.MM.AKMr2 0xd49000 0xffffff8003aa4000
EB|STOP: 0x16
```

At `slide=4`, the failing region starts at physical `0x3aa4000` and extends into the firmware allocation at `0x4400000`. The memory map also contains an ACPI NVS reservation at `0x470c000` with `0x2cb5b` pages, plus `0x8000` pages above 4 GiB. Those reservations are consistent with this 884 MB DMG's RAM backing. The [OpenCore RAM-disk allocator](https://github.com/acidanthera/OpenCorePkg/blob/master/Library/OcAppleRamDiskLib/OcAppleRamDiskLib.c) prefers memory above 4 GiB specifically to avoid kernel collisions, falling back below it when necessary. This is evidence of a kernel-address allocation conflict; it is not evidence that Windows refused the guest or that the previous VMBus stall was reached.

`tools/Validation/BuildRecoveryVhd.py` restores all GPT/HFS+ sectors from the SHA-256-verified UDIF/zlib source into a new fixed VHD. It bounds decoded chunks, refuses existing outputs/partials, requires complete nonoverlapping sector coverage, verifies primary/backup GPT and partition-array CRCs, rechecks the source digest after conversion, and writes a standard fixed-VHD footer. No physical disk is mounted or written. `Recovery-15.4.1.vhd` is 3,221,110,784 virtual bytes, SHA-256 `45BCEACF74704693BE8138C6AC4520B0DB6594C7881B3D9D6086D8B6F1E73718`. Windows' **read-only OpenVirtualDisk/GetVirtualDiskInformation APIs successfully accepted this real file** and reported its expected size and 512-byte sectors.

The repair mode in BootDiagnostics checks the VHD digest, exact pome ownership, Off state, absence of checkpoints and free SCSI 0:2 slot. It converts only to a new owned `Recovery.vhdx`, records a source/operation receipt for retries, attaches it, backs up the EFI configuration, and disables DMG loading so the restored recovery disk boots without its RAM-disk allocation. EFI Secure Boot, SIP, configured CPUs, the 4 GiB RAM allocation and the macOS destination disk are unchanged. Starting still requires the existing 2 GiB free host reserve. Eleven cmdlet-double scenarios pass, including digest mismatch, slot collision, checkpoint rejection and idempotent repair.

## Live guest result after repair

The user ran `Repair-Sequoia-Recovery.cmd` as administrator. The real `Recovery.vhdx` and matching ownership receipt were created. Hyper-V Manager showed Sequoia Running with 4096 MB assigned. After the user opened VMConnect, direct console observation confirmed **the macOS Sequoia installer actively installing onto the destination named macOS**, with an estimate of about 3 hours 46 minutes remaining. The destination VHDX was growing and receiving writes. Evidence is retained in `artifacts/boot-validation-20261028/sequoia-installing.jpg` and `live-boot-observation.json`.

This validates native conversion, attachment, boot past the earlier prohibitory screen, Recovery GUI and entry into installation at 4 GiB RAM for this VM. It does not yet validate installation completion, first normal boot or multi-CPU operation. No guest input, installer cancellation, shutdown, live mounting or forced log collection was performed during this observation.

## Integrated application path

After the user shut down Sequoia and requested a return to application work, disk-based recovery was integrated into automatic creation. `RecoveryDisk.cs` restores the verified DMG to a new fixed VHD using bounded managed UDIF/XML/raw/zlib parsing and GPT CRC checks. CreateVm converts it before registering the new VM and attaches all three disks. OpenCore disables DMG loading and enables Apple debug/panic and file logging (Target 67). Manual VHDX inputs retain their existing behavior. Preparation and helper preflight now reserve space for the intermediate and final recovery disks.

The real cached recovery's entire decoded payload matches the Python restore used for the successful guest attempt: SHA-256 `e2e3e7c508be3317873db7cae9d8d5b2ac1f602c9c6d425bad121a42188ba1e7` over 3,221,110,784 bytes, excluding the VHD footer. Windows' read-only virtual-disk APIs accept the managed output. Evidence is in `artifacts/managed-recovery-validation.json`. The rebuilt app adds a Diagnostics tab for bounded read-only EFI log export, trims the observed NUL preallocation tail, recognizes the captured kernel-allocation error, validates wizard input earlier and opens elevated VMConnect without blocking its UI thread.

Release build and 90 C# checks (including real EFI validation and recovery restoration), 13 management/initialization, 14 diagnostic/repair/export and 3 full creation-script double scenarios pass. The app was inspected without submitting a VM mutation. Sequoia was left powered off as requested. A fresh end-to-end creation through the integrated path and the new real helper log export remain integration gates; the decoder output itself is byte-verified against the boot-tested disk.
