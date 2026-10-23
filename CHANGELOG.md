# Changelog

## 0.1.0-rc.3 — 2026-10-23

Follow-up to minimize the adaptive target while preserving predictive protection and hysteresis. Earlier candidate tags and packages remain immutable.

### Fixed

- On a quiet link with an estimated need around 16 ms, RC2 could stop at 25 ms instead of reaching the negotiated 20 ms minimum. Decreases now require the next 5 ms step to cover the estimate, which already includes its safety margin.
- Learned failure protection now renews only when recent tail delay threatens that next target. Harmless jitter cannot indefinitely renew the hold at a narrowly capped target.
- Clarified the four capture modes and labeled the desktop screenshot as controlled example data.

### Preserved

- Thirty seconds of clean playback before each downward step, three confirmations before a predictive increase, the two-second increase cooldown and the 120-second learned post-failure hold.
- Manual/legacy compatibility, 20–100 ms adaptive bounds, visible target/history and live audio controls.

### Validation

- All 171 portable and 57 Windows client/CLI tests passed, with 16 dashboard and 36 package/version tests. Core coverage is 96.52% of lines and 88.98% of branches.
- Three new regressions fail against RC2 and pass with this correction: gradual minimum-target convergence with nonzero jitter, eventual settling after one loss, and expiration of a learned hold under a narrow 20–25 ms bound. Existing oscillation and recurring-delay protection tests remain unchanged and pass.

### Known pending manual checks

- The owner's flash/click synchronization assessment and physical ten-second network cable recovery check remain pending for a stable release.
- Physical end-to-end latency remains unmeasured; displayed delay is an estimate.

See [VALIDATION.md](docs/VALIDATION.md) for recorded tests and deployment evidence.

## 0.1.0-rc.2 — 2026-10-23

Second release candidate, focused on the complete desktop workflow and predictive buffering. RC1 remains an immutable historical release.

### Added

- Prominent buffer mode and live target controls, with a time history that distinguishes the target from actual occupancy. Manual controls are absent in adaptive mode; automatic initialization is independent of the saved manual target.
- Predictive adaptive buffering based on packet timing, with hysteresis, bounded changes and conservative stable recovery. Explicit capability negotiation distinguishes it from RC1's reactive mode.
- Live mute that preserves gain, optional stereo-linked peak limiting, and an explicit takeover action after a busy receiver rejects the connection.

### Changed

- Rebuilt the desktop layout: primary receiver/source/buffer/audio controls remain visible at 800×640 and 960×720. Advanced settings and activity use separate panels. Automatic mode hides manual presets and timing; its independent initial target is 40 ms within the configured bounds.
- A prominent observed target, last-change reason and two-minute graph distinguish the target from real occupancy. A timeout clears current metrics and pauses history; resumption starts a new graph segment and refreshes its status even when the target did not change.
- Predictive buffering uses a bounded 512-packet timing window, robust delay percentiles and a limited rising-risk forecast. Three high-risk assessments, a deadband and a two-second increase cooldown limit reactions. Decreases require 30 seconds of clean low-risk playback; a learned 120-second hold after a real failure prevents repeatedly returning to an unsafe target.
- New clients explicitly request predictive capability with HELLO format `0x82` and require STATS marker `0xA2`. New servers retain manual and RC1 reactive compatibility; older servers reject the new mode with an update/manual-mode explanation.
- Gain and mute transitions ramp over 5 ms. The optional sample-peak limiter links stereo channels, adds 1 ms lookahead and uses a 50 ms release. Delay estimates include its processing delay, and meters represent actual post-processing PCM. Stream interruptions discard delayed old audio before reconnection.

### Validation

- The combined Release check passed 168 portable tests and 57 Windows client/CLI tests, plus 16 dashboard tests and 36 package/version tests. Core coverage was 96.52% of lines and 88.84% of branches.
- Deterministic predictor tests cover proactive increases, outliers, drift, reorder/duplicates/loss, repeated rare delays, hysteresis, eventual settling and waveform continuity. Native UI checks cover both minimum/default sizes, all source modes, busy rejection, keyboard focus, advanced panels and resumed graphs.
- Limiter tests cover all PCM16 values at seven static bypass gains, stereo linking, peak bounds, delay/tail handling, live transitions and zero steady processing allocations. Real UDP regressions verify transmitted tails, mute, metering and stale-data exclusion.

### Known pending manual checks

- The owner's flash/click synchronization assessment and physical ten-second network cable recovery check remain pending for a stable release.
- Physical end-to-end latency remains unmeasured; displayed delay is an estimate.

See [VALIDATION.md](docs/VALIDATION.md) for recorded results and their scope.

## 0.1.0-rc.1 — 2026-10-23

First release candidate. The remaining listening and physical cable checks below gate the stable `0.1.0` release; they do not prevent publishing an explicitly marked prerelease.

### Added

- C#/.NET 9 Linux receiver with direct ALSA output, WAV/null test outputs, a systemd service and an installer.
- Uncompressed stereo UDP PCM, optional HMAC authentication, jitter buffering and audio clock drift correction.
- Windows WASAPI playback/input capture and continuous silent frames that preserve the audio timeline.
- Compact borderless WPF app with native resizing, waveform app/tray icons, settings, IPv4 server discovery, About, activity history and graceful Quit. Connection actions, live measurements and stereo meters remain visible at the minimum window size.
- Copy diagnostics from the desktop: product/build version, non-secret stream settings, current statistics/levels and the last 50 activity entries. Host/device/user identifiers and shared keys are removed; nothing is copied until the button is clicked.
- Manual target-buffer presets: Low latency 20 ms, Balanced 40 ms and Robust 80 ms, with custom slider values. The initial 30 ms default and saved targets are preserved; presets require disconnection and do not automatically adjust buffering.
- Opt-in adaptive receiver buffering with configurable 20–100 ms bounds. Stream errors raise the target by up to 10 ms with a two-second increase cooldown; 30 seconds of clean playback lowers it by 5 ms. The existing bounded drift resampler adjusts occupancy without reconnecting or deliberately dropping frames. Client, CLI diagnostics and dashboard distinguish the actual target from measured buffer occupancy. Manual mode remains the default.
- Per-application Windows capture includes the selected process tree and excludes other processes, using native process loopback through NAudio. The UI provides a process selector; CLI supports `processes` and `send --process PID`. Selection is pinned to the process instance; exiting or reusing its PID requires an explicit new selection. No extra driver or default-output change is required for this mode.
- Adaptive negotiation preserves ordinary HELLO/STATS packets. Older receivers reject the adaptive format with an actionable instruction to update or select manual mode. The client sends no PCM before receiving a matching adaptive acknowledgment.
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
- Actual process isolation passed on Windows 10 build 19045 using two controlled audio processes on a virtual endpoint: the selected parent's 440 Hz child was captured, the unrelated 990 Hz process was approximately 99 dB lower, silence followed the selected child's pause, and parent exit terminated capture. This is a test-fixture result, not a universal isolation or compatibility guarantee.
- Before adding server-owned history and the native-counter API, a fresh 600.002-second physical ALSA run at a 40 ms target kept one process/session and an unchanged ALSA trigger/owner. All 121 ALSA samples were RUNNING, jitter counters stayed zero, no native recovery events occurred, and server CPU averaged **1.260% of one core**. This is evidence for that earlier deployment; the new monitoring features have separate automated/browser validation and still need their own deployed measurement. Earlier interrupted or failing runs are recorded separately.
- Real Windows S3 sleep/wake recovery, server restart, busy rejection, takeover and idle ALSA release were exercised. Automatic S3 reconnection took **11.72 seconds after the Windows wake event**; server restarts recovered within five seconds.
- Windows-to-Linux WAV analysis verified the 440 Hz test signal. Actual client/server meters responded to live audio; the client meter test compares post-gain values against transmitted PCM and verifies silence/reset behavior.
- Self-contained Windows CLI and Linux server startup, release archive contents and runtime 9.0.20 were verified. SDK/build, native window behavior, virtual-device restoration and hardware evidence are detailed in [VALIDATION.md](docs/VALIDATION.md).

### Known pending manual checks

- The owner must assess synchronization using the generated flash/click video.
- Physical disconnection and reconnection of the PC network cable for ten seconds remains unverified. A controlled UDP outage is covered, but is not a substitute for that test.
- Independent physical end-to-end latency has not been measured. Displayed latency remains an estimate excluding receiver and speaker delay.

See [TODO.md](TODO.md) for acceptance status and [RELEASING.md](docs/RELEASING.md) for version/tag rules.
