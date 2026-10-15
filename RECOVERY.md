# Recovery

The UI records requests in `%LOCALAPPDATA%/pome/operations.db`. The helper creates a new `pome-<operation-guid>` storage directory and writes `operation.json` before copying media. During Hyper-V setup it writes stage transitions and the new VM identifier.

If setup fails, pome attempts to remove only the registration it just created and has never started. It preserves all virtual disks, recovery inputs, identity and evidence. Do not delete files to retry blindly. First check Activity, the marker, and Hyper-V Manager for the recorded VM ID and `pome:v1:<operation-guid>` notes.

Crash windows exist between VM creation and marker/notes persistence. Reconcile using the unique storage path and operation ID; never remove a VM based only on its human-readable name. An existing operation directory blocks reusing that ID. The preview does not automatically reconcile these windows, resume partially copied media, or clean orphaned resources.

A failed download retains `.part` for retry. If the digest fails, the file is renamed `.rejected-<guid>` and never promoted. A verified final file is reused only after hashing it again. Apple recovery's upstream Python downloader has separate behavior and is not covered by the generic resumable GitHub downloader.

No host GPU is detached, so this version needs no device-restoration service. Future DDA support must make restoration independent of the GUI before enabling assignment.
