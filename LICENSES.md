# Licenses and delivery policy

Pome's original code is MIT. MacHyperVSupport diff context and changes remain subject to its BSD-3-Clause notice in `patches/MacHyperVSupport/LICENSE.upstream.txt`. Exact audited commits and license-file digests are in `data/upstream-audit.json`.

| Upstream | Observed license | Pome integration |
|---|---|---|
| OSX-Hyper-V | BSD-3-Clause; archive includes separately licensed components | Optional pinned download, not checked into pome |
| MacHyperVSupport | BSD-3-Clause | Source research patches with notice; boot archive carries an older pinned version |
| OpenCorePkg, Lilu, VirtualSMC, WhateverGreen, OpCore-Simplify | BSD-style notices, generally BSD-3-Clause | Source reference; OpenCore/Lilu/VirtualSMC arrive only in downloaded EFI |
| NullMoth NVIDIA driver and 1401 | PolyForm Noncommercial 1.0.0 at repository root; other components have their own terms | Download-only driver archive, no bundled source/binaries or execution; 1401 is research only |
| NVIDIA open GPU kernel modules | MIT except separately identified dual MIT/GPL components | Research only; no code incorporated |
| Microsoft Virtualization-Documentation | CC-BY-4.0 documentation; inspect code-specific notices separately | References only |
| Microsoft.WindowsAppSDK, Microsoft.Data.Sqlite and transitives | Review restored package notices for exact versions | Runtime dependencies, pinned with lockfiles |

Do not describe PolyForm Noncommercial as an unrestricted open-source license. Providing an upstream download does not confer commercial-use rights. Read [the actual NullMoth license](https://github.com/nullmoth/nvidia-macos-driver/blob/v2.0.2/LICENSE) and component notices before use or redistribution. No affirmative commercial legal clearance is claimed here.

Apple recovery is downloaded directly by its recovery protocol, never redistributed in pome. The [Sequoia license](https://www.apple.com/legal/sla/docs/macOSSequoia.pdf) limits virtualization to qualifying Apple-branded hardware and other conditions. User acknowledgement is not a substitute for entitlement.

Release packaging must include a complete runtime SBOM and corresponding license texts before public distribution. The preview packaging definition is not a completed redistribution audit for all transitive runtime assets.
