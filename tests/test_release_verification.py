"""Run with: python3 -m unittest discover -s tests -p test_release_verification.py"""
import importlib.util
import io
import json
import os
import pathlib
import stat
import struct
import tarfile
import tempfile
import unittest
import zipfile
import zlib
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('verify_release', pathlib.Path(__file__).resolve().parents[1] / 'tools/verify_release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def bundle(payload, compressed=False):
    data = bytearray(b'MZ' + b'\0' * 6 + b'\0' * 8 + release.SIGNATURE)
    start = len(data)
    if compressed:
        compressor = zlib.compressobj(wbits=-15)
        stored = compressor.compress(payload) + compressor.flush()
    else:
        stored = payload
    data.extend(stored)
    header = len(data)
    struct.pack_into('<q', data, 8, header)
    data.extend(struct.pack('<IIi', 6, 0, 1) + b'\x04test' + b'\0' * 40)
    name = b'file.json'
    data.extend(struct.pack('<qqqB', start, len(payload), len(stored) if compressed else 0, 4))
    data.extend(bytes([len(name)]) + name)
    return bytes(data)


class ReleaseVerificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)

    def test_reads_real_layout_and_raw_deflate_bundle_entries(self):
        for compressed in (False, True):
            with self.subTest(compressed=compressed):
                data = bundle(b'{"runtime": "9.0.20"}', compressed)
                self.assertEqual(b'{"runtime": "9.0.20"}', release.entry_bytes(data, release.bundle_entries(data), 'file.json'))

    def test_rejects_corrupt_bundle_offsets(self):
        data = bytearray(bundle(b'test'))
        struct.pack_into('<q', data, 8, len(data) + 10)
        with self.assertRaisesRegex(release.VerificationError, 'header offset'):
            release.bundle_entries(data)

    def make_linux_binary(self, runtime='9.0.20', dashboard=b'<html>current</html>'):
        source = self.root / 'repo'
        resource = source / 'src/NksAudioLink.Server/Dashboard.html'
        resource.parent.mkdir(parents=True)
        resource.write_bytes(b'<html>current</html>')
        config = {'runtimeOptions': {
            'includedFrameworks': [{'name': name, 'version': runtime} for name in
                                   ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')],
            'configProperties': {'System.Threading.ThreadPool.UnfairSemaphoreSpinLimit': 0}}}
        files = {'NksAudioLink.Server.runtimeconfig.json': json.dumps(config).encode(),
                 'NksAudioLink.Server.dll': dashboard, 'NksAudioLink.Core.dll': b'core',
                 'System.Private.CoreLib.dll': b'runtime',
                 'Microsoft.AspNetCore.Server.Kestrel.Core.dll': b'http'}
        data = bytearray(128)
        data[:6] = b'\x7fELF\x02\x01'
        struct.pack_into('<H', data, 18, 62)
        data[64:96] = release.SIGNATURE
        entries = []
        for name, content in files.items():
            start = len(data)
            data.extend(content)
            encoded = name.encode()
            entries.append(struct.pack('<qqqB', start, len(content), 0, 1) + bytes([len(encoded)]) + encoded)
        struct.pack_into('<q', data, 56, len(data))
        data.extend(struct.pack('<IIi', 6, 0, len(entries)) + b'\x04test' + b'\0' * 40)
        for entry in entries:
            data.extend(entry)
        binary = self.root / 'NksAudioLink.Server'
        binary.write_bytes(data)
        return source, binary

    def test_binary_inspection_requires_bundled_runtimes_and_current_dashboard(self):
        source, binary = self.make_linux_binary()
        report = release.verify_binary(binary, 'linux-x64', '9.0.20', source)
        self.assertEqual('9.0.20', report['frameworks']['Microsoft.AspNetCore.App'])
        self.assertEqual(5, report['embeddedFiles'])

    def test_runtime_version_mismatch_is_rejected(self):
        source, binary = self.make_linux_binary(runtime='9.0.4')
        with self.assertRaisesRegex(release.VerificationError, 'runtime mismatch'):
            release.verify_binary(binary, 'linux-x64', '9.0.20', source)

    def test_stale_embedded_dashboard_is_rejected(self):
        source, binary = self.make_linux_binary(dashboard=b'<html>old</html>')
        with self.assertRaisesRegex(release.VerificationError, 'dashboard does not match'):
            release.verify_binary(binary, 'linux-x64', '9.0.20', source)

    def test_cli_bundle_requires_winforms_but_not_wpf(self):
        app = 'NksAudioLink.Cli'
        config = {'runtimeOptions': {'includedFrameworks': [
            {'name': name, 'version': '9.0.20'} for name in
            ('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')]}}
        files = {app + '.runtimeconfig.json': json.dumps(config).encode(),
                 app + '.dll': b'cli', 'NksAudioLink.Core.dll': b'core',
                 'System.Private.CoreLib.dll': b'runtime', 'System.Windows.Forms.dll': b'forms',
                 'NAudio.Core.dll': b'audio', 'NAudio.Wasapi.dll': b'wasapi'}
        data = bytearray(256)
        data[:2] = b'MZ'
        struct.pack_into('<I', data, 0x3c, 128)
        data[128:132] = b'PE\0\0'
        struct.pack_into('<H', data, 132, 0x8664)
        data[160:192] = release.SIGNATURE
        entries = []
        for name, content in files.items():
            start = len(data)
            data.extend(content)
            encoded = name.encode()
            entries.append(struct.pack('<qqqB', start, len(content), 0, 1) + bytes([len(encoded)]) + encoded)
        struct.pack_into('<q', data, 152, len(data))
        data.extend(struct.pack('<IIi', 6, 0, len(entries)) + b'\x04test' + b'\0' * 40)
        for entry in entries:
            data.extend(entry)
        binary = self.root / (app + '.exe')
        binary.write_bytes(data)
        report = release.verify_binary(binary, 'windows-x64', '9.0.20', self.root)
        self.assertEqual('9.0.20', report['frameworks']['Microsoft.WindowsDesktop.App'])

    def test_rejects_unsafe_paths(self):
        for name in ('../key.pem', '/etc/passwd', 'C:/secret', 'a\\b', 'a/../b', 'a//b',
                     'NUL.txt', 'docs/COM1', 'CON', 'README.md.', 'README.md ',
                     'file\x00.txt', 'doc?.md', 'doc*.md', 'doc|.md'):
            with self.subTest(name=name), self.assertRaises(release.VerificationError):
                release.safe_name(name)

    def test_zip_traversal_and_case_duplicates_are_rejected(self):
        for names in (['../escape'], ['README.md', 'readme.md']):
            archive = self.root / 'bad.zip'
            with zipfile.ZipFile(archive, 'w') as output:
                for name in names:
                    item = zipfile.ZipInfo(name)
                    item.external_attr = (stat.S_IFREG | 0o644) << 16
                    output.writestr(item, 'text')
            with self.assertRaisesRegex(release.VerificationError, 'Unsafe|Duplicate'):
                release.extract_checked(archive, self.root / 'extract')
        self.assertFalse((self.root / 'escape').exists())

    def test_tar_links_and_nonexecutable_server_are_rejected(self):
        for link in (False, True):
            archive = self.root / 'bad.tar.gz'
            with tarfile.open(archive, 'w:gz') as output:
                member = tarfile.TarInfo('NksAudioLink.Server')
                member.mode = 0o644
                if link:
                    member.type = tarfile.SYMTYPE
                    member.linkname = '/etc/passwd'
                output.addfile(member)
            with self.assertRaises(release.VerificationError):
                release.extract_checked(archive, self.root / 'extract')

    def test_archives_are_repeatable_and_roundtrip_preserves_bytes(self):
        source = self.root / 'source'
        source.mkdir()
        (source / 'README.md').write_bytes(b'public\n')
        (source / 'NksAudioLink.Server').write_bytes(b'binary')
        (source / 'a-note.txt').write_bytes(b'archive ordering\n')
        for platform, extension in (('windows-x64', '.zip'), ('linux-x64', '.tar.gz')):
            with self.subTest(platform=platform):
                first, second = self.root / ('first' + extension), self.root / ('second' + extension)
                release.create_archive(source, first, platform)
                os.utime(source / 'README.md', (946684800, 946684800))
                release.create_archive(source, second, platform)
                self.assertEqual(first.read_bytes(), second.read_bytes())
                expected_order = ['NksAudioLink.Server', 'README.md', 'a-note.txt']
                if platform == 'windows-x64':
                    with zipfile.ZipFile(first) as archive:
                        self.assertEqual(expected_order, archive.namelist())
                else:
                    with tarfile.open(first) as archive:
                        self.assertEqual(expected_order, archive.getnames())
                target = self.root / platform
                release.extract_checked(first, target)
                self.assertEqual(b'public\n', (target / 'README.md').read_bytes())
                self.assertEqual(b'binary', (target / 'NksAudioLink.Server').read_bytes())

    def make_package(self):
        source, package = self.root / 'repo', self.root / 'package'
        public, required, _ = release.package_layout('linux-x64')
        for name in required:
            path = package / name
            path.parent.mkdir(parents=True, exist_ok=True)
            content = b'public\n'
            if name.endswith('server.json.example'):
                content = json.dumps({'pskBase64': None, 'allowCidrs': ['127.0.0.0/8'], 'httpPort': 0}).encode()
            path.write_bytes(content)
            if name in public:
                original = source / ('deploy/' + name if name.startswith('licenses/') else name)
                original.parent.mkdir(parents=True, exist_ok=True)
                original.write_bytes(content)
        return source, package

    def test_complete_package_is_verified(self):
        source, package = self.make_package()
        with patch.object(release, 'verify_binary', return_value={'frameworks': 'checked separately'}):
            report = release.verify_directory(package, 'linux-x64', source)
        self.assertIn('LICENSE', report['files'])
        self.assertIn('docs/COMPETITION.md', report['files'])
        self.assertIn('docs/RELEASING.md', report['files'])
        self.assertIn('deploy/linux/install.sh', report['files'])

    def test_missing_installer_is_rejected(self):
        source, package = self.make_package()
        (package / 'deploy/linux/install.sh').unlink()
        with self.assertRaisesRegex(release.VerificationError, 'Missing package files'):
            release.verify_directory(package, 'linux-x64', source)

    def test_private_configuration_cannot_be_packaged(self):
        source, package = self.make_package()
        (package / 'server.json').write_text('{"pskBase64":"secret"}')
        with self.assertRaisesRegex(release.VerificationError, 'Unexpected/private'):
            release.verify_directory(package, 'linux-x64', source)

    def test_embedded_secret_in_public_sample_is_rejected(self):
        source, package = self.make_package()
        for root in (source, package):
            (root / 'deploy/linux/server.json.example').write_text('{"pskBase64":"secret"}')
        with self.assertRaisesRegex(release.VerificationError, 'shared key'):
            release.verify_directory(package, 'linux-x64', source)

    def test_outdated_public_files_are_rejected(self):
        source, package = self.make_package()
        (package / 'README.md').write_text('old documentation')
        with self.assertRaisesRegex(release.VerificationError, 'Outdated or modified'):
            release.verify_directory(package, 'linux-x64', source)

    def test_archive_creation_does_not_overwrite_existing_artifact(self):
        destination = self.root / 'existing.zip'
        destination.write_bytes(b'keep me')
        with self.assertRaisesRegex(release.VerificationError, 'already exists'):
            release.create_archive(self.root, destination, 'windows-x64')
        self.assertEqual(b'keep me', destination.read_bytes())

    def test_archive_cannot_include_itself(self):
        with self.assertRaisesRegex(release.VerificationError, 'outside'):
            release.create_archive(self.root, self.root / 'archive.zip', 'windows-x64')

    def test_archive_permission_checks_do_not_depend_on_filename_extension(self):
        archive = self.root / 'disguised.zip'
        with tarfile.open(archive, 'w:gz') as output:
            member = tarfile.TarInfo('NksAudioLink.Server')
            member.mode = 0o644
            output.addfile(member)
        with self.assertRaisesRegex(release.VerificationError, 'permissions'):
            release.extract_checked(archive, self.root / 'extract')

    def test_zip_requires_regular_members_and_safe_permissions(self):
        for mode in (stat.S_IFLNK | 0o644, stat.S_IFREG | 0o666, stat.S_IFIFO | 0o644):
            with self.subTest(mode=oct(mode)):
                archive = self.root / 'bad.zip'
                with zipfile.ZipFile(archive, 'w') as output:
                    member = zipfile.ZipInfo('README.md')
                    member.external_attr = mode << 16
                    output.writestr(member, 'text')
                with self.assertRaisesRegex(release.VerificationError, 'Nonregular|permissions'):
                    release.extract_checked(archive, self.root / 'extract')

    def test_private_archive_members_are_rejected_before_writing(self):
        archive = self.root / 'private.zip'
        with zipfile.ZipFile(archive, 'w') as output:
            member = zipfile.ZipInfo('server.json')
            member.external_attr = (stat.S_IFREG | 0o644) << 16
            output.writestr(member, '{"pskBase64":"secret"}')
        target = self.root / 'extract'
        with self.assertRaisesRegex(release.VerificationError, 'Unexpected/private'):
            release.extract_checked(archive, target, 'linux-x64')
        self.assertFalse((target / 'server.json').exists())

    def test_archive_limits_are_enforced_before_decompression(self):
        archive = self.root / 'large.zip'
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
            member = zipfile.ZipInfo('README.md')
            member.external_attr = (stat.S_IFREG | 0o644) << 16
            output.writestr(member, b'A' * 1024)
        with patch.object(release, 'MAX_MEMBER_BYTES', 512):
            with self.assertRaisesRegex(release.VerificationError, 'size limit'):
                release.extract_checked(archive, self.root / 'extract')
        self.assertFalse((self.root / 'extract/README.md').exists())

    def test_compressed_bundle_rejects_output_larger_than_declared_size(self):
        data = bundle(b'A' * 1024, compressed=True)
        entries = release.bundle_entries(data)
        start, _, compressed = entries['file.json']
        entries['file.json'] = (start, 16, compressed)
        with self.assertRaisesRegex(release.VerificationError, 'compressed bundle'):
            release.entry_bytes(data, entries, 'file.json')


if __name__ == '__main__':
    unittest.main()
