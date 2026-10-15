# Engineering decisions

- **Native WinUI 3 / .NET 10:** follows the requested platform; no browser-based principal UI. SDK/package versions are pinned and lockfiles are committed.
- **Four assemblies, cohesive namespaces:** avoids dozens of empty project placeholders while retaining testable policy/adapters/privilege boundaries.
- **One-shot helper:** each reviewed operation requests UAC; no permanent elevated service or broad arbitrary-script endpoint. A signed recovery service is a future gate for DDA, not a fake completed feature.
- **Pinned complete EFI archive:** configuration and utilities stay matched. Do not mix MacHyperVSupport 1.0.0 into the 0.4.0 archive without validation.
- **Python only for the authenticated recovery utility:** a supporting component; all main application logic remains native .NET. Avoid rewriting Apple's recovery authentication/chunklist verification casually.
- **Conservative media/resource admission:** exact hashes, new operation directories, full disk capacity headroom, explicit unknown hardware attributes.
- **No destructive automatic rollback:** unregister only the newly created unbooted VM when setup fails; preserve all disks and evidence.
- **NullMoth download-only:** follows the user's explicit clarification. Its license remains applicable; no install, execution or guest security changes.
- **Kernel work isolated:** concrete correctness fixes and high-MMIO research patches are reviewable source; unavailable macOS SDK/guest validation is stated clearly.
