#!/usr/bin/env python3
"""Publish per-ABI versions: release x.y.z.0 on 10.11, x.y.z.1 on 12.

Both InstallationManager versions filter targetAbi <= server version, sort by
plugin version descending, and require a strictly newer installed-plugin version.
The revision increment allows automatic ABI migration after a server upgrade.
Archive names and release tags continue to use x.y.z.0 for both artifacts.
"""
import argparse
import hashlib
import json
import pathlib
import sys
from datetime import datetime, timezone
from release_version import artifact_version
import zipfile


def md5(path: pathlib.Path) -> str:
    digest = hashlib.md5()  # noqa: S324 - matches the checksum format Jellyfin expects
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", default="manifest.json")
    ap.add_argument("--version", required=True)
    ap.add_argument("--changelog-file", required=True)
    ap.add_argument(
        "--entry",
        action="append",
        required=True,
        metavar="ZIP=ABI=URL",
        help="One per Jellyfin ABI.",
    )
    args = ap.parse_args()

    changelog = pathlib.Path(args.changelog_file).read_text().strip() + "\n"
    manifest_path = pathlib.Path(args.manifest)
    manifest = json.loads(manifest_path.read_text())
    if not manifest:
        print("manifest.json is empty", file=sys.stderr)
        return 1
    plugin = manifest[0]
    versions = plugin.setdefault("versions", [])

    entries = []
    for raw in args.entry:
        zip_part, abi, url = raw.split("=", 2)
        zip_path = pathlib.Path(zip_part)
        if not zip_path.is_file():
            print(f"missing artifact: {zip_path}", file=sys.stderr)
            return 1
        version = artifact_version(args.version, abi)
        with zipfile.ZipFile(zip_path) as archive:
            meta = json.loads(archive.read('meta.json'))
        if meta['version'] != version or meta['targetAbi'] != abi:
            raise ValueError('Package metadata does not match ABI version')
        entries.append(
            {
                "version": version,
                "changelog": changelog,
                "targetAbi": abi,
                "sourceUrl": url,
                "checksum": md5(zip_path),
                "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            }
        )

    # Remove both revisions (also cleans up old same-version dual-ABI entries).
    release_versions = {artifact_version(args.version, abi) for abi in ('10.11.0.0', '12.0.0.0')}
    versions[:] = [v for v in versions if v.get("version") not in release_versions] + entries
    versions.sort(key=lambda e: (tuple(map(int, e['version'].split('.'))), tuple(map(int, e['targetAbi'].split('.')))), reverse=True)

    manifest_path.write_text(json.dumps(manifest, indent=4) + "\n")
    for entry in entries:
        print(f"  added {entry['version']} targetAbi={entry['targetAbi']} -> {entry['sourceUrl']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
