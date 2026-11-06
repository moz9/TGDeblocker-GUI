"""Inspect a built APK/AAB without running it. Standard library only."""
import argparse
import hashlib
import json
import re
import struct
import subprocess
import zipfile
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('package', type=Path)
parser.add_argument('--abi', choices=['arm64-v8a', 'x86_64'], required=True)
parser.add_argument('--aapt', type=Path, help='Android SDK aapt executable; validates APK launcher icons')
args = parser.parse_args()
errors = []
libraries = []
tdlib_hash = None
icon_check = {'status': 'not_checked', 'reason': 'Pass --aapt with an APK to validate launcher icons.'}
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

    if args.aapt:
        if args.package.suffix.lower() != '.apk':
            errors.append('aapt launcher icon validation requires an APK.')
            icon_check = {'status': 'failed'}
        else:
            try:
                def aapt_dump(*parts):
                    return subprocess.run([str(args.aapt), 'dump', *parts], check=True,
                                          capture_output=True, text=True, encoding='utf-8', errors='replace').stdout
                badging = aapt_dump('badging', str(args.package))
                tree = aapt_dump('xmltree', str(args.package), 'AndroidManifest.xml')
                resources = aapt_dump('resources', str(args.package))
                application = re.search(r"(?m)^application:.*?\bicon='([^']*)'", badging)
                in_application = False
                attributes = {}
                for line in tree.splitlines():
                    stripped = line.strip()
                    if stripped.startswith('E: application '):
                        in_application = True
                        continue
                    if in_application and stripped.startswith('E: '):
                        break
                    if in_application:
                        match = re.match(r'A: android:(icon|roundIcon)\(0x[0-9a-f]+\)=@0x([0-9a-f]+)', stripped)
                        if match:
                            attributes[match.group(1)] = int(match.group(2), 16)
                resource_ids = {}
                for match in re.finditer(r'(?m)^\s*spec resource 0x([0-9a-f]+) [^:\s]+:mipmap/(appicon|appicon_round):', resources):
                    resource_ids[match.group(2)] = int(match.group(1), 16)
                if not application or not application.group(1):
                    errors.append('Application launcher icon is missing from the merged manifest.')
                for attribute, name in [('icon', 'appicon'), ('roundIcon', 'appicon_round')]:
                    if not attributes.get(attribute) or attributes[attribute] != resource_ids.get(name):
                        errors.append(f'Application {attribute} does not reference mipmap/{name}.')
                required = [f'res/mipmap-anydpi-v26/{name}.xml' for name in ('appicon', 'appicon_round')]
                required += [f'res/mipmap-{density}-v4/{name}.png'
                             for density in ('mdpi', 'hdpi', 'xhdpi', 'xxhdpi', 'xxxhdpi')
                             for name in ('appicon', 'appicon_round')]
                for name in required:
                    if name not in names:
                        errors.append(f'Missing launcher icon resource: {name}')
                icon_check = {'status': 'failed' if any('icon' in error.lower() for error in errors) else 'verified'}
            except (OSError, subprocess.CalledProcessError):
                errors.append('Could not inspect APK launcher icons with aapt.')
                icon_check = {'status': 'failed'}

print(json.dumps({'package': args.package.name, 'abi': args.abi,
                  'tdlibSha256': tdlib_hash, 'nativeLibraries': libraries,
                  'launcherIcon': icon_check, 'errors': errors}, indent=2))
raise SystemExit(bool(errors))
