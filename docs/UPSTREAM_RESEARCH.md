# Upstream audit — 2026-10-15

Exact reviewed branch heads and license-file SHA-256 values are recorded in `data/upstream-audit.json`. These are research pins, distinct from runtime release pins. Source was cloned locally; relevant implementation files were inspected in addition to project documentation. This is a scoped initial audit, not a claim that every branch, issue or discussion was reviewed.

| Project | Source inspected / role | Integration decision |
|---|---|---|
| OSX-Hyper-V 0.4.0 | `src/config.yml`, `src/build.lock`, ACPI files, VM/recovery/EFI PowerShell | Architectural reference and opt-in authenticated release archive. Do not execute its moving-branch bootstrap. |
| MacHyperVSupport 1.0.0 | PCIRoot, PCIBridge, private resource setup, MSI, changelog | Tahoe PCI changes exist in standalone source; high-BAR allocation remains unresolved. Two isolated source patches. |
| NullMoth 2.0.2 | `kexts/NVRM/os-xnu.cpp`, `docs/CARD-SUPPORT.md`, driver layout, release/license | Download-only, no bundled code. Physical PCI/IOKit/GSP path needs Hyper-V-specific validation. Current README emphasizes Sequoia, not a verified Tahoe matrix; small-BAR physical validation remains pending upstream. |
| 1401 2.0.2 | `windows/App/PackageVerification.cs`, Engine layout, release inputs, license | Research only. Noncommercial code is not incorporated into pome. Its physical-disk installer is not a Hyper-V EFI generator. |
| OpCore-Simplify | `Scripts/kext_maestro.py`, hardware/compatibility modules | Bare-metal PCI matching is not directly applicable to a synthetic guest. Implement a small independent dependency resolver and preserve upstream Hyper-V templates. |
| OpenCorePkg | Sample plist, XML parser, macrecovery verifier and command line | Match config and `ocvalidate` versions. Use pinned release utilities unelevated. Generated XML must be UTF-8 without BOM. |
| Lilu / WhateverGreen | DeviceInfo PCI bridge traversal and driver relationship | Detection of a synthetic bridge is a separate concern from BAR allocation. No blind WhateverGreen injection. |
| VirtualSMC | Source/license and boot dependency | Required SMC emulation comes from the selected EFI release, not host physical SMC assumptions. |
| NVIDIA open GPU modules | COPYING, GPU resource/OS abstraction layout | Linux module architecture is reference material, not a Windows or macOS drop-in driver. |
| Microsoft virtualization docs | SurveyDDA device checks and DDA deployment restrictions | Read-only evidence model; no client edition bypass. |

## Actual runtime archive

`EFI-0.4.0-64-bit-RELEASE.zip`, published size 14,408,392 bytes, SHA-256 `43b68f135f908f046058c7cb4a6968a9caec57ac22bbaadebcf891289375a428`. The archive was downloaded and its digest matched. Its build lock lists:

- OpenCore 1.0.6, commit `64e3b5811ee17804cc04236618dc968ca57378a6`.
- MacHyperVSupport 0.9.10, commit `9adc712b0689ef04dcceda486a123b0ae913c266`.
- Lilu 1.7.2, commit `161537ec43a19abf38adb44543ded273305d7bf0`.
- VirtualSMC 1.3.8, commit `55b89a23f51beda82581dbab795615838a3e6e56`.
- OcBinaryData commit `e74e533d8f89c1d5014cfb47c185502bf415741f`.

The latest standalone MacHyperVSupport release is 1.0.0. It is not silently substituted into the older assembled EFI. Such an update requires a new integration pin and boot evidence. Tahoe remains disabled in pome even though individual upstream components contain Tahoe changes.

NullMoth archive `nullmoth-nvidia-2.0.2.tar.gz`: size 378,125,394 bytes, SHA-256 `acf939c2829ae31a1840866cefc814c4b819168da1eb5f1ea257ea3e63e8753d`, obtained from official release metadata. Pome's implementation downloads it only on explicit user action. No archive was bundled or installed during development.

## Feasibility and update policy

Native Windows orchestration is feasible with supported Hyper-V APIs. macOS guest compatibility depends on the selected guest stack and real hardware tests. Synthetic graphics is the first path; it is not Metal acceleration. Standard creation is isolated from GPU experiments.

Do not update to latest automatically. Review source/license changes, pin a new complete compatibility unit, validate configuration, cold-back up the current EFI, test boot and retain rollback. Signed updater metadata, atomic live update and automated rollback are not implemented in this preview.

References: [OSX-Hyper-V](https://github.com/Qonfused/OSX-Hyper-V), [MacHyperVSupport](https://github.com/acidanthera/MacHyperVSupport), [NullMoth](https://github.com/nullmoth/nvidia-macos-driver), [1401](https://github.com/nullmoth/1401), [OpCore-Simplify](https://github.com/lzhoang2801/OpCore-Simplify), [Dortania](https://dortania.github.io/OpenCore-Install-Guide/), [Microsoft DDA](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/deploy/deploying-graphics-devices-using-dda).
