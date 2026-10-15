# User guide

1. Open pome and choose **Inspect this computer**. Unknown values mean insufficient evidence; they do not imply failure or support.
2. If Hyper-V is unavailable, read the assessment. **Enable Hyper-V** shows a review before UAC and never reboots automatically. Non-admin discovery may also be unavailable because the account lacks Hyper-V permissions.
3. Choose **Create a virtual machine**. Select an existing local storage directory, CPU, static memory and disk capacity. Recommendations account for currently available RAM and host reserves. Existing switches are listed; Disconnected is explicit. Pome does not create or modify host networks.
4. Automatic media preparation supports the reviewed Intel family-6 matrix. Supply an installed Python 3 executable and review the Apple license and guest security choices. It downloads pinned EFI tooling and uses its recovery downloader. The initial recovery download may take several minutes. Check the version offered inside Apple's installer; server-selected recovery content is not a fixed OS-version promise.
5. Alternatively, turn off automatic media and supply a trusted EFI VHDX with its SHA-256 digest. Prepare a unique identity and CPU profile upstream. Include recovery on the EFI disk or supply a separate recovery VHDX and digest. Hashing an untrusted file yourself does not establish its provenance.
6. Review and create. Only after media preparation and validation does pome ask for UAC. The VM stays off until you explicitly start it.
7. Refresh Library, select the VM, Start and Open console. Complete Disk Utility and macOS installation in the guest. Pome does not claim to detect installation completion.

Graceful shutdown uses Hyper-V integration. If the guest cannot respond, the operation may fail; pome does not force power off. VMs not marked as created by pome are shown read-only. Import/ownership adoption is not implemented.

NullMoth is an optional separate download under This computer. Review PolyForm Noncommercial 1.0.0. The verified archive is saved under `%LOCALAPPDATA%/pome/downloads`; no installer, guest security change or GPU assignment runs. A download is not evidence of hardware compatibility.
