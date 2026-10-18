"""Offline packaging/manifest checks; run after the Release build."""
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]


class ReleaseTests(unittest.TestCase):
    def test_package_and_manifest_keep_both_abis(self):
        results = ROOT / 'TestResults'
        results.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=results) as work:
            work = Path(work)
            notes = work / 'notes.txt'
            notes.write_text('Initial offline package verification.\n')
            subprocess.run(['python3', 'scripts/package.py', '--version', '1.0.0.0', '--notes', str(notes)], cwd=ROOT, check=True)
            manifest = work / 'manifest.json'
            manifest.write_bytes((ROOT / 'manifest.json').read_bytes())
            args = ['python3', 'scripts/update-manifest.py', '--manifest', str(manifest), '--version', '1.0.0.0', '--changelog-file', str(notes)]
            for suffix, abi in [('jf10.11', '10.11.0.0'), ('jf12', '12.0.0.0')]:
                archive = ROOT / 'dist' / f'jellysee-notifications_1.0.0.0_{suffix}.zip'
                with zipfile.ZipFile(archive) as package:
                    self.assertEqual(set(package.namelist()), {'Jellyfin.Plugin.JellySeeNotifications.dll', 'meta.json', 'LICENSE'})
                    meta = json.loads(package.read('meta.json'))
                    self.assertEqual(meta['targetAbi'], abi)
                    self.assertEqual(meta['guid'], '467b751b-71c7-4e3d-a688-8ef46193d251')
                args += ['--entry', f'{archive}={abi}=https://example.invalid/{archive.name}']
            for _ in range(2):
                subprocess.run(args, cwd=ROOT, check=True, capture_output=True)
            versions = json.loads(manifest.read_text())[0]['versions']
            self.assertEqual([v['targetAbi'] for v in versions[:2]], ['12.0.0.0', '10.11.0.0'])
            self.assertEqual(sum(v['version'] == '1.0.0.0' for v in versions), 2)
            for line in (ROOT / 'dist/SHA256SUMS.txt').read_text().splitlines():
                digest, name = line.split('  ')
                self.assertEqual(digest, hashlib.sha256((ROOT / 'dist' / name).read_bytes()).hexdigest())

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
