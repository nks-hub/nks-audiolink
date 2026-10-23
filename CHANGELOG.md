# Changelog

## 0.1.0-rc.1 — 2026-10-23

First release candidate. The remaining listening and physical cable checks below gate the stable `0.1.0` release; they do not prevent publishing an explicitly marked prerelease.

### Added

- C#/.NET 9 Linux receiver with direct ALSA output, WAV/null test outputs, a systemd service and an installer.
- Uncompressed stereo UDP PCM, optional HMAC authentication, jitter buffering and audio clock drift correction.
- Windows WASAPI playback/input capture and continuous silent frames that preserve the audio timeline.
- Compact borderless WPF app with native resizing, waveform app/tray icons, settings, IPv4 server discovery, About, activity history and graceful Quit. Connection actions, live measurements and stereo meters remain visible at the minimum window size.
- Copy diagnostics from the desktop: product/build version, non-secret stream settings, current statistics/levels and the last 50 activity entries. Host/device/user identifiers and shared keys are removed; nothing is copied until the button is clicked.
- Manual target-buffer presets: Low latency 20 ms, Balanced 40 ms and Robust 80 ms, with custom slider values. The initial 30 ms default and saved targets are preserved; presets require disconnection and do not automatically adjust buffering.
- Support for a separately installed VB-CABLE driver. Windows default-output roles are restored on disconnect and on app restart after a crash; the driver is not bundled.
- CLI device listing, server discovery, streaming and a 440 Hz test tone. CLI and server `--version` identify the product version and build without opening audio devices or sockets.
- Optional read-only HTTP dashboard and JSON statistics API, disabled by default and restricted by `allowCidrs`. The desktop launcher uses a separate HTTP port, **7356** by default.
- Stereo signal meters in the client and server: 300 ms RMS, one-second held peaks and clipping indication. Server-owned history retains up to 3,600 samples at one-second intervals while HTTP monitoring is enabled; 1/5/15/60-minute signal, buffer and error graphs survive a page refresh. An epoch/cursor API supports incremental reads; server restart clears the in-memory history.
- Native ALSA recovery totals and per-session baseline counters appear separately from jitter-buffer counters. Fatal output errors remain available after the session closes.
- A shared prerelease version in build metadata, desktop About and server monitoring, with commit metadata retained for build identification.
- Windows x64 and Linux x64 packages with .NET runtime 9.0.20, MIT/third-party licenses and public documentation. Package verification checks a public-file allowlist, embedded runtimes, license content, extraction permissions and round-trip hashes.
- CI tests SDK 9.0.203 and release SDK 9.0.318. Successful main-branch CI builds downloadable packages; version tags publish releases, with `-rc.N` versions marked as prereleases.
- English documentation, controls, tooltips, CLI help and actionable error messages.

### Fixed

- **NETSDK1141:** local builds accept SDK 9.0.203 and newer stable SDKs within .NET 9 instead of requiring SDK 9.0.318.
- Reject misaligned sample indices before jitter buffering and reject target buffers that are not multiples of 5 ms before opening the audio output.
- Recover jitter buffering after consecutive losses, long gaps and interrupted startup; retain reordered startup packets within the ring window.
- Correct client acceptance/timeout races, slow-start state ordering, long-running sender timer overflow, cancellation and late capture callbacks.
- Prevent immediate server rejection during real WASAPI startup from hanging capture disposal. Rejected clients stop and require a new connection action.
- Recreate a UDP socket invalidated by Windows sleep/resume (error 10022), preserving normal error handling for unrelated invalid-argument failures.
- Apply explicit client/server CLI options after configuration-file values regardless of argument order. Rejected test tones stop promptly and release their sessions.
- Clear RMS, held peaks and clipping at connection transitions, including response timeout and quiet reconnection.
- Preserve output frames across short server scheduling delays. Recovery after long pauses is bounded to four due frames; diagnostics report maximum lateness and discarded deadlines.
- Log native ALSA write errors and recovery outcomes separately from network/jitter counters. Cumulative underrun/suspend totals survive rate limiting, and diagnostic-write failures cannot stop playback.

### Validation

- Automated coverage includes portable protocol/server/HTTP/meter tests, Windows client/CLI integration tests, browser regressions and package-verifier tests. Exact current counts, coverage and CI runs are recorded in [VALIDATION.md](docs/VALIDATION.md).
- Diagnostic-report regressions check identity/key redaction, bounded recent activity, version/measurement output and unavailable data after disconnection.
- Before adding server-owned history and the native-counter API, a fresh 600.002-second physical ALSA run at a 40 ms target kept one process/session and an unchanged ALSA trigger/owner. All 121 ALSA samples were RUNNING, jitter counters stayed zero, no native recovery events occurred, and server CPU averaged **1.260% of one core**. This is evidence for that earlier deployment; the new monitoring features have separate automated/browser validation and still need their own deployed measurement. Earlier interrupted or failing runs are recorded separately.
- Real Windows S3 sleep/wake recovery, server restart, busy rejection, takeover and idle ALSA release were exercised. Automatic S3 reconnection took **11.72 seconds after the Windows wake event**; server restarts recovered within five seconds.
- Windows-to-Linux WAV analysis verified the 440 Hz test signal. Actual client/server meters responded to live audio; the client meter test compares post-gain values against transmitted PCM and verifies silence/reset behavior.
- Self-contained Windows CLI and Linux server startup, release archive contents and runtime 9.0.20 were verified. SDK/build, native window behavior, virtual-device restoration and hardware evidence are detailed in [VALIDATION.md](docs/VALIDATION.md).

### Known pending manual checks

- The owner must assess synchronization using the generated flash/click video.
- Physical disconnection and reconnection of the PC network cable for ten seconds remains unverified. A controlled UDP outage is covered, but is not a substitute for that test.
- Independent physical end-to-end latency has not been measured. Displayed latency remains an estimate excluding receiver and speaker delay.

See [TODO.md](TODO.md) for acceptance status and [RELEASING.md](docs/RELEASING.md) for version/tag rules.
