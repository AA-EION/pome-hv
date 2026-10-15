# Security

The GUI is unelevated. The helper exposes only Create, Start, Shutdown and EnableHyperV, validates creation again, accepts bounded JSON over a current-user named pipe, and verifies the UI PID. The UI checks the elevated helper PID. User input is data, never interpolated PowerShell code. Host security settings, firmware, GPUs and Microsoft binaries are not modified.

All reviewed creation requests consent to guest Secure Boot being disabled. Automatic EFI uses an upstream template with `csr-active-config=03000000`, relaxing guest kext signing and filesystem protection. NullMoth would require additional guest changes, but pome does not apply them. Host Secure Boot and guest Secure Boot are distinct.

Downloads use HTTPS with a small redirect-host allowlist, pinned size and SHA-256, bounded writes, partial-file resume and atomic promotion. Rejected files are quarantined. Archives reject traversal, symlinks, duplicate destinations and excessive expansion. The upstream boot utility is executed unelevated only after authenticating its entire archive. No moving-branch bootstrap script is executed.

Diagnostics export only a fixed allowlist of capability and operation-state fields. It excludes raw hardware IDs, serials, paths, VM names and journal messages. Local operation markers and `identity.json` contain private VM information; do not share them without review.

**Preview limitations:** the helper is not signed, publisher pinning is absent, path checks are not yet resistant to every malicious concurrent directory/reparse-point swap, and metadata recovery is not an unattended service. Install under protected Program Files before general distribution. Do not expose a development checkout helper as a persistent privileged service. A user-controlled Python path is permitted only in the unelevated UI and is never accepted as an elevated command.

Report security findings privately to the repository maintainers when a reporting channel is configured; do not publish identifiers or sensitive logs. No external reporting or telemetry is implemented by pome.
