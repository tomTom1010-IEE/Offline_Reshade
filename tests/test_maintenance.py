"""Regression checks for release gates, including realistic corrupt-package inputs."""
import copy
import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from sync_offline_contract import ROOT, generated_files, sync
from verify_offline_package import REQUIRED, validate_manifest


class ReleaseIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.payload = {p: b'fixture' for p in REQUIRED}
        baseline = json.loads((ROOT / 'maintenance/baseline.json').read_text())
        self.payload['maintenance/baseline.json'] = json.dumps(baseline).encode()
        self.payload['verification.json'] = json.dumps({'status': 'passed', 'commands': [{'exitCode': 0}]}).encode()
        self.manifest = {'schemaVersion': 2, 'version': baseline['productVersion'], 'provenance': {k: baseline[k] for k in ['upstream', 'interfaces', 'submodules']}}
        self.manifest['provenance']['baselineSha256'] = hashlib.sha256(self.payload['maintenance/baseline.json']).hexdigest()
        self.refresh()

    def refresh(self):
        self.manifest['files'] = [{'path': name, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()} for name, data in self.payload.items()]

    def check(self):
        validate_manifest(self.manifest, list(self.payload) + ['manifest.json'], self.payload.__getitem__)

    def test_valid_package(self):
        self.check()

    def test_corrupted_binary(self):
        self.payload['ReShade64.dll'] = b'changed'
        with self.assertRaisesRegex(AssertionError, 'hash/size'): self.check()

    def test_missing_runtime(self):
        del self.payload['Mcp/coreclr.dll']
        self.refresh()
        with self.assertRaisesRegex(AssertionError, 'Missing runtime'): self.check()

    def test_unlisted_payload(self):
        self.payload['stale.dll'] = b'stale'
        with self.assertRaisesRegex(AssertionError, 'Unlisted'): self.check()

    def test_forbidden_payload_and_traversal(self):
        for path in ['user.fx', '../outside.dll', 'C:/outside.dll', 'test.disabled', 'Package.appxmanifest']:
            with self.subTest(path=path):
                self.payload[path] = b'x'
                self.refresh()
                with self.assertRaises(AssertionError): self.check()
                del self.payload[path]

    def test_wrong_upstream(self):
        self.manifest['provenance']['upstream'] = copy.deepcopy(self.manifest['provenance']['upstream'])
        self.manifest['provenance']['upstream']['commit'] = '0' * 40
        with self.assertRaisesRegex(AssertionError, 'Provenance'): self.check()

    def test_failed_verification(self):
        self.payload['verification.json'] = json.dumps({'status': 'passed', 'commands': [{'exitCode': 1}]}).encode()
        self.refresh()
        with self.assertRaisesRegex(AssertionError, 'Failed build'): self.check()

    def test_generated_contract_drift(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'maintenance').mkdir()
            baseline = json.loads((ROOT / 'maintenance/baseline.json').read_text())
            (root / 'maintenance/baseline.json').write_text(json.dumps(baseline))
            for name, content in generated_files(baseline).items():
                (root / name).parent.mkdir(exist_ok=True)
                (root / name).write_text(content)
            sync(root)
            (root / 'include/offline_contract.hpp').write_text('old version')
            with self.assertRaisesRegex(ValueError, 'differs'): sync(root)


if __name__ == '__main__': unittest.main()
