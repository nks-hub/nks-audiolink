# NKS AudioLink progress

A phase is checked only after its implementation and required verification are complete.

The 2026-10-23 review adds SDK 9.0.203 compatibility, packet-loss and lifecycle fixes, a borderless desktop UI, signal meters, manual buffer presets, redacted diagnostic copy, and an optional HTTP dashboard with one-hour server history. Local automated coverage now includes 106 portable tests, 32 Windows client/CLI tests, 14 dashboard tests and 36 package/version tests. Core line coverage was remeasured at 90.57% (81.38% branches). See [candidate review evidence](docs/VALIDATION.md#release-candidate-verification-2026-10-23). Historical phase counts below describe the earlier deployment; the current acceptance checklist records the new verification separately.

- [x] 0. Solution scaffold, local build and tests, public GitHub repository, and passing Windows/Linux CI. Verified on 2026-10-02.
- [x] 1. Core protocol, HMAC, jitter buffer, resampling and clock drift tests. The portable Core/server/queue suite has 39 passing tests; Core line coverage is 86.93%. Drift simulation covers ±200 ppm over ten minutes of virtual time.
- [x] 2. UDP server with WAV and null outputs. A Windows-to-Linux 440 Hz test produced 5.095 seconds of WAV audio, RMS 8,484, and no overruns.
- [x] 3. ALSA output, systemd deployment and a ten-minute hardware run. After fixing skipped output deadlines and adding native recovery logging, the 2026-10-23 deployment passed a fresh 600.002-second run at a 40 ms target: one process/session, all 121 ALSA samples RUNNING with one unchanged trigger/owner, zero jitter counters, no native recovery events and 1.260% of one CPU core. Earlier error/interrupted runs remain documented separately in VALIDATION.md.
- [x] 4. Windows WASAPI client, continuous silent frames and real playback. Captured tone/WAV analysis and live PC-to-ALSA streaming passed. The owner confirmed clean receiver playback.
- [ ] 5. Latency checks specified by the no-microphone test plan, targeting an estimate below 100 ms. The app estimated 71–82 ms during the ten-minute stream. Nine Windows-to-Linux LAN clicks took 22.96–37.88 ms, median 31.39 ms, from UDP send to WAV write. Earlier Linux loopback measurements were 27.36–28.69 ms, median 28.22 ms. The owner still needs to assess flash/click synchronization. Physical end-to-end latency remains unmeasured.
- [x] 6. WPF app, system tray, settings, server discovery and screenshots. Connect/disconnect against a WAV server, live statistics, gain changes while streaming, discovery of the deployed service and a screenshot in `docs/screenshots` were verified.
- [x] 7. Virtual-device mode and Windows default-output restoration. The owner authorized a separate signed VB-CABLE installation, completed without restarting the test PC. A 440 Hz CABLE Input → CABLE Output → UDP WAV stream passed. All three Windows default-output roles were restored on disconnect and on app restart after a simulated crash. The driver is not bundled.
- [ ] 8. Recovery after network loss, server restart and PC sleep/resume; session takeover. Real server restarts recovered within five seconds. A real Windows S3 failure was reproduced and fixed by recreating the invalidated UDP socket; the production WASAPI client then reconnected automatically 11.72 s after the Windows wake event. Busy rejection, takeover, capture cleanup and ALSA release were checked. Controlled ten-second UDP outages and sender-clock discontinuities pass. Physical cable disconnection remains unverified.
- [ ] 9. Release workflow, complete README/changelog and verified packages. Windows and Linux packages have been built and inspected; the packaged Windows CLI and Linux server start successfully. Manual workflow runs build artifacts without publishing a release. Tag `v0.1.0` remains pending until the hardware checks pass.

## Current acceptance checklist

- [ ] Deliver version `0.1.0-rc.1` with a dated changelog, consistent app/API/CLI version reporting and verified binary artifacts from successful main-branch CI. Keep the remaining human checks explicit in the candidate release.
- [x] Store the last hour of numeric monitoring history on the server, restore charts after browser refresh and offer 1/5/15/60-minute ranges. Memory history is bounded and contains no audio or credentials; restart clears it.
- [x] Show native ALSA output errors/recoveries separately from network/jitter counters in the HTTP API and dashboard, retaining closed-session errors in lifetime totals.
- [x] Add a client diagnostic-copy action with version, safe settings, current measurements and recent events; exclude shared keys and redact identifying addresses/device labels.
- [x] Add manual 20/40/80 ms buffer presets with a custom value, preserving the 30 ms initial default and saved settings; verify compiled handlers and minimum-window layout.
- [x] Expand the primary-source competitor comparison into [COMPETITION.md](docs/COMPETITION.md), connecting findings to implemented work, acceptance checks and separately scoped future features.
- [x] Merge the newer upstream README and MIT licensing changes; update About and the package workflow to include the MIT notice.
- [x] Rerun portable and Windows client tests, including sender recovery after a scheduler discontinuity; remeasure Core coverage above 80%.
- [x] Verify actual WASAPI capture through an isolated ten-second UDP outage, owned receiver restart and a 200 ms sender pause. These tests do not substitute for physically unplugging the cable or suspending Windows.
- [x] Deploy the updated server with a rollback backup; verify the live HTTP monitor and recovery of the existing client after service replacement (1.197 s).
- [x] Verify the updated desktop app against the deployed ALSA receiver and repeat the ten-minute stability/CPU measurement: 600.002 seconds, zero jitter/native recovery events, unchanged process/session/ALSA trigger and 1.260% of one core.
- [x] Verify live busy rejection, takeover and ALSA device release (approximately five seconds after the last packet).
- [x] Verify a real Windows sleep/wake cycle and automatic reconnection; record the 11.72 s recovery and actual power-button wake source.
- [x] Add stereo signal meters to the client and server, with actual PCM measurements, held peaks, clipping indication and browser history; verify math, transport integration and rendered UI.
- [ ] Have the owner assess the generated flash/click video and physically disconnect/reconnect the PC network cable for ten seconds.
- [x] Verify deterministic release archives, bundled runtime versions, MIT/third-party notices and exclusion of private files. Strict package verification and archive round-trip passed in the release workflow for `7c33f7c`; 22 verifier tests cover repeatable archive output.
- [x] Commit the reviewed changes and pass the new remote CI matrix and manual release-artifact workflow. Both SDKs passed on Windows/Linux for `7c33f7c`, including 94 portable and 27 Windows client/CLI tests.
- [ ] Publish `v0.1.0` after the remaining hardware acceptance checks pass.

## Project rules

- Keep internal inventory, credentials, local configuration and third-party driver binaries out of Git history.
- Use Conventional Commits for meaningful changes.
- Do not use the receiver's dedicated direct-control connection during tests.
