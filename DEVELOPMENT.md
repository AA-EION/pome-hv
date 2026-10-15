# Development

Use `tools/Build/Build.ps1` as the build/test entrypoint. Core policies must remain independent of UI, network and privileged APIs. Write regression tests for security boundaries, failure recovery and compatibility decisions; do not mock a guest boot and call it hardware validation.

Dependencies are pinned in project files, package lockfiles, `global.json`, and `data/upstream-audit.json`. A release change requires inspecting source and license changes, recording an exact asset digest, running the compatible validator, and obtaining guest evidence before changing capability status.

Before production release:

1. Complete threat review of elevated path handling, reparse-point race resistance, IPC peer authentication and media execution. Test standard user, administrator, UAC cancellation, alternate credentials and hostile same-user clients.
2. Sign application/helper binaries and installer. Install under a protected Program Files directory. Verify signatures at launch and in release CI. The preview does not yet enforce publisher pinning.
3. Validate EFI VHDX creation, firmware configuration, guest boot, installation, networking, lifecycle and recovery on supported hardware with entitlement for the guest.
4. Add multi-operation reconciliation, durable migration tests, updater trust metadata, guest services and accessibility coverage.
5. Produce an SBOM and component notices for exact distributed runtime packages and downloaded boot artifacts. Keep NullMoth outside distributions.

Do not publish research patches as driver releases. MacHyperVSupport patches require an Xcode/SDK build, protocol review and the hardware test gates in `docs/PCI_RESEARCH.md`.
