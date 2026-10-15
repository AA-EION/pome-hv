# Lenovo LOQ 15IRH8 / 82XV validation record

Status: **not executed**. Model number alone does not identify CPU/GPU/firmware or certify passthrough.

Record exact CPU model and CPUID, GPU vendor/device/subsystem IDs, driver, BIOS revision, Windows edition/build, RAM, panel and external monitor connections. Keep identifying serial numbers out of public logs. Obtain explicit permission before firmware, device or host-security changes.

| Test | Preconditions | Expected evidence | Actual / result |
|---|---|---|---|
| Host inventory | No mutations | CIM properties agree with Windows tools; unknown fields remain unknown | Not run |
| Standard VM | Hyper-V enabled; licensed recovery; no assigned GPU | EFI boot, installer, storage/network/input, completed guest installation | Not run |
| Integrated display mode | Supported OEM setting; reboot/recovery plan | Internal/external display routes and GPU visibility measured | Not run |
| Hybrid mode | Same hardware and drivers | Both GPUs enumerated; actual display ownership and power dependencies | Not run |
| Discrete-only mode | External recovery access | Host display dependency recorded; unsafe detachment blocked | Not run |
| DDA eligibility | Supported Server host; SurveyDDA; all functions inventoried | IOMMU/ACS, MMIO, reset and power evidence | Not run |
| Driver research | Original EFI saved; separately approved assignment | PCI/BAR/MSI/DMA/GSP stages with precise failure point | Not run |
| Metal validation | Driver initialized | Enumerated device plus real workload, stability and output | Not run |
| Return to host | Recovery display/network available | Detach VM assignment, restore device, verify Windows graphics | Not run |

For each row attach timestamp, preconditions, exact versions, expected/actual outcome, sanitized logs and recovery result. Do not generalize one configuration's result to all 82XV variants. Stop on unknown display ownership or missing recovery access. No firmware control is implemented in pome.
