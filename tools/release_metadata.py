#!/usr/bin/env python3
"""Read the source SemVer and reject release tags that do not match it exactly."""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import pathlib
import re
from urllib.parse import quote
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
NUMBER = r'(?:0|[1-9][0-9]*)'
IDENTIFIER = r'(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
SEMVER = re.compile(
    rf'{NUMBER}\.{NUMBER}\.{NUMBER}'
    rf'(?:-(?P<prerelease>{IDENTIFIER}(?:\.{IDENTIFIER})*))?'
    r'(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?')


def release_metadata(props: pathlib.Path, tag: str | None = None):
    tree = ET.parse(props)
    versions = [node for node in tree.iter() if node.tag.rsplit('}', 1)[-1] == 'Version']
    if len(versions) != 1:
        raise ValueError('Directory.Build.props must contain exactly one explicit Version.')
    node = versions[0]
    parents = {child: parent for parent in tree.iter() for child in parent}
    if node.get('Condition') or parents.get(node, node).get('Condition'):
        raise ValueError('The release Version must be unconditional.')
    version = (node.text or '').strip()
    match = SEMVER.fullmatch(version)
    if not match:
        raise ValueError(f'Invalid release SemVer: {version!r}. Use MAJOR.MINOR.PATCH[-PRERELEASE].')
    expected_tag = 'v' + version
    if tag and tag != expected_tag:
        raise ValueError(f'Release tag {tag!r} does not match source Version {version!r}; expected {expected_tag!r}.')
    return {'version': version, 'tag': expected_tag, 'prerelease': match.group('prerelease') is not None}


def source_revision(value):
    if not re.fullmatch(r'(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})', value):
        raise ValueError('Source revision must be a full Git commit hash.')
    return value.lower()


def file_hash(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        while block := source.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def build_manifest(metadata, revision, assets, tag=None):
    manifest = {'productVersion': metadata['version'], 'sourceRevision': source_revision(revision),
                'releaseTag': tag or None, 'prerelease': metadata['prerelease'], 'artifacts': {}}
    for platform, archive in [('windows-x64', 'nks-audiolink-windows-x64.zip'),
                              ('linux-x64', 'nks-audiolink-linux-x64.tar.gz')]:
        report_name = platform.split('-')[0] + '-package-verification.json'
        report = json.loads((assets / report_name).read_text(encoding='utf-8'))
        digest = file_hash(assets / archive)
        if report['platform'] != platform or report['archive']['name'] != archive or report['archive']['sha256'] != digest:
            raise ValueError(f'Package verification report does not match downloaded archive: {archive}')
        for name in (archive, report_name):
            manifest['artifacts'][name] = {'sha256': file_hash(assets / name), 'bytes': (assets / name).stat().st_size}
    manifest['artifacts']['SHA256SUMS'] = {'sha256': file_hash(assets / 'SHA256SUMS'),
                                         'bytes': (assets / 'SHA256SUMS').stat().st_size}
    return manifest


def changelog_notes(path, version):
    text = path.read_text(encoding='utf-8-sig')
    headings = list(re.finditer(r'^##[ \t]+[^\r\n]+$', text, re.MULTILINE))
    heading = re.compile(rf'##\s+(?:{re.escape(version)}|\[{re.escape(version)}\])\s+[—–-]\s+(\d{{4}}-\d{{2}}-\d{{2}})\s*')
    matches = [(index, heading.fullmatch(item.group())) for index, item in enumerate(headings)]
    matches = [(index, match) for index, match in matches if match]
    if len(matches) != 1:
        raise ValueError(f'CHANGELOG.md must have exactly one dated section for {version}.')
    index, match = matches[0]
    datetime.date.fromisoformat(match.group(1))
    start = headings[index].start()
    end = headings[index + 1].start() if index + 1 < len(headings) else len(text)
    if not text[headings[index].end():end].strip():
        raise ValueError(f'Changelog section for {version} is empty.')
    return text[start:end].strip() + '\n'


def link_release_notes(notes, repository, tag):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise ValueError('Repository must be OWNER/NAME.')
    base = f'https://github.com/{repository}/blob/{quote(tag, safe="")}/'
    # Changelog links are relative to the checkout; release pages need explicit
    # source links to keep validation and known-limitations references usable.
    return re.sub(r'\]\((?![a-zA-Z][a-zA-Z0-9+.-]*:|#|/)([^)]+)\)',
                  lambda match: '](' + base + quote(match.group(1), safe='/#') + ')', notes)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--props', type=pathlib.Path, default=ROOT / 'Directory.Build.props')
    parser.add_argument('--tag', help='Validate a pushed tag; an empty value means artifact-only mode.')
    parser.add_argument('--github-output', type=pathlib.Path, help='Append validated version/prerelease outputs.')
    parser.add_argument('--source-revision', help='Full Git hash used to build these assets.')
    parser.add_argument('--build-manifest', type=pathlib.Path, help='Write version, commit and verified asset hashes.')
    parser.add_argument('--assets', type=pathlib.Path, help='Downloaded archives, verification reports and SHA256SUMS.')
    parser.add_argument('--notes-file', type=pathlib.Path, help='Write the exact dated changelog section for this version.')
    parser.add_argument('--changelog', type=pathlib.Path, default=ROOT / 'CHANGELOG.md')
    parser.add_argument('--repository', help='OWNER/NAME for source links in release notes.')
    parser.add_argument('--run-url', help='Optional workflow link appended to release notes.')
    args = parser.parse_args()
    try:
        metadata = release_metadata(args.props, args.tag)
        revision = source_revision(args.source_revision) if args.source_revision else None
        if args.build_manifest and (not revision or not args.assets):
            raise ValueError('--build-manifest requires --source-revision and --assets.')
        manifest = build_manifest(metadata, revision, args.assets, args.tag) if args.build_manifest else None
        notes = changelog_notes(args.changelog, metadata['version']) if args.notes_file else None
        if notes and args.repository:
            notes = link_release_notes(notes, args.repository, metadata['tag'])
            if revision:
                notes += f'\nBuild commit: [`{revision}`](https://github.com/{args.repository}/commit/{revision}).\n'
            if args.run_url:
                if not re.fullmatch(r'https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/actions/runs/[0-9]+', args.run_url):
                    raise ValueError('Workflow link must be a GitHub Actions run URL.')
                notes += f'\n[Build and verification run]({args.run_url}). '
            notes += f'[Build manifest](https://github.com/{args.repository}/releases/download/{quote(metadata["tag"], safe="")}/release-build.json).\n'
        if args.build_manifest:
            args.build_manifest.write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        if args.notes_file:
            args.notes_file.write_text(notes, encoding='utf-8', newline='\n')
        if args.github_output:
            with args.github_output.open('a', encoding='utf-8', newline='\n') as output:
                output.write(f'version={metadata["version"]}\nprerelease={str(metadata["prerelease"]).lower()}\n')
                if revision:
                    output.write(f'source_revision={revision}\n')
        if revision:
            metadata['sourceRevision'] = revision
        print(json.dumps(metadata))
    except (OSError, ValueError, KeyError, ET.ParseError) as error:
        parser.exit(1, f'Release version validation failed: {error}\n')


if __name__ == '__main__':
    main()
