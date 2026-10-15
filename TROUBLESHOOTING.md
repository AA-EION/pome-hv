# Troubleshooting

| Symptom | Action |
|---|---|
| App will not launch | Keep the whole build folder together, check .NET 10 x64 runtime, inspect `%LOCALAPPDATA%/pome/startup-error.txt`. |
| Hyper-V unavailable | Check edition, Windows feature state, reboot requirement and account membership in Hyper-V Administrators. Unknown may mean access denied. |
| Allocation rejected | Refresh assessment; free RAM may have changed. Keep the documented host reserve. |
| SHA-256 mismatch | Do not bypass verification. Obtain the correct trusted asset/build record. Quarantined bytes are never used. |
| Automatic EFI rejects CPU | Use reviewed custom EFI. Do not guess a CPU profile based on a laptop model. |
| Python utility fails | Supply an existing Python 3 executable, verify network reachability to Apple, inspect the shown error. No remote bootstrap is required. |
| `ocvalidate` fails | Do not create/boot the guest from this output. Use matching OpenCore config and utility versions. |
| UAC cancelled | The operation remains visible for review; retry after verifying no resources were created. |
| VM created but no installer | Inspect VMConnect, boot order and the Apple recovery files. Creation is not installation completion. |
| Graceful shutdown fails | Guest integration may be missing. Shut down from the guest console; pome does not silently force power off. |
| GPU detected but unavailable | Detection does not imply DDA, macOS driver support or Metal. Read the GPU capability explanation. |

Export diagnostics from Activity. Review all files before sharing externally. Pome never uploads a diagnostic bundle automatically.
