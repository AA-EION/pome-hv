# GPU acceleration

Three modes are modeled separately: standard synthetic graphics (experimental), GPU-P (unsupported), and DDA (host-restricted on client Windows; unverified on Server). No mode is labelled Metal-accelerated in pome.

NullMoth 2.0.2 is available as an optional download from its author's release URL. The archive's published SHA-256 and length are pinned in `UpstreamAssets`. It is not a pome package dependency, installer payload, CI artifact, automatic install step or bundled source. The UI links its license and asks for acceptance before fetching. Separate delivery does not remove PolyForm Noncommercial restrictions or component-specific terms.

Current upstream documentation identifies Sequoia x86_64 and Turing-or-newer cards, with strongest reported testing on RTX 5060. Do not generalize that to every mobile GPU or to Tahoe. The initial master brief's broader Tahoe expectation is not considered verified.

Driver stages remain distinct: host eligibility → assignment → PCI enumeration → BAR mapping → MSI/DMA → GSP → NVRM → NVAccel/framebuffer → Metal device → actual workload → stable teardown. Source inspection and a successful download establish none of those runtime milestones.

See [PCI investigation](docs/PCI_RESEARCH.md) and [upstream audit](docs/UPSTREAM_RESEARCH.md). No NullMoth guest security changes or installer scripts are executed by pome.
