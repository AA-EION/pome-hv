# Test evidence

## Real media validation — 2026-11-05

`Pome.BootCheck media <dir> <helper> --version <v> --accept-license` runs the app's own unelevated media path for one catalog release, using real downloads. Steps: Apple recovery download with chunklist verification by the pinned `macrecovery.py`, the Tahoe driver swap, OpenCore configuration plus real `ocvalidate`, and restoration of the recovery to a fixed VHD. No VM, Hyper-V state or host disk is touched. Reports are in `artifacts/media-validation-20261105/media-<version>.json`. Host: Intel i5-13420H (Raptor Lake).

| Release | Apple build served | SMBIOS | CPU profile | Recovery layout | Result |
|---|---|---|---|---|---|
| 26 Tahoe | 26.6.2 (25G83) | iMac20,1 | Comet Lake | GPT | Passed; MacHyperVSupport 1.0.0 kexts and Tools swapped in (SHA-256 `490ebb1f…`) |
| 15 Sequoia (`-os latest`) | newest offered | iMac20,1 | Comet Lake | GPT | Passed |
| 12 Monterey | default | iMac20,1 | Comet Lake | GPT | Passed |
| 10.15 Catalina | default | iMac19,1 | Comet Lake | GPT | Passed |
| 10.13 High Sierra | default | iMac18,3 | Haswell | GPT | Passed |
| 10.11 El Capitan | default | iMac17,1 | Haswell | GPT | Passed |
| 10.10 Yosemite | default | iMac14,2 | Haswell | APM, 2048-byte blocks | Passed |
| 10.8 Mountain Lion | default | iMac14,2 | Haswell | APM, 2048-byte blocks | Passed |
| 10.7 Lion | default | iMac12,2 | Ivy Bridge | APM, 2048-byte blocks | Passed after two fixes (below) |

Findings fixed: Apple cut one Lion download mid-chunk. Pome previously kept the bad file and needed two manual retries; it now sets rejected files aside and re-downloads up to three times. Lion–Yosemite recoveries use an Apple Partition Map with 2 KB blocks rather than GPT; the decoder now validates both. The read-only `InspectRecovery.py` reports Tahoe's HFS+-compressed PlatformSupport files instead of failing.

Not covered: no guest was booted, because the host had 4.2 GB free RAM and a 4 GB guest needs 6 GB including the Windows reserve. Booting an APM recovery from a disk, AMD hosts and the multi-CPU driver build are still unverified. The AMD transform was checked against the real pinned AMD_Vanilla `patches.plist` (`--amd-patches`). Release build: 0 warnings, 0 errors; 117/117 C#, 16 management, 14 diagnostics and 3 creation-script scenarios.

### Guest test checklist (needs about 6 GB free RAM and your UAC approval)

1. In pome, **Create a Mac**, choose the version, keep 2 vCPUs and 4–8 GB, Default Switch, and tick **Start and open the console**.
2. In OpenCore pick the recovery entry, erase the blank disk as APFS (HFS+ Journaled for 10.12 and older) with Disk Utility → Show All Devices, then reinstall.
3. Record the furthest stage: OpenCore picker → Apple logo → Recovery GUI → installer → first boot → desktop. On failure, shut down or **Power → Turn off**, then **Diagnostics → Export boot logs…**.
4. Priority order: 26, 13, 10.15, 10.13, 10.10 (first APM disk boot).

Earlier validation on 2026-10-28:

- Release solution build and assembly: `artifacts/app/Pome.UI.exe` and its helper. Locked dependency versions are unchanged; NU1900 reports the unavailable vulnerability endpoint.
- 88/88 default C# checks; 90/90 with `--upstream-archive` and `--recovery-image`. Tests cover raw/zlib recovery restoration, every decoded fixture sector, VHD footer/checksum/size, GPT corruption, unsupported codecs, gaps, cancellation and refusing existing outputs/partials.
- 13 management/new-disk initialization scenarios, 14 diagnostic/repair/boot-export scenarios and 3 complete creation-script scenarios pass with cmdlet doubles. They do not manipulate real VMs.
- Every embedded PowerShell program and the diagnostic tool parses. Native Library and creation/media/review screens were inspected without submitting a VM action.
- The managed converter restored the real cached macOS 15.4.1 image. Its entire 3,221,110,784-byte decoded payload matches the earlier boot-tested Python restore. Windows OpenVirtualDisk/GetVirtualDiskInformation accepted it without mounting. Evidence: `artifacts/managed-recovery-validation.json`.
- Before this refocus, the user-approved real VM repair reached Recovery and an active Sequoia installer at 4 GiB. The user then shut down the VM. No further VM action or mount was performed during the application work. Installation completion and first normal boot remain pending.

Detailed build/media logs are `artifacts/pome-release-build.log` and `artifacts/pome-media-validation.log`. The integrated automatic creation and new boot-export paths have simulated Hyper-V coverage; they have not been exercised against another real VM.

Development validation on 2026-10-27 (ExHyperV workflow/UI port):

| Check | Result |
|---|---|
| Release solution build and side-by-side app assembly | Passed; 0 compiler errors. Two NU1900 warnings because NuGet's vulnerability endpoint was unreachable; locked dependencies unchanged. Output: `artifacts/exhyperv-preview/Pome.UI.exe` |
| C# executable suite | 83/83 passed outside the filesystem sandbox, including real local named-pipe ACL/peer tests; inside the sandbox, two existing pipe tests fail due to access restrictions |
| Embedded PowerShell syntax | All six scripts parsed |
| Actual management/lifecycle scripts with deterministic cmdlet doubles | 12/12 scenarios passed: ownership, stale state, partial-settings rollback, duplicate rename, checkpoint-policy recovery/ownership, read-only inspection, saved-state RAM gating, disconnection and exact-disk/no-shrink/checkpoint growth guards |
| Native UI inspection | Library, grouped Create and Appearance inspected at normal/maximized sizes. The fix batch compiles; a final visual confirmation could not resume because computer use remained stopped after Escape, even after user permission to resume |
| Real Hyper-V mutations / macOS installation | Not run in this audit; no VM was created, started, checkpointed, exported or resized |

Run `tools/Build/Build.ps1 -Configuration Release -OutputDirectory artifacts/exhyperv-preview` for a separate output folder while another build is running. See [port audit](docs/EXHYPERV_PORT_AUDIT.md) for scope and remaining gaps. The historical evidence below remains dated to its original validation.

Development validation on 2026-10-15:

| Check | Result |
|---|---|
| Complete Release solution build | Passed, 0 warnings / 0 errors |
| Default executable suite | 67/67 passed (includes user-only pipe ACL round-trip, negative owner/peer/CLI helper checks, journal/marker reconciliation, download cancellation, and cached recovery corruption quarantine) |
| Optional real upstream EFI configuration + `ocvalidate` | Passed; 68/68 with the default suite |
| Optional read-only Windows CIM host discovery | Passed; 68/68 with the default suite (verified VMMS Running → Hyper-V enabled even when unprivileged management query is restricted) |
| Elevated administrator inspection (`Pome.BootCheck inspect`) | Passed on Lenovo 82XV (`i5-13420H`, Windows 11 Pro for Workstations build 26300); Hyper-V enabled, `Default Switch` discovered |
| Apple recovery download & verification (`Pome.BootCheck prepare`) | Passed (`696-28424`, `BaseSystem.dmg` + signed `BaseSystem.chunklist` verified by upstream `macrecovery.py`, OpenCore configuration validated by `ocvalidate.exe`) |
| Native UI assembly & launch (`artifacts/app/Pome.UI.exe`) | Reassembled and launched cleanly |
| Four MacHyperVSupport patches against pinned source (`cafcd34...`) | `git apply --check`, sequential apply, and 21 MSVC (`cl.exe`) allocation/fault-injection/BAR-readback scenarios passed via `tools/Validation/TestPciPatches.py` |
| Full macOS kext compilation | Not run; macOS Xcode/Kernel SDK unavailable on Windows |
| Hyper-V VM/VHDX creation & guest boot | Pending user consent for guest security (`Secure Boot Off`, `SIP 0x03`) and freeing host RAM (requires ≥10 GiB free on host for 8 GiB guest + 2 GiB Windows reserve; currently ~4.5–5.3 GiB free) |
| GPU detachment, PCI mapping, GSP, Metal, device restoration | Not run; disabled on Windows client |
| Installer compilation/signing/install/uninstall | Not run; definition supplied |

The suite covers recommendation limits, capability separation, resource/consent validation, shell-shaped input rejection, MMIO width/alignment/overlap/overflow, kext graph errors, path boundaries, durable SQLite state, per-VM `operation.json` reconciliation, bounded IPC, user-only pipe ACLs, helper CLI/PID checks, authenticated download behavior, cancellation, archive safety, unique identities and OpenCore transformation.

HTTP tests are deterministic in-memory simulations. SQLite and file operations use real local temporary test files under `artifacts/tests`. Hardware identity tests do not certify guest compatibility.

```powershell
./tools/Build/Build.ps1 -Configuration Release
dotnet run --project tests/Unit/Pome.Tests.csproj -c Release --no-build -- --host-survey
dotnet run --project tests/Unit/Pome.Tests.csproj -c Release --no-build -- --upstream-archive C:\path\EFI-0.4.0-64-bit-RELEASE.zip
python tools/Validation/TestPciPatches.py
```

The optional archive test checks the pinned SHA-256 before extracting and running the matching validator. It does not install software, mount a VHDX or boot macOS. Use [integration procedures](tests/Integration/README.md), [fault injection](tests/FaultInjection/README.md), and the [82XV plan](tests/Hardware/LOQ-82XV.md) for remaining validation.
