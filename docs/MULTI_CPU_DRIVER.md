# Multi-CPU guest driver

Automatic media keeps macOS on one active processor (`cpus=1`) because MacHyperVSupport 0.9.9 (inside the pinned OSX-Hyper-V 0.4.0 EFI) and 1.0.0 can acknowledge SynIC messages on the wrong vCPU. That stalls VMBus enumeration on multi-CPU boots (`docs/VMBS_BOOT_STALL_FINDINGS.md`). `patches/MacHyperVSupport/0005-synic-eom-owner-cpu.patch` fixes it; upstream [acidanthera/MacHyperVSupport#12](https://github.com/acidanthera/MacHyperVSupport/pull/12) makes the same change and booted Sequoia 15.8 with 8, 12 and 16 vCPUs on an i9-13900HX host.

## Build

Run **Actions → Build multi-CPU MacHyperVSupport** (`.github/workflows/build-machypervsupport.yml`) on GitHub. It needs no Mac. It:

1. checks out MacHyperVSupport 1.0.0 (`cafcd34`) and MacKernelSDK `3f75008`, which includes [MacKernelSDK#25](https://github.com/acidanthera/MacKernelSDK/pull/25), the macOS 26 `IOPCIHostBridge` layout fix;
2. applies patch 0005, plus optionally the pinned heads of #13 (VMBus retry backoff), #14 (software cursor on macOS 26) and #15 (network start failure). The job fails if a PR head moved since it was pinned;
3. builds RELEASE with Xcode 16.4 on `macos-15-intel`, using commit-pinned ocbuild and Lilu bootstrap scripts;
4. uploads `MacHyperVSupport-1.0.0-pome-smp-RELEASE.zip` and `build-info.json` (SHA-256, size, applied commits).

The archive has the same `Kexts/` and `Tools/` layout as the upstream release, so `BootMedia.ReplaceHyperVDriverAsync` accepts it unchanged.

## Pin

1. Publish the zip as a release asset on this repository (allowlisted host `github.com` / `release-assets.githubusercontent.com`).
2. In `src/Infrastructure/VerifiedDownloader.cs`, point `UpstreamAssets.MacHyperVSupport` at that asset with the SHA-256 and size from `build-info.json`.
3. Keep `GuestDriverPolicy.PinnedDriverFixesSmp` (`src/Core/MacOsCatalog.cs`) **false** until the boot gate below passes. While false, only Tahoe uses the replacement driver and every guest keeps `cpus=1`.

## Boot gate before setting `PinnedDriverFixesSmp = true`

On a Windows 11 Hyper-V host, for Sequoia and Tahoe at least:

- create a VM with 4 and with 8 vCPUs, then cold-boot it after a host restart (the upstream failure is intermittent and worse after a host restart);
- the verbose boot shows every VMBus channel (storage included), not just channel 1, and the root disk mounts;
- `sysctl hw.ncpu` inside macOS reports the configured count, `loginwindow` runs and the Hyper-V heartbeat is healthy;
- export boot logs (**Diagnostics → Export boot logs…**) and keep them with the build's `build-info.json`.

### Evidence so far

- 2026-11-06, Tahoe 26.6.2 (25G83), i5-13420H host, Windows 11 Build 26300: build `3be53bf5…`
  (run 42126666183, commits `70c88ae beeaa7e ea619ac af94973`) replaced the kexts on an installed
  6-vCPU / 4 GB VM and `cpus=1` was removed. It booted to the setup assistant with no VMBus stall, and
  Hyper-V reported guest run time on all six virtual processors (72–83 % each). Not yet covered: a cold
  boot after a host restart, Sequoia, `sysctl hw.ncpu` inside macOS and the heartbeat check.

When this passes, set `PinnedDriverFixesSmp = true`. Every release then uses the replacement driver, `OpenCoreConfiguration` no longer adds `cpus=1`, and the resource recommendation can return to more than two processors (`ResourcePolicy.CompatibilityCpuCount`). Existing VMs keep their old EFI until their media is rebuilt.
