# Review-only MacHyperVSupport patches

Base: `cafcd34daea5e58633112a1e63cef1f07bbc8404`.

Apply in numeric order to a disposable checkout. Pome never applies these to a guest or includes a patched kext. Patch 1 fixes response/bounds/MSI logic. Patch 2 is a gated high-MMIO experiment with unresolved resource-lifecycle and hardware gates.

```powershell
git -C path/to/MacHyperVSupport apply --check path/to/0001-pci-response-and-msi-correctness.patch path/to/0002-opt-in-high-mmio-research.patch
```

`tools/Validation/GeneratePciPatches.py` regenerates the diff from the exact audited checkout under `.research/MacHyperVSupport` without changing it. The generator deliberately fails if source context or commit differs.

See `docs/PCI_RESEARCH.md` for root cause, workaround candidates, known gaps, validation and rollback gates. See `LICENSE.upstream.txt` for the original BSD notice. No macOS SDK build or runtime validation has been performed.
