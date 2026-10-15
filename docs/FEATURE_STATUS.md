# Feature status

`Implemented` describes code, not proof that a guest works. Tests and hardware evidence are separate.

| Capability | Code | Validation / remaining gate |
|---|---|---|
| Native WinUI app and navigation | Implemented | Application launched; native UI inspection performed |
| Host CIM / GPU / Hyper-V discovery | Implemented | Read-only host integration test; unknown topology remains explicit |
| Resource and capability policies | Implemented | Automated fixtures |
| VM creation / CPU / static RAM / disks / firmware | Implemented | Physical Hyper-V mutation tests not run |
| Lifecycle / console | Implemented | Needs a pome-managed test VM |
| Existing VM management | Read-only discovery | Adoption/import not implemented |
| Prerequisite enablement | Implemented | Requires explicit UI review/UAC; not executed during development |
| EFI download and OpenCore configuration | Implemented | Published digest checked; generated config accepted by real `ocvalidate` |
| Apple recovery acquisition | Implemented via pinned upstream utility | Not exercised against Apple during development; OS version is server-selected |
| EFI VHDX construction | Implemented | Needs authorized Hyper-V integration test |
| macOS boot / installation | Unverified | No boot or installation evidence |
| SQLite journal / failure records | Implemented | Local persistence tests; no unattended crash reconciliation |
| Full backup / update / rollback | Not implemented | Trust metadata, transactional updates and cold-boot tests required |
| GPU inventory / gating / MMIO planning | Implemented | Unit tests; resource sizes and display ownership are unknown |
| NullMoth separate download | Implemented | Simulated downloader tests; no driver install or execution |
| PCI correctness / high-MMIO patches | Source candidates | Apply check only; SDK build, teardown review and hardware tests pending |
| DDA, GPU-P, Metal | Disabled / unverified | All hardware milestones pending |
| Diagnostics | Allowlisted JSON export | Local-only; Event Log integration not implemented |
| Installer | Definition provided | Unsigned, not compiled or installed in this session |

The master brief's production acceptance criteria are not all met. This repository is a buildable engineering preview with a documented route to the remaining gates.
