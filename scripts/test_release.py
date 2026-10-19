"""Offline packaging/manifest checks; run after the Release build."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET
import tempfile
import unittest
import zipfile
from release_version import artifact_version

ROOT = Path(__file__).resolve().parents[1]
VERSION = ET.parse(ROOT / "Jellyfin.Plugin.JellySeeNotifications/Jellyfin.Plugin.JellySeeNotifications.csproj").findtext(".//ReleaseVersion")
VERSION12 = artifact_version(VERSION, "12.0.0.0")


class ReleaseTests(unittest.TestCase):
    def test_package_and_manifest_keep_both_abis(self):
        results = ROOT / 'TestResults'
        results.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=results) as work:
            work = Path(work)
            notes = work / 'notes.txt'
            notes.write_text('Initial offline package verification.\n')
            subprocess.run([sys.executable, 'scripts/package.py', '--version', VERSION, '--notes', str(notes)], cwd=ROOT, check=True)
            manifest = work / 'manifest.json'
            manifest.write_bytes((ROOT / 'manifest.json').read_bytes())
            args = [sys.executable, 'scripts/update-manifest.py', '--manifest', str(manifest), '--version', VERSION, '--changelog-file', str(notes)]
            for suffix, abi in [('jf10.11', '10.11.0.0'), ('jf12', '12.0.0.0')]:
                archive = ROOT / 'dist' / f'jellysee-notifications_{VERSION}_{suffix}.zip'
                with zipfile.ZipFile(archive) as package:
                    self.assertEqual(set(package.namelist()), {'Jellyfin.Plugin.JellySeeNotifications.dll', 'meta.json', 'LICENSE'})
                    meta = json.loads(package.read('meta.json'))
                    self.assertEqual(meta['targetAbi'], abi)
                    self.assertEqual(meta['version'], VERSION12 if suffix == 'jf12' else VERSION)
                    self.assertEqual(meta['guid'], '467b751b-71c7-4e3d-a688-8ef46193d251')
                args += ['--entry', f'{archive}={abi}=https://example.invalid/{archive.name}']
            for _ in range(2):
                subprocess.run(args, cwd=ROOT, check=True, capture_output=True)
            versions = json.loads(manifest.read_text())[0]['versions']
            self.assertEqual([v['targetAbi'] for v in versions[:2]], ['12.0.0.0', '10.11.0.0'])
            self.assertEqual([v['version'] for v in versions[:2]], [VERSION12, VERSION])
            self.verify_server_upgrade(versions)
            for line in (ROOT / 'dist/SHA256SUMS.txt').read_text().splitlines():
                digest, name = line.split('  ')
                self.assertEqual(digest, hashlib.sha256((ROOT / 'dist' / name).read_bytes()).hexdigest())

    def verify_server_upgrade(self, versions):
        # Source-checked against v10.11.0 and v12.0 InstallationManager:
        # GetCompatibleVersions: TargetAbi <= appVer, VersionNumber >= minVersion,
        # OrderByDescending(VersionNumber); GetAvailablePluginUpdates: Version > installed.
        def number(value):
            return tuple(map(int, value.split('.')))

        def update(server, installed):
            compatible = sorted((v for v in versions if number(v['targetAbi']) <= number(server)
                                 and number(v['version']) >= number(installed)),
                                key=lambda v: number(v['version']), reverse=True)
            return next((v for v in compatible if number(v['version']) > number(installed)), None)

        self.assertEqual(update('10.11.8.0', '1.0.0.0')['version'], VERSION)
        self.assertIsNone(update('10.11.8.0', VERSION))
        migrated = update('12.0.0.0', '1.0.0.0')
        self.assertEqual(migrated['targetAbi'], '12.0.0.0')
        self.assertEqual(migrated['version'], VERSION12)
        self.assertEqual(update('12.0.0.0', '1.0.0.1')['version'], VERSION12)
        self.assertIsNone(update('12.0.0.0', VERSION12))
        for server, abi in [('10.11.8.0', '10.11.0.0'), ('12.0.0.0', '12.0.0.0')]:
            self.assertEqual(update(server, '0.9.0.1')['targetAbi'], abi)

    def test_release_revision_is_reserved_for_abi_migration(self):
        from release_version import artifact_version
        self.assertEqual(artifact_version('2.3.4.0', '12.0.0.0'), '2.3.4.1')
        for invalid in ('1.0.0.1', '1.0.0', '1.0.0.65535', '65535.0.0.0'):
            with self.assertRaises(ValueError):
                artifact_version(invalid, '12.0.0.0')

    def test_workflows_pin_every_action_and_release_only_tags(self):
        import re
        for workflow in (ROOT / '.github/workflows').glob('*.yml'):
            for action in re.findall(r'uses:\s*(\S+)', workflow.read_text()):
                self.assertRegex(action, r'@[0-9a-f]{40}$')
        release = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertNotIn('workflow_dispatch', release)
        self.assertIn('dotnet restore --locked-mode', release)
        self.assertIn('persist-credentials: false', release)


if __name__ == '__main__':
    unittest.main()
