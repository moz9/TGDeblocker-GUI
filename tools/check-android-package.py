"""Inspect a built APK/AAB without running it. Standard library only."""
import argparse
import hashlib
import json
import struct
import zipfile
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('package', type=Path)
parser.add_argument('--abi', choices=['arm64-v8a', 'x86_64'], required=True)
args = parser.parse_args()
errors = []
libraries = []
tdlib_hash = None
with zipfile.ZipFile(args.package) as archive:
    names = archive.namelist()
    native_names = [name for name in names if name.endswith('.so') and '/lib/' in '/' + name]
    if not native_names:
        errors.append('No native libraries found')
    for name in native_names:
        data = archive.read(name)
        if f'/lib/{args.abi}/' not in '/' + name:
            errors.append(f'Unexpected ABI: {name}')
        if data[:6] != b'\x7fELF\x02\x01':
            errors.append(f'Not a little-endian ELF64 library: {name}')
            continue
        phoff = struct.unpack_from('<Q', data, 32)[0]
        phsize, phcount = struct.unpack_from('<HH', data, 54)
        alignments = []
        for index in range(phcount):
            offset = phoff + index * phsize
            if struct.unpack_from('<I', data, offset)[0] == 1:
                alignment = struct.unpack_from('<Q', data, offset + 48)[0]
                alignments.append(alignment)
                if alignment < 16384:
                    errors.append(f'ELF segment below 16 KiB alignment: {name}')
        libraries.append({'name': name, 'loadAlignments': alignments})
        if name.endswith('/libtdjson.so'):
            tdlib_hash = hashlib.sha256(data).hexdigest()
    if tdlib_hash is None:
        errors.append('TDLib is missing')
    notice = [name for name in names if name.endswith('/third-party-notices.txt')]
    if len(notice) != 1 or len(archive.read(notice[0])) < 1000:
        errors.append('Bundled third-party notices are missing')
    forbidden = [name for name in names if name.lower().endswith(('.dpapi', '.keystore', '.jks', '.p12', '.binlog', 'tdjson.dll'))]
    if forbidden:
        errors.append('Unexpected desktop/session/signing files: ' + ', '.join(forbidden))

print(json.dumps({'package': args.package.name, 'abi': args.abi,
                  'tdlibSha256': tdlib_hash, 'nativeLibraries': libraries,
                  'errors': errors}, indent=2))
raise SystemExit(bool(errors))
