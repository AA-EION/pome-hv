"""Generate review-only patches against the audited source. Does not modify the checkout."""
import difflib
import pathlib
import subprocess

root = pathlib.Path(__file__).resolve().parents[2]
checkout = root / '.research/MacHyperVSupport'
commit = 'cafcd34daea5e58633112a1e63cef1f07bbc8404'
assert subprocess.check_output(['git', '-C', str(checkout), 'rev-parse', 'HEAD'], text=True).strip() == commit
names = [
    'MacHyperVSupport/PCIBridge/HyperVPCIBridge.cpp',
    'MacHyperVSupport/PCIBridge/HyperVPCIBridgePrivate.cpp',
    'MacHyperVSupport/PCIBridge/HyperVPCIBridge.hpp',
    'MacHyperVSupport/PCIRoot/HyperVPCIRoot.cpp',
]
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

transactional = experimental.copy()
name = names[1]
transactional[name] = replace(transactional[name], '  //\n  // Allocate and set BARs for PCI device.\n', """  // Refuse to reprogram an active decoder or bus master. Teardown/quiescence
  // needs a separate host protocol implementation, not speculative range reuse.
  if (readPCIConfig(kIOPCIConfigCommand, sizeof (UInt16)) & 0x0006) {
    return kIOReturnBusy;
  }

  //
  // Reserve every BAR before publishing any address to the device.
""")
transactional[name] = replace(transactional[name], '    bool isBar64Bit = barVal & kHyperVPCIBarMemoryType64Bit;', """    UInt32 memoryType = (UInt32)barVal & 0x6;
    if (memoryType != 0 && memoryType != kHyperVPCIBarMemoryType64Bit) {
      status = kIOReturnUnsupported;
      break;
    }
    bool isBar64Bit = memoryType == kHyperVPCIBarMemoryType64Bit;""")
start = transactional[name].index('    //\n    // Write new BAR to PCI device.')
end = transactional[name].index('  //\n  // Enable memory I/O on device.', start)
transactional[name] = transactional[name][:start] + """    if (isBar64Bit) {
      i++;
    }
  }

  if (status != kIOReturnSuccess) {
    // No BAR has been written and decoding was inactive. These reservations
    // are private to this failed query and can be safely returned to the root.
    for (int i = 0; i < kHyperVPCIBarCount; i++) {
      if (_bars[i] != 0) {
        _hvPCIRoot->freeRange(_bars[i], _barSizes[i]);
      }
    }
    memset(_bars, 0, sizeof (_bars));
    memset(_barSizes, 0, sizeof (_barSizes));
    memset(_barIs64Bit, 0, sizeof (_barIs64Bit));
    return status;
  }

  for (int i = 0; i < kHyperVPCIBarCount; i++) {
    if (_bars[i] == 0) {
      continue;
    }
    writePCIConfig(kIOPCIConfigBaseAddress0 + (i * sizeof (UInt32)), sizeof (UInt32), (UInt32)_bars[i]);
    if (_barIs64Bit[i]) {
      writePCIConfig(kIOPCIConfigBaseAddress0 + ((i + 1) * sizeof (UInt32)), sizeof (UInt32), (UInt32)(_bars[i] >> 32));
      i++;
    }
  }

""" + transactional[name][end:]
write_patch('0003-reserve-bars-before-programming.patch', experimental, transactional)

hardened = transactional.copy()
name = names[3]
hardened[name] = replace(hardened[name], '''        //
        // Add to appropriate range allocator.
        //
        if (acpiRanges[i].minAddressRange > UINT32_MAX) {
          _rangeAllocatorHigh->deallocate(static_cast<IOPhysicalAddress>(acpiRanges[i].minAddressRange),
                                          static_cast<IOPhysicalLength>(acpiRanges[i].addressLength));
        } else {
          _rangeAllocatorLow->deallocate(static_cast<IOPhysicalAddress>(acpiRanges[i].minAddressRange),
                                         static_cast<IOPhysicalLength>(acpiRanges[i].addressLength));
        }''', '''        //
        // Validate ACPI aperture bounds, overflow, 4 GiB boundary crossing and overlaps
        // before admitting the range into the low or high allocator.
        //
        UInt64 minAddr = acpiRanges[i].minAddressRange;
        UInt64 lenAddr = acpiRanges[i].addressLength;
        if (minAddr == 0 || lenAddr == 0 || lenAddr - 1 > UINT64_MAX - minAddr) {
          HVSYSLOG("Skipping malformed ACPI memory range %d", i);
          continue;
        }
        UInt64 endAddr = minAddr + (lenAddr - 1);
        if (acpiRanges[i].maxAddressRange != 0 && acpiRanges[i].maxAddressRange < endAddr) {
          HVSYSLOG("Skipping inconsistent ACPI memory range %d", i);
          continue;
        }
        if (minAddr <= UINT32_MAX && endAddr > UINT32_MAX) {
          HVSYSLOG("Skipping ACPI memory range %d crossing the 4 GiB boundary", i);
          continue;
        }
        bool overlaps = false;
        for (int j = 0; j < i; j++) {
          if (acpiRanges[j].resourceType != kIOACPIMemoryRange || acpiRanges[j].addressLength == 0 ||
              acpiRanges[j].addressLength - 1 > UINT64_MAX - acpiRanges[j].minAddressRange) {
            continue;
          }
          UInt64 prevMin = acpiRanges[j].minAddressRange;
          UInt64 prevEnd = prevMin + (acpiRanges[j].addressLength - 1);
          if (minAddr <= prevEnd && prevMin <= endAddr) {
            overlaps = true;
            break;
          }
        }
        if (overlaps) {
          HVSYSLOG("Skipping overlapping ACPI memory range %d", i);
          continue;
        }
        if (minAddr > UINT32_MAX) {
          _rangeAllocatorHigh->deallocate(static_cast<IOPhysicalAddress>(minAddr),
                                          static_cast<IOPhysicalLength>(lenAddr));
        } else {
          _rangeAllocatorLow->deallocate(static_cast<IOPhysicalAddress>(minAddr),
                                         static_cast<IOPhysicalLength>(lenAddr));
        }''')
hardened[name] = replace(hardened[name], '''  if (maxAddress > UINT32_MAX) {
    result = _rangeAllocatorHigh->allocate(size, &range, alignment);
  }
  if (!result) {
    result = _rangeAllocatorLow->allocate(size, &range, alignment);
  }''', '''  IOInterruptState ints = IOSimpleLockLockDisableInterrupt(_pciLock);
  if (maxAddress > UINT32_MAX) {
    result = _rangeAllocatorHigh->allocate(size, &range, alignment);
  }
  if (!result) {
    result = _rangeAllocatorLow->allocate(size, &range, alignment);
  }
  IOSimpleLockUnlockEnableInterrupt(_pciLock, ints);''')
hardened[name] = replace(hardened[name], '''  if (start > UINT32_MAX) {
    _rangeAllocatorHigh->deallocate(start, size);
  } else {
    _rangeAllocatorLow->deallocate(start, size);
  }''', '''  IOInterruptState ints = IOSimpleLockLockDisableInterrupt(_pciLock);
  if (start > UINT32_MAX) {
    _rangeAllocatorHigh->deallocate(start, size);
  } else {
    _rangeAllocatorLow->deallocate(start, size);
  }
  IOSimpleLockUnlockEnableInterrupt(_pciLock, ints);''')
hardened[name] = replace(hardened[name], '''  if (_pciLock != nullptr) {
    IOSimpleLockFree(_pciLock);
  }''', '''  OSSafeReleaseNULL(_rangeAllocatorLow);
  OSSafeReleaseNULL(_rangeAllocatorHigh);
  if (_pciLock != nullptr) {
    IOSimpleLockFree(_pciLock);
    _pciLock = nullptr;
  }''')
name = names[2]
hardened[name] = replace(hardened[name], '  bool start(IOService *provider) APPLE_KEXT_OVERRIDE;', '  bool start(IOService *provider) APPLE_KEXT_OVERRIDE;\n  void stop(IOService *provider) APPLE_KEXT_OVERRIDE;')
name = names[0]
hardened[name] = replace(hardened[name], '''  if (!result) {
    stop(provider);
  }
  return result;
}''', '''  if (!result) {
    stop(provider);
  }
  return result;
}

void HyperVPCIBridge::stop(IOService *provider) {
  HVDBGLOG("Stopping Hyper-V PCI Bridge");
  if (_pciConfigMemoryMap != nullptr && _hvPCIRoot != nullptr) {
    UInt16 cmd = (UInt16)readPCIConfig(kIOPCIConfigCommand, sizeof (UInt16));
    if (cmd & 0x0006) {
      writePCIConfig(kIOPCIConfigCommand, sizeof (UInt16), cmd & ~0x0006U);
    }
  }
  if (_hvPCIRoot != nullptr) {
    for (int i = 0; i < kHyperVPCIBarCount; i++) {
      if (_bars[i] != 0) {
        _hvPCIRoot->freeRange(_bars[i], _barSizes[i]);
        _bars[i] = 0;
        _barSizes[i] = 0;
        _barIs64Bit[i] = false;
      }
    }
  }
  OSSafeReleaseNULL(_pciConfigMemoryMap);
  OSSafeReleaseNULL(_pciConfigMemoryDescriptor);
  if (_pciConfigSpace != 0 && _hvPCIRoot != nullptr) {
    _hvPCIRoot->freeRange(_pciConfigSpace, kHyperVPCIBridgeWindowSize);
    _pciConfigSpace = 0;
  }
  if (_pciFunctions != nullptr) {
    IOFree(_pciFunctions, _pciFunctionsCount * sizeof (HyperVPCIFunctionDescription));
    _pciFunctions = nullptr;
    _pciFunctionsCount = 0;
  }
  if (_pciLock != nullptr) {
    IOSimpleLockFree(_pciLock);
    _pciLock = nullptr;
  }
  OSSafeReleaseNULL(_hvDevice);
  super::stop(provider);
}''')
name = names[1]
hardened[name] = replace(hardened[name], '''  for (int i = 0; i < kHyperVPCIBarCount; i++) {
    if (_bars[i] == 0) {
      continue;
    }
    writePCIConfig(kIOPCIConfigBaseAddress0 + (i * sizeof (UInt32)), sizeof (UInt32), (UInt32)_bars[i]);
    if (_barIs64Bit[i]) {
      writePCIConfig(kIOPCIConfigBaseAddress0 + ((i + 1) * sizeof (UInt32)), sizeof (UInt32), (UInt32)(_bars[i] >> 32));
      i++;
    }
  }''', '''  for (int i = 0; i < kHyperVPCIBarCount; i++) {
    if (_bars[i] == 0) {
      continue;
    }
    UInt32 lowFlags = pktReqResponse.probedBARs[i] & 0x0FU;
    UInt32 expectedLow = ((UInt32)_bars[i] & ~0x0FU) | lowFlags;
    writePCIConfig(kIOPCIConfigBaseAddress0 + (i * sizeof (UInt32)), sizeof (UInt32), expectedLow);
    UInt32 actualLow = readPCIConfig(kIOPCIConfigBaseAddress0 + (i * sizeof (UInt32)), sizeof (UInt32));
    if ((actualLow & ~0x0FU) != (expectedLow & ~0x0FU) || (actualLow & 0x07U) != (lowFlags & 0x07U)) {
      status = kIOReturnIOError;
      break;
    }
    if (_barIs64Bit[i]) {
      UInt32 expectedHigh = (UInt32)(_bars[i] >> 32);
      writePCIConfig(kIOPCIConfigBaseAddress0 + ((i + 1) * sizeof (UInt32)), sizeof (UInt32), expectedHigh);
      UInt32 actualHigh = readPCIConfig(kIOPCIConfigBaseAddress0 + ((i + 1) * sizeof (UInt32)), sizeof (UInt32));
      if (actualHigh != expectedHigh) {
        status = kIOReturnIOError;
        break;
      }
      i++;
    }
  }

  if (status != kIOReturnSuccess) {
    for (int i = 0; i < kHyperVPCIBarCount; i++) {
      if (_bars[i] != 0) {
        writePCIConfig(kIOPCIConfigBaseAddress0 + (i * sizeof (UInt32)), sizeof (UInt32), 0);
        if (_barIs64Bit[i]) {
          writePCIConfig(kIOPCIConfigBaseAddress0 + ((i + 1) * sizeof (UInt32)), sizeof (UInt32), 0);
        }
        _hvPCIRoot->freeRange(_bars[i], _barSizes[i]);
      }
    }
    memset(_bars, 0, sizeof (_bars));
    memset(_barSizes, 0, sizeof (_barSizes));
    memset(_barIs64Bit, 0, sizeof (_barIs64Bit));
    return status;
  }''')
write_patch('0004-acpi-admission-locking-and-teardown.patch', transactional, hardened)
print('Generated four source-only patches. No kext is compiled, installed or hardware-validated.')
