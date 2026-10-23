# Versioning and releases

The product version is set once in [Directory.Build.props](../Directory.Build.props). The current candidate is **0.1.0-rc.1**. Builds of Core, client, desktop, CLI and server use this version.

Desktop About and the HTTP monitor retain the prerelease suffix. About's version tooltip and `NksAudioLink.Cli.exe --version` / `NksAudioLink.Server --version` also expose the informational build identity, including commit metadata when supplied by the build. Assembly/file versions are numeric .NET metadata and cannot represent `-rc.1`; use the product version when identifying a release.

## Version and tag policy

- Use `MAJOR.MINOR.PATCH` product versions and `-rc.N` for release candidates. The matching Git tag adds only a `v` prefix: `0.1.0-rc.1` → `v0.1.0-rc.1`.
- Each published candidate is immutable. Further changes require a new candidate such as `0.1.0-rc.2`; do not move a published tag or replace its assets with binaries from another commit.
- Promote to `0.1.0` only after the acceptance gates below pass. Subsequent compatible fixes advance the patch version; new functionality advances the minor version. Document any breaking changes explicitly, especially while the project remains below 1.0.
- Use Conventional Commits for reviewable changes. A source commit identifies an iteration; a version tag identifies a published product candidate or stable release. Commit metadata does not prove that a developer's working tree was clean, so published artifacts come from CI checkouts.

## What CI publishes

| Trigger | Result |
|---|---|
| Pull request | Test matrix and regression checks; no release publication |
| Successful main-branch CI | Downloadable Windows/Linux packages after the complete SDK 9.0.203/9.0.318 test matrix passes |
| Manual Release workflow | Tested, verified downloadable artifacts; no GitHub Release |
| Matching `v*` tag | Tested, verified packages and a GitHub Release; hyphenated prerelease versions are marked **Prerelease** |

The release workflow rejects a tag that does not exactly match `v` plus the version in `Directory.Build.props`. Packaging uses SDK 9.0.318 and checks the actual embedded runtime versions. All triggers share the packaging workflow so that main-branch downloads and tagged releases use the same package checks.

Published files include:

- `nks-audiolink-windows-x64.zip`
- `nks-audiolink-linux-x64.tar.gz`
- `windows-package-verification.json` and `linux-package-verification.json`
- `SHA256SUMS`
- `release-build.json`, binding the product version and source commit to the archive/report/checksum hashes

The archives include the MIT license, third-party notices and runtime licenses. The verifier rejects unexpected private files, invalid archive paths and inconsistent file contents/permissions. On Linux, use `sha256sum -c SHA256SUMS` beside the downloaded archives. On Windows, compare `Get-FileHash -Algorithm SHA256` results with the manifest.

Before tag publication, the workflow recomputes and checks the build manifest. Release notes come from the exact dated changelog section, including its known pending checks, and link the source commit, documentation, workflow and manifest.

## Preparing a candidate

1. Update the version in `Directory.Build.props`, the README version badge and the dated changelog entry together. Record known limitations and the corresponding evidence in [VALIDATION.md](VALIDATION.md) and [TODO.md](../TODO.md).
2. Commit and push the reviewed changes to main. Wait for the full CI matrix and packaging jobs for that exact commit; inspect both downloadable archives and their verification reports. A successful older commit is not evidence for a newer candidate.
3. Confirm that the package's `--version` output and desktop About match the intended candidate. Keep credentials, local settings, machine inventory and third-party driver binaries out of the repository and archives.
4. Create an annotated tag on the verified commit and push that tag. For example, replace `VERIFIED_COMMIT` with the exact reviewed commit hash:

   ```sh
   git tag -a v0.1.0-rc.1 VERIFIED_COMMIT -m "NKS AudioLink 0.1.0-rc.1"
   git push origin v0.1.0-rc.1
   ```

5. Check that the tag workflow passes, publishes both platforms and labels the candidate as a prerelease. Archive the workflow link and hashes with the validation evidence.

A release candidate can document incomplete manual acceptance checks. It must not claim those checks passed.

## Stable-release acceptance

Before creating `v0.1.0`, review every open acceptance item in [TODO.md](../TODO.md), with evidence for the actual revision being released. In particular, the owner still needs to assess flash/click synchronization and complete the physical ten-second network-cable disconnection/reconnection test. The existing controlled UDP outage and generated reference video do not prove those observations.

Retain evidence for the build/test matrix, Core coverage gate, verified packages, real WASAPI/ALSA operation, a ten-minute hardware stability/CPU run, restart/sleep recovery, busy rejection/takeover and virtual-device restoration. Earlier successful or failing runs remain historical evidence and must not be silently relabeled as a test of new code.

CI enforces tag/version consistency and package verification. It cannot perform the owner's listening or physical cable checks; those results must be recorded before the stable tag is created. After the gates pass, remove the prerelease suffix, update the changelog and repeat CI/package verification for the new stable-version commit before tagging it.
