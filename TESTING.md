# Test evidence

Development validation on 2026-10-15:

| Check | Result |
|---|---|
| Complete Release solution build | Passed, 0 warnings / 0 errors |
| Default executable suite | 52/52 passed |
| Optional real upstream EFI configuration + `ocvalidate` | Passed; 53/53 with the default suite |
| Optional read-only Windows CIM host discovery | Passed; 53/53 with the default suite |
| Native UI launch | Passed after fixing an invalid WinUI icon name |
| Native Library/This computer navigation inspection | Performed with Windows UI automation |
| Two MacHyperVSupport patches against pinned source | `git apply --check` passed |
| Kernel patch compilation | Not run; macOS kernel SDK unavailable |
| Hyper-V VM/VHDX/feature mutations | Not run |
| Apple recovery download / installation / guest boot | Not run |
| GPU detachment, PCI mapping, GSP, Metal, device restoration | Not run |
| Installer compilation/signing/install/uninstall | Not run; definition supplied |

The suite covers recommendation limits, capability separation, resource/consent validation, shell-shaped input rejection, MMIO width/alignment/overlap/overflow, kext graph errors, path boundaries, durable SQLite state, bounded IPC, authenticated download behavior, archive safety, unique identities and OpenCore transformation.

HTTP tests are deterministic in-memory simulations. SQLite and file operations use real local temporary test files under `artifacts/tests`. Hardware identity tests do not certify guest compatibility. Pure MMIO planning tests do not execute kernel-patch code.

```powershell
./tools/Build/Build.ps1
dotnet run --project tests/Unit/Pome.Tests.csproj -- --host-survey
dotnet run --project tests/Unit/Pome.Tests.csproj -- --upstream-archive C:\path\EFI-0.4.0-64-bit-RELEASE.zip
```

The optional archive test checks the pinned SHA-256 before extracting and running the matching validator. It does not install software, mount a VHDX or boot macOS. Use [integration procedures](tests/Integration/README.md), [fault injection](tests/FaultInjection/README.md), and the [82XV plan](tests/Hardware/LOQ-82XV.md) for remaining validation.
