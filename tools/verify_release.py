#!/usr/bin/env python3
"""Verify AudioLink publish folders/archives and optionally create a repeatable archive.

Python 3.10+, standard library only. Run with --help. Bundle layout references:
https://github.com/dotnet/runtime/tree/v9.0.20/src/installer/managed/Microsoft.NET.HostModel
Only public, explicitly listed files may enter a package. Run after publishing and
copying documentation/licenses; this tool never installs or starts the product.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import pathlib
import stat
import struct
import tarfile
import tempfile
import zipfile
import zlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
# Current packages are below 350 MiB. Bound untrusted input before extraction.
MAX_MEMBER_BYTES = 512 * 1024 * 1024
MAX_PACKAGE_BYTES = 1024 * 1024 * 1024
MAX_ARCHIVE_MEMBERS = 256
SIGNATURE = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
PUBLIC_FILES = {
    'README.md', 'LICENSE', 'TODO.md', 'CHANGELOG.md', 'THIRD_PARTY_NOTICES.md',
    'docs/INSTALL.md', 'docs/PROTOCOL.md', 'docs/VALIDATION.md', 'docs/WINDOWS.md',
    'docs/screenshots/client.png', 'docs/screenshots/client-preview.png',
    'docs/screenshots/server-dashboard.png',
}
LINUX_DEPLOY = {'deploy/linux/install.sh', 'deploy/linux/nks-audiolink.service',
                'deploy/linux/server.json.example'}
EXECUTABLES = {'linux-x64': ['NksAudioLink.Server'],
               'windows-x64': ['NksAudioLink.App.exe', 'cli/NksAudioLink.Cli.exe']}
LICENSE_TREES = {'linux-x64': ['dotnet-coreapp-linux-x64', 'dotnet-aspnetcore-linux-x64'],
                 'windows-x64': ['dotnet-coreapp-win-x64', 'dotnet-windowsdesktop-win-x64']}


class VerificationError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def safe_name(name):
    require(name and '\\' not in name and ':' not in name and not name.startswith('/'),
            f'Unsafe package path: {name!r}')
    require(all(part not in ('', '.', '..') for part in name.split('/')),
            f'Unsafe package path: {name!r}')
    reserved = {'CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$'} | {
        prefix + suffix for prefix in ('COM', 'LPT') for suffix in '123456789\u00b9\u00b2\u00b3'}
    require(not any(ord(character) < 32 or character in '<>"|?*' for character in name) and
            all(part == part.rstrip(' .') and part.split('.')[0].upper() not in reserved
                for part in name.split('/')),
            f'Unsafe Windows package path: {name!r}')
    return name


def read_string(stream):
    length = 0
    for shift in range(0, 35, 7):
        raw = stream.read(1)
        require(len(raw) == 1, 'Truncated bundle string')
        length |= (raw[0] & 127) << shift
        if not raw[0] & 128:
            require(length < 16384, 'Excessive bundle path length')
            raw = stream.read(length)
            require(len(raw) == length, 'Truncated bundle path')
            return raw.decode('utf-8')
    raise VerificationError('Invalid bundle string length')


def bundle_entries(data):
    marker = data.find(SIGNATURE)
    require(marker >= 8, 'Not a .NET single-file executable')
    offset = struct.unpack_from('<q', data, marker - 8)[0]
    require(0 < offset < len(data) - 12, 'Invalid bundle header offset')
    stream = io.BytesIO(data)
    stream.seek(offset)
    major, minor, count = struct.unpack('<IIi', stream.read(12))
    require(major in (2, 6) and minor == 0 and 0 < count < 10000,
            f'Unsupported bundle header: {major}.{minor}, entries={count}')
    read_string(stream)  # Bundle ID.
    require(len(stream.read(40)) == 40, 'Truncated bundle header')
    entries = {}
    for _ in range(count):
        values = struct.unpack('<qqq' if major >= 6 else '<qq', stream.read(24 if major >= 6 else 16))
        start, size = values[:2]
        compressed = values[2] if major >= 6 else 0
        require(len(stream.read(1)) == 1, 'Truncated bundle entry')
        name = safe_name(read_string(stream))
        require(name not in entries, f'Duplicate bundle entry: {name}')
        require(start >= 0 and 0 <= size <= MAX_MEMBER_BYTES and compressed >= 0 and
                start + (compressed or size) <= offset,
                f'Invalid bundle entry bounds: {name}')
        entries[name] = (start, size, compressed)
    return entries


def entry_bytes(data, entries, name):
    require(name in entries, f'Missing embedded file: {name}')
    start, size, compressed = entries[name]
    result = data[start:start + (compressed or size)]
    if compressed:
        inflater = zlib.decompressobj(-zlib.MAX_WBITS)
        result = inflater.decompress(result, size + 1)
        require(inflater.eof and not inflater.unused_data and not inflater.unconsumed_tail,
                f'Invalid compressed bundle entry: {name}')
    require(len(result) == size, f'Invalid decompressed size: {name}')
    return result


def verify_binary(path, platform, runtime_version, repository):
    require(path.stat().st_size <= MAX_MEMBER_BYTES, f'Executable exceeds size limit: {path.name}')
    data = path.read_bytes()
    require(data.startswith(b'MZ' if platform == 'windows-x64' else b'\x7fELF'),
            f'Wrong executable format: {path.name}')
    if platform == 'windows-x64':
        pe = struct.unpack_from('<I', data, 0x3c)[0]
        require(data[pe:pe + 4] == b'PE\0\0' and struct.unpack_from('<H', data, pe + 4)[0] == 0x8664,
                'Windows executable must be x64')
    else:
        require(data[4:6] == b'\x02\x01' and struct.unpack_from('<H', data, 18)[0] == 62,
                'Linux executable must be little-endian x64 ELF')
    entries = bundle_entries(data)
    app = path.name.removesuffix('.exe')
    config = json.loads(entry_bytes(data, entries, app + '.runtimeconfig.json'))['runtimeOptions']
    frameworks = {item['name']: item['version'] for item in config.get('includedFrameworks', [])}
    expected = {'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App' if platform == 'windows-x64'
                else 'Microsoft.AspNetCore.App'}
    require(set(frameworks) == expected, f'{path.name}: not the expected self-contained runtimes: {frameworks}')
    require(all(version == runtime_version for version in frameworks.values()),
            f'{path.name}: runtime mismatch, expected {runtime_version}, got {frameworks}')
    require('framework' not in config and 'frameworks' not in config,
            f'{path.name}: framework-dependent runtime requested')
    for name in (app + '.dll', 'NksAudioLink.Core.dll', 'System.Private.CoreLib.dll'):
        require(name in entries, f'{path.name}: missing embedded assembly {name}')
    application = entry_bytes(data, entries, app + '.dll')
    if platform == 'linux-x64':
        require('Microsoft.AspNetCore.Server.Kestrel.Core.dll' in entries, 'Missing embedded Kestrel')
        # CoreCLR is statically integrated into the single-file host.
        require(config.get('configProperties', {}).get('System.Threading.ThreadPool.UnfairSemaphoreSpinLimit') == 0,
                'Linux socket/threadpool setting is missing')
        require((repository / 'src/NksAudioLink.Server/Dashboard.html').read_bytes() in application,
                'Embedded dashboard does not match the current source')
    else:
        for name in ('System.Windows.Forms.dll', 'NAudio.Core.dll', 'NAudio.Wasapi.dll'):
            require(name in entries, f'{path.name}: missing bundled Windows dependency {name}')
        if app == 'NksAudioLink.App':
            for name in ('wpfgfx_cor3.dll', 'PresentationFramework.dll'):
                require(name in entries, f'{path.name}: missing bundled WPF dependency {name}')
            icon = (repository / 'src/NksAudioLink.App/Assets/AudioLink.ico').read_bytes()
            require(icon in application, 'Embedded desktop icon does not match the current source')
    return {'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data),
            'frameworks': frameworks, 'embeddedFiles': len(entries)}


def package_layout(platform):
    public = set(PUBLIC_FILES)
    if platform == 'linux-x64':
        public |= LINUX_DEPLOY
    for tree in LICENSE_TREES[platform]:
        public |= {f'licenses/{tree}/LICENSE.TXT', f'licenses/{tree}/THIRD-PARTY-NOTICES.TXT'}
    required = public | set(EXECUTABLES[platform])
    symbols = {'NksAudioLink.Core.pdb', 'NksAudioLink.Server.pdb'} if platform == 'linux-x64' else {
        'NksAudioLink.App.pdb', 'NksAudioLink.Client.pdb', 'NksAudioLink.Core.pdb',
        'cli/NksAudioLink.Cli.pdb', 'cli/NksAudioLink.Client.pdb', 'cli/NksAudioLink.Core.pdb'}
    return public, required, required | symbols


def files_under(root):
    require(root.is_dir() and not root.is_symlink() and
            not getattr(root.lstat(), 'st_file_attributes', 0) & 0x400,
            f'Package root must be a real directory: {root}')
    result = {}
    # Explicit POSIX spelling gives Windows and Linux the same archive order.
    for path in sorted(root.rglob('*'), key=lambda item: item.relative_to(root).as_posix()):
        attributes = path.lstat()
        require(not path.is_symlink() and not getattr(attributes, 'st_file_attributes', 0) & 0x400,
                f'Links/reparse points are not allowed: {path.relative_to(root)}')
        if path.is_dir():
            continue
        require(path.is_file(), f'Nonregular package member: {path}')
        name = safe_name(path.relative_to(root).as_posix())
        require(name.casefold() not in {item.casefold() for item in result}, f'Duplicate path: {name}')
        result[name] = path
    return result


def file_hash(path):
    digest = hashlib.sha256()
    with path.open('rb') as content:
        while block := content.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def verify_directory(root, platform, repository=ROOT, runtime_version='9.0.20'):
    public, required, allowed = package_layout(platform)
    files = files_under(root)
    require(not required - files.keys(), f'Missing package files: {sorted(required - files.keys())}')
    require(not files.keys() - allowed, f'Unexpected/private package files: {sorted(files.keys() - allowed)}')
    for name in public:
        source = repository / ('deploy/' + name if name.startswith('licenses/') else name)
        require(source.is_file(), f'Missing public source file: {source}')
        require(files[name].read_bytes() == source.read_bytes(), f'Outdated or modified package file: {name}')
    if platform == 'linux-x64':
        config = json.loads(files['deploy/linux/server.json.example'].read_text(encoding='utf-8-sig'))
        require(config.get('pskBase64') in (None, ''), 'Example config contains a shared key')
        require(config.get('allowCidrs') == ['127.0.0.0/8'], 'Example config must allow only local clients')
        require(config.get('httpPort', 0) == 0, 'Example HTTP must be disabled by default')
        for name in ('deploy/linux/install.sh', 'deploy/linux/nks-audiolink.service'):
            require(b'\r' not in files[name].read_bytes(), f'Unix file has CRLF line endings: {name}')
    binaries = {name: verify_binary(files[name], platform, runtime_version, repository)
                for name in EXECUTABLES[platform]}
    hashes = {name: file_hash(path) for name, path in files.items()}
    return {'platform': platform, 'runtimeVersion': runtime_version, 'binaries': binaries, 'files': hashes}


def executable_member(name):
    return name == 'NksAudioLink.Server' or name.endswith('.exe') or name == 'deploy/linux/install.sh'


def create_archive(root, destination, platform):
    require(not destination.exists(), f'Archive already exists: {destination}')
    require(not destination.resolve().is_relative_to(root.resolve()),
            'Archive destination must be outside the package directory')
    destination.parent.mkdir(parents=True, exist_ok=True)
    files = files_under(root)
    if platform == 'windows-x64':
        with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, path in files.items():
                info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
                info.create_system = 3
                info.external_attr = (stat.S_IFREG | (0o755 if executable_member(name) else 0o644)) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(info, path.read_bytes(), compresslevel=9)
    else:
        with destination.open('wb') as output, gzip.GzipFile(filename='', mode='wb', fileobj=output, mtime=0) as gz:
            with tarfile.open(fileobj=gz, mode='w', format=tarfile.USTAR_FORMAT) as archive:
                for name, path in files.items():
                    info = tarfile.TarInfo(name)
                    info.size = path.stat().st_size
                    info.mode = 0o755 if executable_member(name) else 0o644
                    with path.open('rb') as content:
                        archive.addfile(info, content)


def extract_checked(archive_path, destination, platform=None):
    require(archive_path.stat().st_size <= MAX_PACKAGE_BYTES, 'Archive exceeds input size limit')
    seen = set()
    total_bytes = 0
    allowed = package_layout(platform)[2] if platform else None
    directories = {parent.as_posix() for name in allowed or ()
                   for parent in pathlib.PurePosixPath(name).parents if parent.as_posix() != '.'}

    def check_name(name, size, is_directory=False):
        nonlocal total_bytes
        while name.startswith('./'):
            name = name[2:]
        safe_name(name)
        require(name.casefold() not in seen, f'Duplicate archive member: {name}')
        seen.add(name.casefold())
        require(len(seen) <= MAX_ARCHIVE_MEMBERS, 'Archive contains too many members')
        require(0 <= size <= MAX_MEMBER_BYTES, f'Archive member exceeds size limit: {name}')
        if is_directory:
            require(size == 0, f'Archive directory contains data: {name}')
        total_bytes += size
        require(total_bytes <= MAX_PACKAGE_BYTES, 'Archive exceeds total size limit')
        if allowed is not None:
            require(name in (directories if is_directory else allowed),
                    f'Unexpected/private archive member: {name}')
        return name

    def save(name, content, mode, size):
        expected_mode = 0o755 if executable_member(name) else 0o644
        require(mode & 0o7777 == expected_mode,
                f'Incorrect archive permissions: {name} (expected {oct(expected_mode)})')
        path = destination / name
        path.parent.mkdir(parents=True, exist_ok=True)
        written = 0
        with path.open('wb') as output:
            while block := content.read(1024 * 1024):
                written += len(block)
                require(written <= size, f'Archive size mismatch: {name}')
                output.write(block)
        require(written == size, f'Archive size mismatch: {name}')
        path.chmod(mode & 0o777)
    if zipfile.is_zipfile(archive_path):
        with zipfile.ZipFile(archive_path) as archive:
            require(len(archive.infolist()) <= MAX_ARCHIVE_MEMBERS, 'Archive contains too many members')
            for item in archive.infolist():
                mode = item.external_attr >> 16
                if item.is_dir():
                    check_name(item.filename.rstrip('/'), item.file_size, True)
                    require(stat.S_ISDIR(mode), f'Nonregular archive directory: {item.filename}')
                    continue
                name = check_name(item.filename, item.file_size)
                require(stat.S_ISREG(mode), f'Nonregular archive member: {item.filename}')
                with archive.open(item) as content:
                    save(name, content, mode, item.file_size)
    else:
        with tarfile.open(archive_path, 'r:gz') as archive:
            for index, item in enumerate(archive):
                require(index < MAX_ARCHIVE_MEMBERS, 'Archive contains too many members')
                if item.isdir():
                    if item.name not in ('.', './'):
                        check_name(item.name.rstrip('/'), item.size, True)
                    continue
                name = check_name(item.name, item.size)
                require(item.isfile(), f'Nonregular archive member: {item.name}')
                with archive.extractfile(item) as content:
                    save(name, content, item.mode, item.size)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('package', type=pathlib.Path, help='Publish directory, ZIP, or tar.gz')
    parser.add_argument('--platform', choices=EXECUTABLES, required=True)
    parser.add_argument('--repository', type=pathlib.Path, default=ROOT)
    parser.add_argument('--runtime-version', default='9.0.20')
    parser.add_argument('--create-archive', type=pathlib.Path, help='New destination; refuses existing files')
    parser.add_argument('--report', type=pathlib.Path, help='Write verified file hashes and runtime inventory as JSON')
    args = parser.parse_args()
    try:
        with tempfile.TemporaryDirectory(prefix='nks-release-check-') as temporary:
            root = args.package
            if not root.is_dir():
                extract_checked(root, pathlib.Path(temporary), args.platform)
                root = pathlib.Path(temporary)
            report = verify_directory(root, args.platform, args.repository, args.runtime_version)
            if args.create_archive:
                create_archive(root, args.create_archive, args.platform)
                with tempfile.TemporaryDirectory(prefix='nks-release-roundtrip-') as roundtrip:
                    extract_checked(args.create_archive, pathlib.Path(roundtrip), args.platform)
                    unpacked = verify_directory(pathlib.Path(roundtrip), args.platform, args.repository, args.runtime_version)
                    require(unpacked == report, 'Archive round-trip content mismatch')
                report['archive'] = {'name': args.create_archive.name, 'sha256': file_hash(args.create_archive)}
            if args.report:
                args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
            print(json.dumps({'result': 'PASS', 'platform': args.platform, 'files': len(report['files']),
                              'binaries': report['binaries'], 'archive': report.get('archive')}, indent=2))
    except (OSError, ValueError, KeyError, RuntimeError, struct.error, zlib.error, zipfile.BadZipFile, tarfile.TarError) as error:
        parser.exit(1, f'Release verification failed: {error}\n')


if __name__ == '__main__':
    main()
