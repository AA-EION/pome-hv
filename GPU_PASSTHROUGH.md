# GPU passthrough

Assignment is disabled in this preview. This is deliberate: the app cannot yet prove host display continuity, IOMMU isolation, reset capability, power independence, valid MMIO resources and a compatible guest driver.

On an eligible Server test host, a future implementation must first capture PNP state, driver, location paths, every GPU function, display routing and recovery access. It must persist rollback before disabling any device, receive explicit consent, and make restoration work after service failure or reboot. Dynamic memory, save/restore and checkpoints need DDA-specific policies.

A laptop MUX can reroute displays without releasing power or IOMMU dependencies. An integrated-GPU mode is only a candidate test condition, not an assignment guarantee. Never detach the only active display or infer display ownership from a video-controller inventory.

The isolated source patches under `patches/MacHyperVSupport` address concrete code defects and an opt-in high-MMIO experiment. They do not provide a complete DDA implementation. Follow the staged test plan before building or loading a patched guest driver.
