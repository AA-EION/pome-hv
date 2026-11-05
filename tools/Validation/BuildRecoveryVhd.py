"""Restore a verified GPT/zlib recovery DMG to a new fixed VHD, without mounting.

The fixed VHD is an intermediate for Hyper-V Convert-VHD to produce VHDX.
The source GPT, HFS+ volume, boot metadata and all decoded sectors are retained.
Only the new output file is written. Existing output/partial files are refused.
"""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import struct
import uuid
import zlib

from InspectRecovery import Recovery, u32


def footer(size):
    sectors = min(size // 512, 65535 * 16 * 255)
    if sectors >= 65535 * 16 * 63:
        spt, heads = 255, 16
        track_heads = sectors // spt
    else:
        spt = 17
        track_heads = sectors // spt
        heads = max(4, (track_heads + 1023) // 1024)
        if track_heads >= heads * 1024 or heads > 16:
            spt, heads = 31, 16
            track_heads = sectors // spt
        if track_heads >= heads * 1024:
            spt, heads = 63, 16
            track_heads = sectors // spt
    timestamp = int((datetime.datetime.now(datetime.timezone.utc) - datetime.datetime(2000, 1, 1, tzinfo=datetime.timezone.utc)).total_seconds())
    data = bytearray(512)
    struct.pack_into('>8sIIQI4sI4sQQHBBII16sB', data, 0,
                     b'conectix', 2, 0x10000, 0xffffffffffffffff,
                     timestamp, b'pome', 0x10000, b'Wi2k', size, size,
                     track_heads // heads, heads, spt, 2, 0, uuid.uuid4().bytes, 0)
    struct.pack_into('>I', data, 64, (~sum(data)) & 0xffffffff)
    return data


def verify_gpt(stream, sectors):
    for lba in (1, sectors - 1):
        stream.seek(lba * 512)
        header = bytearray(stream.read(512))
        if header[:8] != b'EFI PART':
            raise ValueError('Expected primary and backup GPT headers')
        length, checksum = struct.unpack_from('<II', header, 12)
        if not 92 <= length <= 512:
            raise ValueError('Invalid GPT header size')
        struct.pack_into('<I', header, 16, 0)
        if zlib.crc32(header[:length]) != checksum:
            raise ValueError('GPT header checksum mismatch')
        if struct.unpack_from('<Q', header, 24)[0] != lba:
            raise ValueError('GPT header LBA mismatch')
        start, count, entry_size, entries_checksum = struct.unpack_from('<QIII', header, 72)
        if count * entry_size > 1024 * 1024 or start + (count * entry_size + 511) // 512 > sectors:
            raise ValueError('Invalid GPT partition array')
        stream.seek(start * 512)
        if zlib.crc32(stream.read(count * entry_size)) != entries_checksum:
            raise ValueError('GPT partition checksum mismatch')


def restore(image, destination, expected_hash):
    image, destination = Path(image), Path(destination)
    expected_hash = expected_hash.lower()
    if len(expected_hash) != 64 or any(c not in '0123456789abcdef' for c in expected_hash):
        raise ValueError('Expected a trusted SHA-256 digest')
    with image.open('rb') as source:
        if hashlib.file_digest(source, 'sha256').hexdigest() != expected_hash:
            raise ValueError('Recovery image hash mismatch')
    if destination.exists():
        raise FileExistsError(destination)
    partial = destination.with_name(destination.name + '.partial')
    recovery = Recovery(image)
    try:
        # Prove this is a supported recovery filesystem before producing output.
        metadata = recovery.inspect()
        if not 1 <= recovery.sectors <= 64 * 1024**3 // 512:
            raise ValueError('Recovery disk must be at most 64 GiB')
        disk_size = recovery.sectors * 512
        blocks = []
        for partition in recovery.metadata['resource-fork']['blkx']:
            table = partition['Data']
            if table[:4] != b'mish':
                raise ValueError('Invalid UDIF block map')
            first, count, base = struct.unpack_from('>QQQ', table, 8)
            if first + count > recovery.sectors:
                raise ValueError('Partition exceeds the image')
            for index in range(u32(table, 200)):
                kind, _, sector, length, offset, compressed = struct.unpack_from('>IIQQQQ', table, 204 + index * 40)
                if not length or kind == 0xffffffff:
                    continue
                if sector + length > count or kind not in (0, 1, 2, 0x80000005):
                    raise ValueError('Unsupported or out-of-bounds UDIF chunk')
                if kind not in (0, 2) and length * 512 > 16 * 1024**2:
                    raise ValueError('Oversized UDIF data chunk')
                if kind not in (0, 2) and (not 0 < compressed <= 17 * 1024**2 or base + offset + compressed > image.stat().st_size):
                    raise ValueError('Invalid compressed data range')
                blocks.append(((first + sector) * 512, length * 512, kind, base + offset, compressed))
        blocks.sort()
        position = 0
        for offset, size, *_ in blocks:
            if offset != position:
                raise ValueError('Overlapping or incomplete UDIF sector coverage')
            position += size
        if position != disk_size:
            raise ValueError('Incomplete UDIF disk image')
        with partial.open('x+b') as target:
            target.truncate(disk_size + 512)
            for offset, size, kind, source_offset, length in blocks:
                if kind in (0, 2):  # pre-zeroed free/zero sectors
                    continue
                recovery.file.seek(source_offset)
                data = recovery.file.read(length)
                if kind == 0x80000005:
                    decoder = zlib.decompressobj()
                    data = decoder.decompress(data, size + 1)
                    if not decoder.eof or decoder.unconsumed_tail:
                        raise ValueError('Invalid UDIF compressed data')
                if len(data) != size:
                    raise ValueError('Decoded sector count mismatch')
                target.seek(offset)
                target.write(data)
            verify_gpt(target, recovery.sectors)
            target.seek(disk_size)
            target.write(footer(disk_size))
        with image.open('rb') as source:
            if hashlib.file_digest(source, 'sha256').hexdigest() != expected_hash:
                raise ValueError('Source recovery changed during conversion')
        # rename refuses existing destinations on Windows; partials survive errors.
        partial.rename(destination)
        return {'sourceSha256': expected_hash, 'virtualBytes': disk_size, 'path': str(destination.resolve()),
                'systemVersion': metadata['/System/Library/CoreServices/SystemVersion.plist']}
    finally:
        recovery.file.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image')
    parser.add_argument('destination')
    parser.add_argument('--sha256', required=True)
    args = parser.parse_args()
    print(json.dumps(restore(args.image, args.destination, args.sha256), indent=2))
