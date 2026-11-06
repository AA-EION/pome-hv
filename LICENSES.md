# Licenses and delivery policy

Pome's original code is MIT. MacHyperVSupport diff context and changes remain subject to its BSD-3-Clause notice in `patches/MacHyperVSupport/LICENSE.upstream.txt`. Exact audited commits and license-file digests are in `data/upstream-audit.json`.

| Upstream | Observed license | Pome integration |
|---|---|---|
| OSX-Hyper-V | BSD-3-Clause; archive includes separately licensed components | Optional pinned download, not checked into pome |
| MacHyperVSupport | BSD-3-Clause | Source research patches with notice; boot archive carries an older pinned version. The official 1.0.0 release zip is an optional pinned download (Tahoe, and every version once a multi-CPU build is pinned); the CI workflow builds it from source with pome's patch for the user's own use |
| AMD_Vanilla (`eaf52ef292abf4ebec899df6d48626569ba50cc6`) | No license file published | `patches.plist` is downloaded at runtime for AMD hosts only, pinned by commit and SHA-256; never bundled or redistributed by pome |
| OpenCorePkg, Lilu, VirtualSMC, WhateverGreen, OpCore-Simplify | BSD-style notices, generally BSD-3-Clause | Source reference; OpenCore/Lilu/VirtualSMC arrive only in downloaded EFI |
| NullMoth NVIDIA driver and 1401 | PolyForm Noncommercial 1.0.0 at repository root; other components have their own terms | The NVIDIA driver itself stays a download-only archive. Its Metal plugin and IOAccelerator (`18ba05f`) are imported, renamed, into `gpu/guest/metal/plugin` and `accel` under PolyForm Noncommercial 1.0.0 with the Required Notice (`gpu/guest/metal/LICENSE`, `NOTICE`); 1401 is research only |
| metal2vulkan translator (in NullMoth, `gpu/guest/metal/translator`) | LGPL-3.0-or-later | Source imported unmodified except the NVMTL→PVGMTL rename; built as its own dylib |
| virglrenderer (`244fc1a`) and venus-protocol 1.1.3 | MIT | Fetched at build time by `gpu/host/build-windows.ps1`, patched by `apply_windows.py`, shipped as `libvirglrenderer-1.dll` |
| Mesa (`0c106844`) | MIT (Venus, util); other drivers have their own notices | Fetched by the guest CI workflow; pome adds `vn_renderer_pvg.c` |
| MinGW-w64 winpthreads | MIT and BSD-3-Clause | `libwinpthread-1.dll` shipped beside pvg-host |
| Linux, FreeBSD, XNU, VirtualBox, gfxstream sources | GPL-2.0, BSD-2-Clause, APSL-2.0, GPL-3.0, Apache-2.0 | Read as references for the virtual GPU (`gpu/README.md`); no code copied |
| NVIDIA open GPU kernel modules | MIT except separately identified dual MIT/GPL components | Research only; no code incorporated |
| Microsoft Virtualization-Documentation | CC-BY-4.0 documentation; inspect code-specific notices separately | References only |
| Microsoft.WindowsAppSDK, Microsoft.Data.Sqlite and transitives | Review restored package notices for exact versions | Runtime dependencies, pinned with lockfiles |
| ExHyperV (`f51e0667a8499e88e3f93d4beb221dc48e14f9b5`) | GPL-3.0 repository license | Local feature/UI reference only; no source, assets, DLLs or dependencies copied into pome |

Do not describe PolyForm Noncommercial as an unrestricted open-source license. Providing an upstream download does not confer commercial-use rights. Read [the actual NullMoth license](https://github.com/nullmoth/nvidia-macos-driver/blob/v2.0.2/LICENSE) and component notices before use or redistribution. No affirmative commercial legal clearance is claimed here.

Apple recovery is downloaded directly by its recovery protocol, never redistributed in pome. Each macOS license (for example [Sequoia's](https://www.apple.com/legal/sla/docs/macOSSequoia.pdf); see [all Apple licenses](https://www.apple.com/legal/sla/)) limits virtualization to qualifying Apple-branded hardware and other conditions. User acknowledgement is not a substitute for entitlement.

Release packaging must include a complete runtime SBOM and corresponding license texts before public distribution. The preview packaging definition is not a completed redistribution audit for all transitive runtime assets.
