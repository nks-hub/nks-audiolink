import importlib.util
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / 'tools/release_metadata.py'
spec = importlib.util.spec_from_file_location('release_metadata', SCRIPT)
metadata = importlib.util.module_from_spec(spec)
spec.loader.exec_module(metadata)


class ReleaseMetadataTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.props = self.root / 'Directory.Build.props'

    def version(self, value):
        self.props.write_text(f'<Project><PropertyGroup><Version>{value}</Version></PropertyGroup></Project>')

    def test_stable_and_prerelease_classification(self):
        for version, prerelease in [('0.1.0', False), ('0.1.0-rc.1', True), ('1.2.3-alpha-2.0', True),
                                    ('1.2.3+build-with-hyphen', False), ('1.2.3-rc.1+build.7', True)]:
            with self.subTest(version=version):
                self.version(version)
                self.assertEqual({'version': version, 'tag': 'v' + version, 'prerelease': prerelease},
                                 metadata.release_metadata(self.props, 'v' + version))

    def test_tag_must_match_source_version_exactly(self):
        self.version('0.1.0-rc.1')
        for tag in ('v0.1.0', 'v0.1.0-rc.2', '0.1.0-rc.1', 'v0.1.0-rc.1-extra', 'main'):
            with self.subTest(tag=tag), self.assertRaisesRegex(ValueError, 'does not match'):
                metadata.release_metadata(self.props, tag)

    def test_manual_and_ci_artifact_mode_has_no_tag_requirement(self):
        self.version('0.1.0-rc.1')
        self.assertEqual(metadata.release_metadata(self.props), metadata.release_metadata(self.props, ''))

    def test_invalid_or_dynamic_versions_are_rejected(self):
        for version in ('', '1.2', '01.2.3', '1.02.3', '1.2.03', '1.2.3-01', '1.2.3-rc..1',
                        '1.2.3-', '1.2.3+', '$(ReleaseVersion)', '1.2.3\nprerelease=false'):
            with self.subTest(version=version):
                self.version(version)
                with self.assertRaisesRegex(ValueError, 'Invalid release SemVer'):
                    metadata.release_metadata(self.props)

    def test_missing_or_ambiguous_versions_are_rejected(self):
        for xml in ('<Project/>', '<Project><Version>1.0.0</Version><Version>2.0.0</Version></Project>'):
            with self.subTest(xml=xml):
                self.props.write_text(xml)
                with self.assertRaisesRegex(ValueError, 'exactly one'):
                    metadata.release_metadata(self.props)

    def test_conditional_version_is_rejected(self):
        for xml in ('<Project><Version Condition="true">1.0.0</Version></Project>',
                    '<Project><PropertyGroup Condition="true"><Version>1.0.0</Version></PropertyGroup></Project>'):
            with self.subTest(xml=xml):
                self.props.write_text(xml)
                with self.assertRaisesRegex(ValueError, 'unconditional'):
                    metadata.release_metadata(self.props)

    def test_cli_emits_github_outputs_only_after_successful_validation(self):
        self.version('0.1.0-rc.1')
        output = self.root / 'github-output'
        command = [sys.executable, str(SCRIPT), '--props', str(self.props), '--github-output', str(output)]
        failed = subprocess.run(command + ['--tag', 'v0.1.0'], capture_output=True, text=True)
        self.assertEqual(1, failed.returncode)
        self.assertFalse(output.exists())
        self.assertIn('does not match', failed.stderr)
        passed = subprocess.run(command + ['--tag', 'v0.1.0-rc.1'], capture_output=True, text=True)
        self.assertEqual(0, passed.returncode, passed.stderr)
        self.assertTrue(json.loads(passed.stdout)['prerelease'])
        self.assertEqual('version=0.1.0-rc.1\nprerelease=true\n', output.read_text())

    def test_notes_select_exact_dated_version_and_retain_known_limits(self):
        changelog = self.root / 'CHANGELOG.md'
        changelog.write_text('# Changelog\n\n## Unreleased\n\nFuture work.\n\n'
                             '## 0.1.0-rc.10 — 2026-10-24\n\nDifferent candidate.\n\n'
                             '## 0.1.0-rc.1 — 2026-10-23\n\n### Added\n\n- Signal meters.\n\n'
                             '### Known pending manual checks\n\n- Physical cable test.\n\n'
                             '## 0.0.9 — 2026-10-01\n\nOld release.\n', encoding='utf-8')
        notes = metadata.changelog_notes(changelog, '0.1.0-rc.1')
        self.assertTrue(notes.startswith('## 0.1.0-rc.1 — 2026-10-23\n'))
        self.assertIn('### Known pending manual checks\n\n- Physical cable test.', notes)
        self.assertNotIn('Different candidate', notes)
        self.assertNotIn('Old release', notes)
        self.assertNotIn('Future work', notes)

    def test_notes_reject_missing_duplicate_undated_empty_or_invalid_date(self):
        changelog = self.root / 'CHANGELOG.md'
        for contents in ('## 1.0.1 — 2026-10-23\nOther version.', '## 1.0.0\nUndated.',
                         '## 1.0.0 — 2026-99-23\nBad date.', '## 1.0.0 — 2026-10-23\n\n',
                         '## 1.0.0 — 2026-10-23\nFirst.\n## 1.0.0 — 2026-10-23\nDuplicate.'):
            with self.subTest(contents=contents):
                changelog.write_text(contents, encoding='utf-8')
                with self.assertRaises(ValueError):
                    metadata.changelog_notes(changelog, '1.0.0')

    def test_notes_links_point_to_versioned_sources(self):
        notes = '[Validation](docs/VALIDATION.md) [External](https://example.com) [Local](#known-limits)\n'
        linked = metadata.link_release_notes(notes, 'nks-hub/nks-audiolink', 'v0.1.0-rc.1')
        self.assertIn('https://github.com/nks-hub/nks-audiolink/blob/v0.1.0-rc.1/docs/VALIDATION.md', linked)
        self.assertIn('[External](https://example.com)', linked)
        self.assertIn('[Local](#known-limits)', linked)

    def make_assets(self):
        assets = self.root / 'assets'
        assets.mkdir()
        for platform, name in [('windows-x64', 'nks-audiolink-windows-x64.zip'),
                               ('linux-x64', 'nks-audiolink-linux-x64.tar.gz')]:
            data = ('verified package ' + platform).encode()
            (assets / name).write_bytes(data)
            report = {'platform': platform, 'archive': {'name': name, 'sha256': hashlib.sha256(data).hexdigest()}}
            (assets / (platform.split('-')[0] + '-package-verification.json')).write_text(json.dumps(report))
        (assets / 'SHA256SUMS').write_text('checksums are hashed as a separate asset\n')
        return assets

    def test_build_manifest_maps_downloads_to_exact_version_and_commit(self):
        self.version('0.1.0-rc.1')
        assets = self.make_assets()
        manifest = metadata.build_manifest(metadata.release_metadata(self.props), 'A' * 40, assets, 'v0.1.0-rc.1')
        self.assertEqual('0.1.0-rc.1', manifest['productVersion'])
        self.assertEqual('a' * 40, manifest['sourceRevision'])
        self.assertEqual('v0.1.0-rc.1', manifest['releaseTag'])
        self.assertTrue(manifest['prerelease'])
        self.assertEqual(5, len(manifest['artifacts']))
        for name, record in manifest['artifacts'].items():
            self.assertEqual(hashlib.sha256((assets / name).read_bytes()).hexdigest(), record['sha256'])
            self.assertEqual((assets / name).stat().st_size, record['bytes'])

    def test_manifest_rejects_archive_that_no_longer_matches_verification_report(self):
        self.version('1.0.0')
        assets = self.make_assets()
        (assets / 'nks-audiolink-windows-x64.zip').write_bytes(b'modified after verification')
        with self.assertRaisesRegex(ValueError, 'does not match'):
            metadata.build_manifest(metadata.release_metadata(self.props), 'a' * 40, assets)

    def test_build_revision_must_be_a_full_hash(self):
        for revision in ('7c33f7c', 'main', 'g' * 40, 'a' * 40 + '\nprerelease=false'):
            with self.subTest(revision=revision), self.assertRaisesRegex(ValueError, 'full Git'):
                metadata.source_revision(revision)

    def test_cli_writes_real_multiline_notes_and_exports_revision(self):
        self.version('0.1.0-rc.1')
        changelog = self.root / 'CHANGELOG.md'
        changelog.write_text('## 0.1.0-rc.1 — 2026-10-23\n\n### Known pending manual checks\n\n- Cable test.\n', encoding='utf-8')
        notes = self.root / 'notes.md'
        output = self.root / 'github-output'
        result = subprocess.run([sys.executable, str(SCRIPT), '--props', str(self.props), '--tag', 'v0.1.0-rc.1',
            '--source-revision', 'a' * 40, '--github-output', str(output), '--notes-file', str(notes),
            '--changelog', str(changelog), '--repository', 'nks-hub/nks-audiolink',
            '--run-url', 'https://github.com/nks-hub/nks-audiolink/actions/runs/123'], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('source_revision=' + 'a' * 40 + '\n', output.read_text())
        contents = notes.read_text(encoding='utf-8')
        self.assertIn('### Known pending manual checks\n\n- Cable test.', contents)
        self.assertIn('/actions/runs/123', contents)
        self.assertIn('/releases/download/v0.1.0-rc.1/release-build.json', contents)


if __name__ == '__main__':
    unittest.main()
