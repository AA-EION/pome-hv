# Opt-in integration procedures

Ordinary CI runs only local policy/storage/IPC/simulated HTTP tests. `--host-survey` adds the read-only CIM test; `--upstream-archive <path>` authenticates the pinned EFI archive, generates configuration and executes its matched `ocvalidate` unelevated.

For VM mutation testing, use an explicitly approved disposable Windows Hyper-V host, a separate test data directory, a trusted recovery source and appropriate macOS entitlement. Run Create through the native review/UAC flow. Verify generation 2, static RAM, CPU count, guest Secure Boot off, EFI first in boot order, expected VHDX paths, switch and unique notes/identity. Confirm the VM remains off after creation.

Then start via pome, inspect VMConnect and document actual boot/install stages. Graceful shutdown and networking need real guest integration. Test duplicate names, insufficient storage, missing switch, UAC cancellation, invalid hashes and mid-stage failures. Preserve disk evidence; never run cleanup by VM name or wildcard.

No write-enabled Hyper-V integration test is run automatically. No successful guest boot is assumed.
