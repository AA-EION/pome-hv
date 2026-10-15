# Fault injection

Automated fixtures cover truncated/oversized IPC, corrupt/oversized/interrupted downloads, ignored Range headers, unknown redirect hosts, SQLite reopen, archive traversal/duplicate paths, invalid profiles, missing/cyclic kexts and MMIO overflow/exhaustion.

Pending disposable-host tests: terminate GUI after helper acceptance; terminate helper at each stage; power-loss simulation in a nested test host; failure after New-VM but before notes/marker persistence; failed VM unregister; mounted EFI failure; guest boot failure; UAC cancellation and reboot after prerequisite enablement. Required result: durable evidence, no deletion of unrelated files, no duplicate VM on a reused operation ID, and a clearly unresolved state when completion cannot be proven.

GPU dismount/reset failures cannot be tested through this preview because device assignment is disabled. Build the recovery service and host-display proof before enabling those tests.
