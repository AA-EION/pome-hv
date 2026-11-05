"""Apply the series to fresh copies, compile the actual patched allocation method
against fake IOKit/VMBus boundaries, and fault-inject every BAR allocation. This
does NOT build a kext or validate macOS, DMA, host grants or device teardown.
Run from the repository root in a Visual Studio x64 developer environment.
"""
import pathlib
import shutil
import subprocess
import uuid

root = pathlib.Path(__file__).resolve().parents[2]
subprocess.run(['python', str(root / 'tools/Validation/GeneratePciPatches.py')], check=True)
work = root / 'artifacts' / ('pci-test-' + uuid.uuid4().hex)
source = 'MacHyperVSupport/PCIBridge/HyperVPCIBridgePrivate.cpp'
header = 'MacHyperVSupport/PCIBridge/HyperVPCIBridge.hpp'
for name in [source, header, 'MacHyperVSupport/PCIBridge/HyperVPCIBridge.cpp', 'MacHyperVSupport/PCIRoot/HyperVPCIRoot.cpp', 'MacHyperVSupport/Controller/HyperVControllerInterrupts.cpp']:
    dest = work / name
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(root / '.research/MacHyperVSupport' / name, dest)
for patch in sorted((root / 'patches/MacHyperVSupport').glob('*.patch')):
    command = ['git', 'apply', '--directory=' + work.relative_to(root).as_posix(), str(patch)]
    subprocess.run(command[:2] + ['--check'] + command[2:], cwd=root, check=True)
    subprocess.run(command, cwd=root, check=True)

text = (work / source).read_text()
method = text[text.index('IOReturn HyperVPCIBridge::queryResourceRequirements()'):text.index('IOReturn HyperVPCIBridge::sendResourcesAssigned')]
text = (work / header).read_text()
size_method = text[text.index('  inline UInt64 getBarSize'):text.index('\n  //\n  // Device properties')]
harness = r'''
#include <cstdint>
#include <cstring>
#include <cassert>
#include <vector>
#include <tuple>
#include <map>
#include <iostream>
using UInt64=uint64_t; using UInt32=uint32_t; using UInt16=uint16_t;
using IORangeScalar=uint64_t; using IOReturn=int;
constexpr int kIOReturnSuccess=0,kIOReturnBadArgument=1,kIOReturnNoResources=2,kIOReturnIOError=3,kIOReturnUnsupported=4,kIOReturnBusy=5;
constexpr int kHyperVPCIBarCount=6, PAGE_SIZE=4096;
constexpr UInt32 kHyperVPCIBarSpaceIO=1,kHyperVPCIBarMemoryType64Bit=4;
constexpr UInt32 kIOPCIConfigCommand=4,kIOPCIConfigBaseAddress0=16,kIOPCICommandMemorySpace=2;
constexpr int kHyperVPCIBridgeMessageTypeQueryResourceRequirements=1;
#define HVDBGLOG(...) ((void)0)
#define HVSYSLOG(...) ((void)0)
struct HyperVPCIBridgeChildMessage { struct { int type; } header; struct { int slot; } slot; };
struct HyperVPCIBridgeQueryResourceRequirementsResponse { int status=0; UInt32 probedBARs[6]={}; };
struct Device {
  HyperVPCIBridgeQueryResourceRequirementsResponse response; int transport=0;
  int writeInbandPacket(void*,size_t,bool,void* out,size_t) { std::memcpy(out,&response,sizeof(response)); return transport; }
};
struct Root {
  int calls=0,failAt=-1; bool high=false; bool malformed=false;
  std::vector<std::pair<UInt64,UInt64>> allocated,freed;
  UInt64 allocateRange(UInt64 size,UInt64 alignment,UInt64 max) {
    assert(size==alignment); int index=calls++; if(index==failAt)return 0;
    UInt64 address=malformed ? 0xfffff800ULL : (max==UINT64_MAX ? 0x200000000ULL : 0xc0000000ULL)+index*0x1000000ULL;
    high=max==UINT64_MAX; allocated.emplace_back(address,size); return address;
  }
  void freeRange(UInt64 start,UInt64 size) { freed.emplace_back(start,size); }
};
class HyperVPCIBridge { public:
  Device device; Root root; Device* _hvDevice=&device; Root* _hvPCIRoot=&root;
  UInt64 _bars[6]={},_barSizes[6]={}; bool _barIs64Bit[6]={};
  bool optIn=false, corruptReadback=false; UInt32 command=0;
  std::map<UInt32,UInt32> configRegs;
  std::vector<std::tuple<UInt32,UInt32,int>> writes;
  bool checkKernelArgument(const char*) {return optIn;}
  UInt32 readPCIConfig(UInt32 offset,size_t) {
    if (offset==kIOPCIConfigCommand) return command;
    if (corruptReadback) return 0xdeadbeefU;
    auto it = configRegs.find(offset);
    return it != configRegs.end() ? it->second : 0;
  }
  void writePCIConfig(UInt32 offset,size_t,UInt32 value) {
    configRegs[offset] = value;
    writes.emplace_back(offset,value,root.calls);
  }
  IOReturn queryResourceRequirements();
''' + size_method + '\n};\n' + method + r'''
int main() {
  for(int fail=0;fail<6;fail++) {
    HyperVPCIBridge b; for(auto& bar:b.device.response.probedBARs)bar=0xfffff000;
    b.root.failAt=fail; assert(b.queryResourceRequirements()==kIOReturnNoResources);
    assert(b.writes.empty()); assert(b.root.freed==b.root.allocated);
    for(auto bar:b._bars)assert(bar==0);
  }
  { HyperVPCIBridge b; for(auto& bar:b.device.response.probedBARs)bar=0xfffff000;
    assert(b.queryResourceRequirements()==0); assert(b.writes.size()==7);
    for(auto write:b.writes)assert(std::get<2>(write)==6); }
  { HyperVPCIBridge b; b.optIn=true; b.device.response.probedBARs[0]=0xc; b.device.response.probedBARs[1]=0xfffffffe;
    assert(b.queryResourceRequirements()==0); assert(b.root.high); assert(b._barSizes[0]==0x200000000ULL);
    assert((std::get<1>(b.writes[0]) & 0x0fU)==0xcU);
    assert(std::get<1>(b.writes[1])==2); }
  { HyperVPCIBridge b; b.device.response.probedBARs[0]=4; b.device.response.probedBARs[1]=0xfffffffe;
    assert(b.queryResourceRequirements()==kIOReturnNoResources); assert(b.writes.empty()); }
  { HyperVPCIBridge b; b.device.response.probedBARs[0]=0xfffff004; b.device.response.probedBARs[1]=0xffffffff;
    assert(b.queryResourceRequirements()==0); assert(!b.root.high); assert(b._barIs64Bit[0]); assert(b.writes.size()==3);
    assert((std::get<1>(b.writes[0]) & 0x0fU)==4U); }
  { HyperVPCIBridge b; b.device.response.probedBARs[5]=0xfffff004;
    assert(b.queryResourceRequirements()==kIOReturnBadArgument); assert(b.writes.empty()); }
  for(UInt32 value: {0xfffff001U,0xfffff002U,0xfffff006U}) {
    HyperVPCIBridge b; b.device.response.probedBARs[0]=0xfffff000; b.device.response.probedBARs[1]=value;
    assert(b.queryResourceRequirements()==kIOReturnUnsupported); assert(b.writes.empty()); assert(b.root.freed==b.root.allocated); }
  { HyperVPCIBridge b; b.root.malformed=true; b.device.response.probedBARs[0]=0xfffff000;
    assert(b.queryResourceRequirements()==kIOReturnNoResources); assert(b.root.freed==b.root.allocated); assert(b.writes.empty()); }
  { HyperVPCIBridge b; b.corruptReadback=true; b.device.response.probedBARs[0]=0xfffff004; b.device.response.probedBARs[1]=0xffffffff;
    assert(b.queryResourceRequirements()==kIOReturnIOError); assert(b.root.freed==b.root.allocated);
    for(auto bar:b._bars)assert(bar==0); assert(b.configRegs[kIOPCIConfigBaseAddress0]==0); }
  for(UInt32 command:{2U,4U,6U}) { HyperVPCIBridge b; b.command=command;
    assert(b.queryResourceRequirements()==kIOReturnBusy); assert(b.root.calls==0); assert(b.writes.empty()); }
  { HyperVPCIBridge b; b.device.response.status=1; assert(b.queryResourceRequirements()==kIOReturnIOError); assert(b.root.calls==0); }
  { HyperVPCIBridge b; b.device.transport=kIOReturnIOError; assert(b.queryResourceRequirements()==kIOReturnIOError); assert(b.root.calls==0); }
  std::cout << "21 allocation/fault/readback scenarios passed against extracted patched C++; fake IOKit/VMBus only.\n";
}
'''
(work / 'allocation-test.cpp').write_text(harness)
subprocess.run(['cl.exe', '/nologo', '/EHsc', '/std:c++20', '/D__x86_64__', '/Fe:allocation-test.exe', 'allocation-test.cpp'], cwd=work, check=True)
subprocess.run([str(work / 'allocation-test.exe')], cwd=work, check=True)
print('Evidence:', work)
