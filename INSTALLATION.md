# Installation

This repository provides an engineering-preview build and an Inno Setup packaging definition, not a signed production installer. Build using [BUILDING](BUILDING.md), keep the assembled app folder intact, and launch `artifacts/app/Pome.UI.exe`.

Install the .NET 10 x64 runtime if it is missing. The GUI carries its Windows App SDK runtime locally. Automatic Apple recovery preparation also requires an existing Python 3 installation; enter its executable path in Create. Pome does not install Python or change execution policy.

Use This computer to inspect capabilities. Hyper-V can be enabled from its reviewed button with UAC. It may require a restart; pome never restarts Windows automatically. Unsupported Windows editions cannot be made eligible through that button.

The installer definition places application files under Program Files and registers normal uninstall. VM disks, downloaded media and diagnostics are outside the installed application directory and are not removed during uninstall. Re-running the installer repairs application files. A signed installer and upgrade validation are release gates.
