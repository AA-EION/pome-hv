# Windows client limitations

Microsoft's [DDA deployment documentation](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/deploy/deploying-graphics-devices-using-dda) specifies Windows Server hosts and supported Windows guests. macOS on DDA is outside that documented guest matrix. Windows 10/11 Pro hosting Hyper-V does not imply support for DDA.

Pome classifies client-host DDA as HostOsRestricted. It does not patch Microsoft binaries, bypass code integrity, disable VBS/HVCI or invoke undocumented edition unlocks. Available device-assignment cmdlets alone are not a reproducible support result.

GPU-P is a distinct guest/host driver protocol. It does not expose the complete physical NVIDIA PCI function required by an ordinary macOS NVIDIA driver. Pome includes no validated macOS GPU-P client and never treats a detected GPU-P capability as acceleration.

Standard VM creation remains independent of GPU assignment. Host graphics and networking are preserved except for explicitly reviewed Hyper-V feature enablement.
