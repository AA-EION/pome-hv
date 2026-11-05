"""Read-only inspection of the pinned x86-64 Mach-O guest kext; never executes it."""
import hashlib
import pathlib
import struct
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.tools/boot-analysis'))

def inspect(path):
    data = pathlib.Path(path).read_bytes()
    if struct.unpack_from('<I', data)[0] != 0xfeedfacf:
        raise ValueError('Expected thin little-endian Mach-O 64')
    symbols, sections, segments = [], [], []
    cursor = 32
    for _ in range(struct.unpack_from('<I', data, 16)[0]):
        cmd, size = struct.unpack_from('<II', data, cursor)
        if cmd == 2:
            symoff, nsyms, stroff, strsize = struct.unpack_from('<IIII', data, cursor + 8)
        elif cmd == 0xb:
            indirectoff, indirectcount = struct.unpack_from('<II', data, cursor + 56)
        elif cmd == 0x19:
            vm, vmsize, off, length = struct.unpack_from('<QQQQ', data, cursor + 24)
            segments.append((vm, vmsize, off, length))
            for i in range(struct.unpack_from('<I', data, cursor + 64)[0]):
                s = cursor + 72 + i * 80
                name = data[s:s+16].split(b'\0')[0].decode()
                address, length, off = struct.unpack_from('<QQI', data, s + 32)
                flags, first, stride = struct.unpack_from('<III', data, s + 64)
                sections.append((name, address, length, off, flags, first, stride))
        cursor += size
    for i in range(nsyms):
        name, kind, section, desc, value = struct.unpack_from('<IBBHQ', data, symoff + i * 16)
        end = data.index(b'\0', stroff + name)
        symbols.append((data[stroff+name:end].decode(), value, kind))
    stubs = {}
    for name, addr, length, off, flags, first, stride in sections:
        if flags & 0xff == 8:
            for i in range(length // stride):
                index = struct.unpack_from('<I', data, indirectoff + (first+i)*4)[0]
                if index < len(symbols):
                    stubs[symbols[index][0]] = addr + i * stride
    return data, symbols, segments, stubs

if __name__ == '__main__':
    data, symbols, segments, stubs = inspect(sys.argv[1])
    print('SHA256', hashlib.sha256(data).hexdigest())
    for name, value, kind in symbols:
        if 'sendSynICEOM' not in name:
            continue
        next_addr = min(v for _, v, _ in symbols if v > value)
        vm, size, off, length = next(s for s in segments if s[0] <= value < s[0]+s[3])
        code = data[off+value-vm:off+next_addr-vm]
        print(name, hex(value), len(code), code.hex())
        from capstone import Cs, CS_ARCH_X86, CS_MODE_64
        for ins in Cs(CS_ARCH_X86, CS_MODE_64).disasm(code, value):
            print(hex(ins.address), ins.mnemonic, ins.op_str)
    print({k: hex(v) for k, v in stubs.items() if any(x in k for x in ['cpu_number', 'rendezvous', 'ml_set_interrupts', 'thread_block'])})
