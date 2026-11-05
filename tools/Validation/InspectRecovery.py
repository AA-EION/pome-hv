"""Read SystemVersion/PlatformSupport from a zlib UDIF/HFS+ recovery image.

Uses only Python's standard library; never mounts or modifies the image.
Unsupported compression, fragmented catalogs and malformed structures fail closed.
"""
import argparse
import bisect
import functools
import json
import plistlib
import struct


def u16(data, offset=0):
    return struct.unpack_from('>H', data, offset)[0]


def u32(data, offset=0):
    return struct.unpack_from('>I', data, offset)[0]


class Recovery:
    def __init__(self, path):
        self.file = open(path, 'rb')
        self.file.seek(-512, 2)
        trailer = self.file.read(512)
        if trailer[:4] != b'koly':
            raise ValueError('Expected a UDIF image')
        offset, size = struct.unpack_from('>QQ', trailer, 216)
        if size > 16 * 1024 * 1024:
            raise ValueError('Oversized UDIF metadata')
        self.file.seek(offset)
        metadata = plistlib.loads(self.file.read(size))
        self.metadata = metadata
        self.sectors = struct.unpack_from('>Q', trailer, 492)[0]
        partitions = [x for x in metadata['resource-fork']['blkx'] if 'Apple_HFS' in x['Name']]
        if len(partitions) != 1:
            raise ValueError('Expected exactly one HFS partition')
        table = partitions[0]['Data']
        self.base = struct.unpack_from('>Q', table, 24)[0]
        self.chunks = []
        for i in range(u32(table, 200)):
            kind, _, sector, count, start, length = struct.unpack_from('>IIQQQQ', table, 204 + 40 * i)
            if count and kind != 0xffffffff:
                if kind not in (0, 1, 2, 0x80000005):
                    raise ValueError(f'Unsupported UDIF compression {kind:#x}')
                self.chunks.append((sector * 512, count * 512, kind, start, length))
        self.starts = [x[0] for x in self.chunks]
        header = self.read(1024, 512)
        if header[:2] not in (b'H+', b'HX'):
            raise ValueError('Expected HFS+ volume')
        self.block = u32(header, 40)
        self.catalog = header[272:352]

    @functools.lru_cache(maxsize=8)
    def chunk(self, index):
        import zlib
        _, size, kind, start, length = self.chunks[index]
        if size > 16 * 1024 * 1024:
            raise ValueError('Oversized UDIF chunk')
        if kind in (0, 2):
            return bytes(size)
        self.file.seek(self.base + start)
        data = self.file.read(length)
        if kind == 0x80000005:
            decoder = zlib.decompressobj()
            data = decoder.decompress(data, size + 1)
            if not decoder.eof or decoder.unconsumed_tail:
                raise ValueError('Invalid or oversized compressed chunk')
        if len(data) != size:
            raise ValueError('UDIF chunk length mismatch')
        return data

    def read(self, offset, length):
        result = bytearray()
        while length:
            index = bisect.bisect_right(self.starts, offset) - 1
            if index < 0:
                raise ValueError('Read before partition')
            start, size, *_ = self.chunks[index]
            count = min(length, start + size - offset)
            if count <= 0:
                raise ValueError('Read outside partition')
            result.extend(self.chunk(index)[offset - start:offset - start + count])
            offset += count
            length -= count
        return bytes(result)

    def fork(self, descriptor):
        length = struct.unpack_from('>Q', descriptor)[0]
        if length > 64 * 1024 * 1024:
            raise ValueError('Oversized HFS fork')
        data = bytearray()
        for i in range(8):
            start, count = struct.unpack_from('>II', descriptor, 16 + i * 8)
            if count:
                data.extend(self.read(start * self.block, min(count * self.block, length - len(data))))
            if len(data) == length:
                return bytes(data)
        raise ValueError('HFS extents overflow is not supported')

    def inspect(self):
        catalog = self.fork(self.catalog)
        size = u16(catalog, 32)
        if size < 512 or size > 32768:
            raise ValueError('Invalid B-tree node size')
        folders = {2: ''}
        files = []
        for position in range(0, len(catalog), size):
            node = catalog[position:position + size]
            if len(node) != size or node[8] != 0xff:  # leaf node
                continue
            for i in range(u16(node, 10)):
                record = u16(node, size - 2 * (i + 1))
                keylen = u16(node, record)
                parent = u32(node, record + 2)
                name_length = u16(node, record + 6)
                name = node[record + 8:record + 8 + 2 * name_length].decode('utf-16-be')
                value = record + 2 + keylen
                kind = u16(node, value)
                if kind == 1:
                    folders[u32(node, value + 8)] = (parent, name)
                elif kind == 2 and name in ('SystemVersion.plist', 'PlatformSupport.plist'):
                    files.append((parent, name, node[value + 88:value + 168]))
        def path(parent):
            parts = []
            for _ in range(128):
                if parent == 2:
                    return '/' + '/'.join(reversed(parts))
                parent, name = folders[parent]
                parts.append(name)
            raise ValueError('Catalog folder cycle')
        result = {}
        for parent, name, descriptor in files:
            location = path(parent) + '/' + name
            try:
                result[location] = plistlib.loads(self.fork(descriptor))
            except (plistlib.InvalidFileException, ValueError) as error:
                # Newer recoveries (macOS 26) store small system files HFS+-compressed
                # (decmpfs): the data fork is empty. Report it rather than guessing.
                result[location] = {'unreadable': f'{type(error).__name__}: likely HFS+-compressed; not decoded by this read-only tool'}
        if '/System/Library/CoreServices/SystemVersion.plist' not in result:
            raise ValueError('Recovery SystemVersion was not found')
        return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image')
    args = parser.parse_args()
    recovery = Recovery(args.image)
    try:
        print(json.dumps(recovery.inspect(), indent=2, default=str))
    finally:
        recovery.file.close()
