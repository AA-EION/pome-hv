"""Generate/apply the SynIC source fix and compile its actual method with CPU doubles.
This does not build a macOS kext or establish a successful guest boot.
"""
from pathlib import Path
import difflib
import subprocess
import uuid

root = Path(__file__).resolve().parents[2]
relative = 'MacHyperVSupport/Controller/HyperVControllerInterrupts.cpp'
old = (root / '.research/MacHyperVSupport' / relative).read_text()
method = '''void HyperVController::sendSynICEOM(UInt32 cpu) {
  if (_cpuData == nullptr || cpu >= _cpuDataCount) {
    HVSYSLOG("Invalid SynIC CPU index %u", cpu);
    return;
  }
  _cpuData[cpu].messages[kVMBusInterruptMessage].type = kHyperVMessageTypeNone;
  OSMemoryBarrier();
  if (!_cpuData[cpu].messages[kVMBusInterruptMessage].flags.messagePending) {
    return;
  }
  // Binding a work-loop thread does not immediately migrate it. The EOM MSR
  // is per-CPU: synchronously acknowledge only its owner. No IOKit lock is
  // held by this method while the rendezvous runs; its callback never blocks.
  mp_rendezvous_no_intrs(doAllCpuSyncICEOM, &cpu);
}
'''
callback = '''extern "C" void doAllCpuSyncICEOM(void *cpu) {
  UInt32 desiredCpu = *static_cast<UInt32*>(cpu);
  if (static_cast<UInt32>(cpu_number()) == desiredCpu) {
    wrmsr64(kHyperVMsrEom, 0);
  }
}'''
start = old.index('void HyperVController::sendSynICEOM(UInt32 cpu) {')
new = old[:start] + method
new = new.replace('#include "VMBus.hpp"', '#include "VMBus.hpp"\n#include <libkern/OSAtomic.h>')
start = new.index('#if __MAC_OS_X_VERSION_MIN_REQUIRED < __MAC_10_6\nextern "C" void doAllCpuSyncICEOM')
end = new.index('\n#endif', start) + len('\n#endif')
new = new[:start] + callback + new[end:]
patch = ''.join(difflib.unified_diff(old.splitlines(True), new.splitlines(True), fromfile='a/'+relative, tofile='b/'+relative))
patch_path = root / 'patches/MacHyperVSupport/0005-synic-eom-owner-cpu.patch'
patch_path.write_text(patch)
work = root / 'artifacts' / ('synic-test-' + uuid.uuid4().hex)
target = work / relative
target.parent.mkdir(parents=True)
target.write_text(old)
command = ['git', 'apply', '--directory='+work.relative_to(root).as_posix(), str(patch_path)]
subprocess.run(command[:2]+['--check']+command[2:], cwd=root, check=True)
subprocess.run(command, cwd=root, check=True)
applied = target.read_text()
assert applied == new
actual_method = applied[applied.index('void HyperVController::sendSynICEOM(UInt32 cpu) {'):]
actual_callback = applied[applied.index('extern "C" void doAllCpuSyncICEOM'):applied.index('\nbool HyperVController::allocateInterruptBuffers()')]
harness = r'''
#include <cstdint>
#include <cassert>
#include <vector>
#include <atomic>
#include <iostream>
using UInt32 = uint32_t;
constexpr int kVMBusInterruptMessage=2, kHyperVMessageTypeNone=0;
constexpr uint32_t kHyperVMsrEom=0x40000084;
int currentCpu=0, barriers=0, rendezvous=0;
std::vector<int> acknowledgements;
int cpu_number() { return currentCpu; }
void wrmsr64(uint32_t msr, uint64_t value) { assert(msr==kHyperVMsrEom && value==0); acknowledgements.push_back(currentCpu); }
void OSMemoryBarrier() { ++barriers; std::atomic_thread_fence(std::memory_order_seq_cst); }
#define HVSYSLOG(...) ((void)0)
struct Message { int type=1; struct { bool messagePending=false; } flags; };
struct CpuData { Message messages[3]; };
class HyperVController { public: CpuData* _cpuData; UInt32 _cpuDataCount; void sendSynICEOM(UInt32); };
void mp_rendezvous_no_intrs(void (*action)(void*), void* arg) {
  assert(barriers>0); ++rendezvous; int original=currentCpu;
  for(currentCpu=0;currentCpu<4;++currentCpu) action(arg);
  currentCpu=original;
}
'''
harness += actual_callback + '\n' + actual_method
harness += r'''
int main() {
  CpuData cpus[4]; HyperVController controller{cpus,4}; int cases=0;
  for(int origin=0;origin<4;++origin) for(UInt32 owner=0;owner<4;++owner) for(bool pending: {false,true}) {
    acknowledgements.clear(); rendezvous=barriers=0; currentCpu=origin;
    cpus[owner].messages[2].type=1; cpus[owner].messages[2].flags.messagePending=pending;
    controller.sendSynICEOM(owner);
    assert(cpus[owner].messages[2].type==0 && barriers==1 && currentCpu==origin);
    assert(rendezvous==(pending?1:0) && acknowledgements.size()==(pending?1u:0u));
    if(pending) assert(acknowledgements[0]==static_cast<int>(owner));
    ++cases;
  }
  acknowledgements.clear(); controller.sendSynICEOM(4); controller.sendSynICEOM(UINT32_MAX);
  controller._cpuData=nullptr; controller.sendSynICEOM(0); assert(acknowledgements.empty());
  std::cout << cases+3 << " SynIC source scenarios passed; no real MSRs or guest executed\n";
}
'''
(work / 'synic-test.cpp').write_text(harness)
subprocess.run(['cl.exe', '/nologo', '/EHsc', '/std:c++20', '/Fe:synic-test.exe', 'synic-test.cpp'], cwd=work, check=True)
subprocess.run([str(work / 'synic-test.exe')], cwd=work, check=True)
