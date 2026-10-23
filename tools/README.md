# Release package verification

`verify_release.py` checks an AudioLink publish directory or archive. It uses only
the Python 3.10+ standard library. Run it from a source checkout after publishing
the binaries and copying the public documentation and licenses. The release
workflow in [`.github/workflows/package.yml`](../.github/workflows/package.yml)
contains the complete staging commands.

On Windows, use `py -3` instead of `python3` if needed.

```sh
# Inspect a staged package without creating an archive.
python3 tools/verify_release.py publish/windows --platform windows-x64

# Verify, create an archive, extract it, and verify the extracted bytes again.
python3 tools/verify_release.py publish/linux --platform linux-x64 --create-archive artifacts/nks-audiolink-linux-x64.tar.gz --report artifacts/linux-package-verification.json

# Check a downloaded archive against this source checkout.
python3 tools/verify_release.py artifacts/nks-audiolink-windows-x64.zip --platform windows-x64

# Run package and version/release-metadata regression tests.
python3 -m unittest discover -s tests -p "test_release*.py"
```

Place archive and report outputs outside the publish directory. Archive creation
refuses to overwrite an existing file. A successful check exits with code 0 and
prints a short JSON result; a failed check exits with code 1. The optional report
records each packaged file's SHA-256, binary sizes, bundled framework versions,
and the archive hash when an archive was created.

## What is checked

- Required public files are present and unexpected files are rejected. Public
  documentation, licenses and Linux deployment files must exactly match the
  supplied source checkout. The default is the repository containing this
  script; `--repository PATH` selects another one.
- The example server configuration contains no shared key, allows only local
  clients, and disables HTTP by default. Private configuration files are outside
  the package allowlist.
- Executables have the expected x64 PE or ELF format and a readable .NET
  single-file bundle. Embedded runtime configuration must declare the expected
  self-contained frameworks at version `9.0.20`; `--runtime-version VERSION`
  explicitly changes this requirement. Required managed/native dependencies must
  be present. The embedded server dashboard and desktop icon must match their
  source assets.
- Archive paths, file types and permissions are checked before writing members.
  Traversal, Windows path aliases, duplicates, links, unexpected members and
  excessive sizes are rejected. Limits are 256 archive members, 512 MiB per file,
  and 1 GiB total. Extraction uses a temporary directory.
- An archive created by this tool is extracted and checked again. Its file
  hashes and binary inventory must equal the verified input directory.

These checks inspect package contents and embedded metadata. They do not prove
that every assembly was built from the supplied source revision, authenticate a
publisher, replace a signed provenance record, or run the application. Runtime,
hardware and integration tests remain separate release requirements.

## Repeatable archive output

Archive creation uses an explicit file order, fixed timestamps and permissions,
and normalized TAR ownership. Identical input bytes produce identical archive
bytes with the same Python/compression implementation, regardless of input file
modification times. This is a property of the packaging step. It does not claim
that rebuilding the application produces identical binaries or that different
compression-library versions produce identical compressed output.

## Updating the package layout

`PUBLIC_FILES`, `LINUX_DEPLOY`, `LICENSE_TREES` and `package_layout()` define the
public allowlist in `verify_release.py`. Required files must be staged; listed
debug symbols are optional. When adding a public release file:

1. Review its contents and decide which platforms should include it.
2. Update the explicit allowlist and the corresponding release-workflow copy
   commands together. Do not add a broad directory wildcard to the verifier.
3. Update relevant regression tests, then verify both staged platform packages.

When changing the bundled runtime, update the publish SDK/runtime choice,
third-party license files and notices, the verifier's expected runtime, and its
tests together. Re-run verification against newly published binaries.

## Version, release notes and build identity

`release_metadata.py` reads the explicit SemVer in `Directory.Build.props`.
`--tag v0.1.0-rc.1` rejects a mismatch before the release jobs run. The helper
classifies prereleases, exports validated GitHub Actions outputs, and can write
the exact dated changelog section with `--notes-file PATH`. Missing, duplicate or
undated version sections fail validation rather than falling back to commit lists.

The packaging workflow writes a separate `release-build.json` with product
version, full source commit and archive/report/checksum hashes. It compares each
archive against its verification report. The publication job recomputes this
manifest and compares it with the downloaded one before uploading assets. This
records CI build identity; it is not a signed provenance attestation.
