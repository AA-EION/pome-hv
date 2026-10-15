# Hardware compatibility

Target: x64 Windows 10 build 19041+ and Windows 11 Pro, Enterprise or Education. Hyper-V requires appropriate CPU virtualization, SLAT, firmware enablement and OS support. Read-only discovery reports what CIM and Hyper-V actually return. Access denial remains unknown instead of being classified as missing hardware.

Automatic boot profiles are limited to an explicit Intel family-6 model set in `BootMedia.DetectIntelProfile`. Tiger Lake, Alder Lake and selected Raptor Lake models receive the upstream Comet Lake CPUID mask. Other processors need reviewed custom EFI; AMD automatic kernel patches are not implemented.

The Sequoia profile requires at least 8 GiB guest RAM and 64 GiB disk capacity. Recommendations preserve 6 GiB of installed RAM and 2 GiB of available RAM for the host; allocations are checked again at execution. Storage admission reserves room for full disk capacity, copied media and staging. Dynamic VHDX allocation does not guarantee future free space.

Lenovo 82XV is a test family, never a hard-coded hardware profile. CPU, GPU and firmware must be discovered per machine. MUX, Optimus routes, eDP ownership, power dependencies, PCI BAR sizes and IOMMU isolation remain unknown. See `tests/Hardware/LOQ-82XV.md`.

Machine-readable status is in `data/compatibility.json`. No hardware has been certified for guest boot or graphics acceleration by this project.
