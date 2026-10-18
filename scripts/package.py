#!/usr/bin/env python3
"""Package only the plugin DLL and metadata, never Jellyfin or NuGet assemblies."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
from datetime import datetime, timezone
import xml.etree.ElementTree as ET
import zipfile
from release_version import artifact_version

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'Jellyfin.Plugin.JellySeeNotifications'
NAME = 'Jellyfin.Plugin.JellySeeNotifications.dll'
GUID = '467b751b-71c7-4e3d-a688-8ef46193d251'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--version', required=True)
    ap.add_argument('--notes', type=Path, required=True)
    args = ap.parse_args()
    declared = ET.parse(PROJECT / 'Jellyfin.Plugin.JellySeeNotifications.csproj').findtext('.//ReleaseVersion')
    if not re.fullmatch(r'\d+\.\d+\.\d+\.\d+', args.version) or args.version != declared:
        raise SystemExit('Tag version must match the four-part project version')
    artifact_version(args.version, '12.0.0.0')  # validates reserved revision slot
    epoch = int(os.environ.get('SOURCE_DATE_EPOCH', '1767225600'))
    stamp = datetime.fromtimestamp(epoch, timezone.utc)
    notes = args.notes.read_text().strip()
    dist = ROOT / 'dist'
    dist.mkdir(exist_ok=True)
    hashes = []
    for tfm, abi, suffix in [('net9.0', '10.11.0.0', 'jf10.11'), ('net10.0', '12.0.0.0', 'jf12')]:
        dll = PROJECT / 'bin' / 'Release' / tfm / NAME
        meta = dict(guid=GUID, name='JellySee Notifications', version=artifact_version(args.version, abi),
                    targetAbi=abi, description='Private library notifications for JellySee.',
                    overview='Audience-scoped library notifications.', owner='HarshLabs',
                    category='Notifications', assemblies=[NAME], changelog=notes,
                    timestamp=stamp.strftime('%Y-%m-%dT%H:%M:%SZ'), status=0, autoUpdate=True)
        target = dist / f'jellysee-notifications_{args.version}_{suffix}.zip'
        with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, data in [(NAME, dll.read_bytes()), ('meta.json', (json.dumps(meta, indent=2) + '\n').encode()), ('LICENSE', (ROOT / 'LICENSE').read_bytes())]:
                info = zipfile.ZipInfo(name, stamp.timetuple()[:6])
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                archive.writestr(info, data)
        hashes.append(hashlib.sha256(target.read_bytes()).hexdigest() + '  ' + target.name)
    (dist / 'SHA256SUMS.txt').write_text('\n'.join(hashes) + '\n')


if __name__ == '__main__':
    main()
