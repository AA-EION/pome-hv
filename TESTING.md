# Test evidence

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
