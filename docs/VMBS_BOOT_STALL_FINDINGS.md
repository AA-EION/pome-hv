# macOS 15 (Sequoia) Hyper-V Boot Stall Analysis: `busy timeout: 'VMBS' (a,4020001)`

## 1. Observed Symptom

When booting the generated `Sequoia` Generation 2 Hyper-V VM (4 vCPUs, 8 GiB RAM, `OSX-Hyper-V 0.4.0` EFI + `MacHyperVSupportMonterey.kext`, Comet Lake `0x0A0655` CPUID spoof on Intel Core i5-13420H, Windows 11 Pro Build 26200):

- OpenCore (`BOOTx64.efi`) loads `com.apple.recovery.boot\BaseSystem.dmg` into the memory ramdisk (`md0`) and transitions cleanly past `[EB|LOG:EXITBS:START]`.
- The XNU kernel initializes CPUs, ACPI, and mounts `/` from the recovery ramdisk.
- `launchd` starts userspace services (`fskitd`, `systemkeychain`, `trustd`, `locationd`, `cvmsCompAgent_x86_64`), and `com.apple.fskit.msdos` probes the FAT32 `EFI.vhdx` disk attached to the Hyper-V synthetic SCSI controller (`HyperVStorage`).
- Before `WindowServer` / the macOS Recovery GUI can launch, `IOKitWaitQuiet()` stalls and logs every 240 seconds:
  ```text
  busy timeout[0], (240s): 'VMBS' (a,4020001)
  busy timeout[1], (240s): 'VMBS' (a,4020001)
  ```
- Keyboard input (`HyperVKeyboard`) does not respond because VMBus enumeration has not completed.

---

## 2. Decoding XNU `IOService` State `(a, 4020001)`

From XNU `iokit/IOKit/IOService.h` and `iokit/Kernel/IOServicePrivate.h`:

- **`__state[0] = 0x0000000a`**:
  - `0x00000002 = kIOServiceRegisteredState`
  - `0x00000008 = kIOServiceFirstPublishState`
  - Notably, `kIOServiceMatchedState (0x00000004)` is **not** set yet.
- **`__state[1] = 0x04020001`**:
  - `0x04000000 = kIOServiceConfigState`
  - `0x00020000 = kIOServiceConfigRunning`
  - `0x00000001 = busyState = 1` (masked by `kIOServiceBusyStateMask = 0x3ff`)

In `IOService::doServiceMatch()` (`iokit/Kernel/IOService.cpp` lines 5506–5578), `kIOServiceConfigRunning (0x00020000)` is set immediately before `probeCandidates(matches)` and cleared immediately after `probeCandidates(matches)` returns. Therefore:

1. `VMBS` (`\_SB.VMOD.VMBS`, `IOACPIPlatformDevice` with `_HID = VBS0001` from `SSDT-HV-VMBUS.aml`) matched `HyperVController`.
2. `VMOD` (`\_SB.VMOD`, `HyperVPCIRoot`) is **not** in the busy list — `HyperVPCIRoot::start(VMOD)` completed and registered `HyperVPCIRoot`.
3. A kernel thread is blocked inside `HyperVController::start(VMBS)` -> `HyperVVMBus::attach()` -> `HyperVVMBus::scanVMBus()`.

---

## 3. Root Cause Identified in `MacHyperVSupport` (`HyperVController::sendSynICEOM`)

### Evidence that VMBus Partially Enumerated Before Freezing
In the console screenshot (`media_1792109717646_017b8e07.png`, line 12):
```text
com.apple.xpc.launchd|... (pid/231/com.apple.fskit.msdos.00000000-0000-0000-0000-000000000001 [361])
```
`fskit.msdos` only runs when a FAT32 block device (`EFI.vhdx`) is discovered. In a Generation 2 Hyper-V VM, `EFI.vhdx` is attached to the synthetic SCSI controller (`HyperVStorage`) over VMBus. This proves:
- `HyperVController::start(VMBS)` passed `waitForMatchingService("HyperVPCIRoot")`, `initHypercalls()`, `initInterrupts()`, and `connectVMBus()`.
- `HyperVVMBus::scanVMBus()` sent `kVMBusChannelMessageTypeRequestChannels` and began receiving `kVMBusChannelMessageTypeChannelOffer` messages from Hyper-V, registering child `HyperVVMBusDevice` nubs (including `HyperVStorage`), before stalling while waiting for `kVMBusChannelMessageTypeRequestChannelsDone`.

### The Bug in `HyperVController::sendSynICEOM(UInt32 cpu)`
In `MacHyperVSupport/Controller/HyperVControllerInterrupts.cpp` (lines 279–325):

```cpp
void HyperVController::sendSynICEOM(UInt32 cpu) {
  //
  // Only EOM if there is a pending message.
  //
  _cpuData[cpu].messages[kVMBusInterruptMessage].type = kHyperVMessageTypeNone;
  if (!_cpuData[cpu].messages[kVMBusInterruptMessage].flags.messagePending) {
    return;
  }
  
#if __MAC_OS_X_VERSION_MIN_REQUIRED >= __MAC_10_6
  //
  // Change to desired CPU if needed.
  //
  if (cpu != cpu_number()) {
    if (!IOSimpleLockTryLock(_preemptionLock)) {
      HVSYSLOG("Failed to disable preemption");
      return;
    }

    bool intsEnabled = ml_set_interrupts_enabled(false);

    do {
      processor_t proc = _pmCallbacks.LCPUtoProcessor(cpu);
      if (proc == nullptr) {
        break;
      }

      _pmCallbacks.ThreadBind(proc);
    } while (false);

    IOSimpleLockUnlock(_preemptionLock);
    ml_set_interrupts_enabled(intsEnabled);
    HVDBGLOG("Changed to cpu %u", cpu_number());
  }

  //
  // We should now be on the correct CPU, so send EOM.
  //
  wrmsr64(kHyperVMsrEom, 0);
#else
  //
  // PM kext API does not exist on 10.4, call function on all CPUs.
  //
  mp_rendezvous_no_intrs(doAllCpuSyncICEOM, &cpu);
#endif
}
```

Why this deadlocks on multi-vCPU VMs during `scanVMBus()`:
1. During `scanVMBus()`, Hyper-V queues ~10 `ChannelOffer` messages plus `RequestChannelsDone` back-to-back on **vCPU 0** (`targetProcessor = 0`), setting `flags.messagePending = 1` in vCPU 0's SynIC `SIMP` message slot (`kVMBusInterruptMessage`).
2. `HyperVController::handleInterrupt` on vCPU 0 triggers `IOInterruptEventSource::interruptOccurred`, which wakes the `HyperVVMBus` `IOWorkLoop` thread to run `HyperVVMBus::processIncomingVMBusMessage(0)`.
3. On a 4-vCPU VM, the `IOWorkLoop` thread frequently executes on **vCPU 1, 2, or 3** (`cpu_number() != 0`), especially once `registerVMBusDevice()` kicks off child driver matching threads on vCPU 0.
4. When `sendSynICEOM(0)` is called from `cpu_number() != 0` with `flags.messagePending == 1`:
   - It calls `_pmCallbacks.ThreadBind(proc)` (`thread_bind` in XNU `osfmk/kern/sched_prim.c`) while interrupts are disabled (`ml_set_interrupts_enabled(false)`), and **never calls `thread_block(THREAD_CONTINUE_NULL)`**.
   - As documented in XNU `osfmk/kern/sched_prim.c` (lines 1768–1772):
     ```c
     /*
      * thread_bind:
      * Force the current thread to execute on the specified processor.
      * Takes effect after the next thread_block().
      */
     ```
   - Because `thread_block` is never called, the thread **does not migrate to vCPU 0**! It executes `wrmsr64(kHyperVMsrEom, 0)` (`0f 30` with `ecx = 0x40000084`) on **vCPU 1/2/3** instead of **vCPU 0**!
   - Furthermore, if `IOSimpleLockTryLock(_preemptionLock)` fails, it returns early without writing `kHyperVMsrEom` at all.
5. Because `HV_X64_MSR_EOM` (`0x40000084`) is a per-vCPU MSR, **vCPU 0 never receives the EOM write**. Hyper-V sees `messagePending == 1` still unacknowledged on vCPU 0 and never delivers the remaining `ChannelOffer`s or `kVMBusChannelMessageTypeRequestChannelsDone`.
6. `HyperVVMBus::scanVMBus()` sleeps forever in `_cmdGate->commandSleep(&_cmdGateEvent)`, leaving `VMBS` permanently in state `(a, 4020001)`.

---

## 4. Additional Issues Identified for Next Session

1. **Fix `HyperVController::sendSynICEOM` in `MacHyperVSupportMonterey.kext` (or via OpenCore `Kernel -> Patch`)**:
   - In `MacHyperVSupportMonterey` (`0x14500..0x145d0`), `sendSynICEOM` should either:
     - Call `mp_rendezvous_no_intrs(doAllCpuSyncICEOM, &cpu)` (just like the 10.4 path, which guarantees `wrmsr64(kHyperVMsrEom, 0)` runs on the exact target `cpu`), OR
     - Perform `wrmsr64(kHyperVMsrEom, 0)` directly inside `HyperVController::handleInterrupt` on `cpu_number()` after copying the 256-byte `HyperVMessage` into a per-CPU ring/buffer before waking the `IOWorkLoop`, OR
     - Call `thread_block(THREAD_CONTINUE_NULL)` after `ThreadBind(proc)` (with interrupts enabled) and unbind (`ThreadBind(PROCESSOR_NULL)`) afterward.
   - As an immediate workaround/verification, booting with `cpuCount = 1` (or patching `sendSynICEOM` in `OpenCoreConfiguration.cs`) prevents `cpu != cpu_number()`.
2. **Enforce `Set-VMProcessor -HwThreadCountPerCore 1` in `CreateVm.ps1`**:
   - On Intel Hybrid CPUs (like the reference i5-13420H), exposing asymmetric logical-to-physical ratios to macOS can trigger `libsystem_malloc` fatal assertion `logical_ncpus % phys_ncpus != 0` (`OSX-Hyper-V` issue #85). Adding `-HwThreadCountPerCore 1` in `CreateVm.ps1` prevents this.
3. **Initialize `macOS.vhdx` with GPT + exFAT in `CreateVm.ps1`**:
   - Upstream `OSX-Hyper-V` commit `05a6922e` (`Initialize macOS install volume for diskutil detection`) initializes `macOS.vhdx` as GPT + exFAT (`Label = "macOS"`) during VM creation so that the macOS Recovery installer and Disk Utility immediately display the target disk.
4. **Upgrade `MacHyperVSupport` kexts to `1.0.0`**:
   - `OSX-Hyper-V 0.4.0` bundles pre-release `MacHyperVSupport 0.9.10`, which Vitaly Cheptsov (`vit9696`) replaced in commit `c8a99f7f` (`Use 1.0.0 version as some software may not play well with 0.9.10`).
