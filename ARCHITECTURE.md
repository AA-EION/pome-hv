# Architecture

Pome has four runtime layers and an executable test project. Small modules share a project until independent packaging or platform requirements justify a separate assembly; there are no empty subsystem projects.

| Layer | Responsibility |
|---|---|
| `src/Core` | Typed host/VM/operation contracts; capability assessment; resource and GPU safety policies; plan validation; MMIO allocator; kext dependency ordering; OpenCore XML transformation and unique identities |
| `src/Infrastructure` | Windows CIM and Hyper-V adapters; embedded PowerShell execution; SQLite journal; pipe framing and UAC broker; download integrity and resume; safe archive extraction; boot-media preparation |
| `src/PrivilegedService` | One-shot elevated helper; bounded request dispatch; independent validation; copy/hash checks; Hyper-V mutation |
| `src/UI` | WinUI 3 views, MVVM state, review dialogs and platform navigation |

The UI runs as the user. Hardware assessment and downloads are unelevated. A reviewed operation launches a fresh elevated helper through UAC. The random named pipe is current-user-only and each peer verifies the other's PID. There is no arbitrary command/script/file-execution verb. PowerShell programs are embedded in the infrastructure assembly; JSON is base64 encoded as data. Each mutation runs to completion after acceptance; the GUI does not terminate it on cancellation.

Creation writes only to a new `pome-<operation-guid>` subdirectory of a selected existing local data directory. Media is copied before use and verified again by the helper. The backend creates a dynamic destination VHDX, a Gen2 VM with static memory, an existing switch, and guest firmware boot order. It never starts the VM automatically. On configuration failure it attempts to unregister only the just-created VM and retains disks and stage records.

Automatic EFI preparation preserves the full pinned upstream template, modifies identity and reviewed CPU fields, and runs the matching OpenCore validator unelevated. The helper regenerates the same configuration shape with a fresh persisted identity. No bare-metal motherboard settings are inferred from the host.

SQLite tracks UI operations. `operation.json` beside VM resources tracks backend stages. This preview exposes unresolved records; it does not run an unattended reconciliation service. File-level journals do not guarantee automatic recovery after all crash windows. See [RECOVERY](RECOVERY.md).
