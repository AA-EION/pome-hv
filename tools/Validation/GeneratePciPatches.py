"""Generate review-only patches against the audited source. Does not modify the checkout."""
import difflib
import pathlib
import subprocess

root = pathlib.Path(__file__).resolve().parents[2]
checkout = root / '.research/MacHyperVSupport'
commit = 'cafcd34daea5e58633112a1e63cef1f07bbc8404'
assert subprocess.check_output(['git', '-C', str(checkout), 'rev-parse', 'HEAD'], text=True).strip() == commit
names = ['MacHyperVSupport/PCIBridge/HyperVPCIBridge.cpp', 'MacHyperVSupport/PCIBridge/HyperVPCIBridgePrivate.cpp', 'MacHyperVSupport/PCIBridge/HyperVPCIBridge.hpp']
original = {name: (checkout / name).read_text(encoding='utf-8') for name in names}

def replace(text, before, after):
    assert text.count(before) == 1, before
    return text.replace(before, after)

def write_patch(number, before, after):
    directory = root / 'patches/MacHyperVSupport'
    directory.mkdir(parents=True, exist_ok=True)
    patch = ''.join(''.join(difflib.unified_diff(before[name].splitlines(True), after[name].splitlines(True), fromfile='a/' + name, tofile='b/' + name)) for name in names)
    (directory / number).write_text(patch, encoding='utf-8', newline='\n')

corrected = original.copy()
name = names[0]
corrected[name] = replace(corrected[name], '(UInt32) (pciStatus.address << 32)', '(UInt32) (pciStatus.address >> 32)')
name = names[1]
corrected[name] = replace(corrected[name], '    if (isBar64Bit) {\n      barVal |=', '''    if (isBar64Bit && i + 1 >= kHyperVPCIBarCount) {
      HVSYSLOG("Truncated 64-bit BAR at index %u", i);
      status = kIOReturnBadArgument;
      break;
    }
    if (isBar64Bit) {
      barVal |=''')
corrected[name] = replace(corrected[name], '  HVDBGLOG("Got resource requirements with status %u", pktReqResponse.status);', '''  HVDBGLOG("Got resource requirements with status %u", pktReqResponse.status);
  if (pktReqResponse.status != 0) {
    return kIOReturnIOError;
  }''')
corrected[name] = replace(corrected[name], '  HVDBGLOG("PCI status %X", pciStatus);\n  \n  return status;', '  HVDBGLOG("PCI status %X", pciStatus);\n\n  return pciStatus == 0 ? kIOReturnSuccess : kIOReturnIOError;')
write_patch('0001-pci-response-and-msi-correctness.patch', original, corrected)

experimental = corrected.copy()
name = names[2]
experimental[name] = replace(experimental[name], '  UInt64              _barSizes[kHyperVPCIBarCount] = { };', '  UInt64              _barSizes[kHyperVPCIBarCount] = { };\n  bool                _barIs64Bit[kHyperVPCIBarCount] = { };')
experimental[name] = replace(experimental[name], '''  inline UInt64 getBarSize(UInt64 barValue) {
    if (barValue < UINT32_MAX) {
      barValue |= 0xFFFFFFFF00000000;
    }
    return roundup((1 + ~(barValue & kHyperVPCIBarMemoryMask)), PAGE_SIZE);
  }''', '''  inline UInt64 getBarSize(UInt64 barValue, bool is64Bit) {
    if (!is64Bit) {
      barValue |= 0xFFFFFFFF00000000ULL;
    }
    UInt64 size = 1 + ~(barValue & ~0x0FULL);
    if (size == 0 || (size & (size - 1)) != 0) {
      return 0;
    }
    return size < PAGE_SIZE ? PAGE_SIZE : size;
  }''')
name = names[1]
experimental[name] = replace(experimental[name], '  memset(_barSizes, 0, sizeof (_barSizes));', '  memset(_barSizes, 0, sizeof (_barSizes));\n  memset(_barIs64Bit, 0, sizeof (_barIs64Bit));')
experimental[name] = replace(experimental[name], '_barSizes[i] = getBarSize(barVal);', '_barSizes[i] = getBarSize(barVal, isBar64Bit);')
experimental[name] = replace(experimental[name], '''    _bars[i] = _hvPCIRoot->allocateRange(_barSizes[i], _barSizes[i], UINT32_MAX);''', '''    _barIs64Bit[i] = isBar64Bit;
    // Research opt-in: only true 64-bit BARs may use the ACPI high aperture.
    // The config window and all 32-bit BARs stay below 4 GiB.
    IORangeScalar maxAddress = UINT32_MAX;
#if defined(__x86_64__)
    if (isBar64Bit && checkKernelArgument("-hvpcilargemmio")) {
      maxAddress = UINT64_MAX;
    }
#endif
    if (_barSizes[i] == 0 || (_barSizes[i] & (_barSizes[i] - 1)) != 0 ||
        _barSizes[i] > maxAddress) {
      HVSYSLOG("BAR%u size is invalid or exceeds the selected aperture", i);
      status = kIOReturnNoResources;
      break;
    }
    _bars[i] = _hvPCIRoot->allocateRange(_barSizes[i], _barSizes[i], maxAddress);
    if (_bars[i] != 0 && (_bars[i] > maxAddress ||
        _barSizes[i] - 1 > maxAddress - _bars[i])) {
      _hvPCIRoot->freeRange(_bars[i], _barSizes[i]);
      _bars[i] = 0;
    }''')
name = names[0]
experimental[name] = replace(experimental[name], '    if (_bars[i] > UINT32_MAX) {', '    if (_barIs64Bit[i]) {')
experimental[name] = replace(experimental[name], '    HVDBGLOG("Adding memory range 0x%llX of %llu bytes for BAR%u - %u", _bars[i], _barSizes[i], i, result);', '''    HVDBGLOG("Adding memory range 0x%llX of %llu bytes for BAR%u - %u", _bars[i], _barSizes[i], i, result);
    if (!result) {
      HVSYSLOG("macOS rejected BAR%u bridge aperture", i);
      return false;
    }''')
write_patch('0002-opt-in-high-mmio-research.patch', corrected, experimental)
print('Generated two source-only patches. Neither is compiled, installed or hardware-validated.')
