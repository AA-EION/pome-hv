# Review-only MacHyperVSupport patches

Base: `cafcd34daea5e58633112a1e63cef1f07bbc8404`.

Apply in numeric order (`0001` through `0004`) to a disposable checkout. Pome never applies these to a guest or includes a patched kext. Patch 1 fixes response/bounds/MSI logic. Patch 2 is a gated high-MMIO experiment. Patch 3 reserves all BARs transactionally before programming and refuses active decoders. Patch 4 adds ACPI range admission checks, root allocator locking, BAR attribute preservation/readback verification, and bridge `stop()` cleanup.

```powershell
python tools/Validation/TestPciPatches.py
```

`tools/Validation/GeneratePciPatches.py` regenerates the 4 diffs from the exact audited checkout under `.research/MacHyperVSupport` without changing it. `tools/Validation/TestPciPatches.py` verifies `git apply --check`, applies all 4 patches to a temporary copy, and compiles/tests the extracted allocation method with MSVC (21 scenarios).

See `docs/PCI_RESEARCH.md` for root cause, workaround candidates, known gaps, validation and rollback gates. See `LICENSE.upstream.txt` for the original BSD notice. No macOS SDK build or runtime validation has been performed.
