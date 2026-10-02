# NKS AudioLink progress

A phase is checked only after its implementation and required verification are complete.

- [x] 0. Solution scaffold, local build and tests, public GitHub repository, and passing Windows/Linux CI. Verified on 2026-10-02.
- [x] 1. Core protocol, HMAC, jitter buffer, resampling and clock drift tests. The portable Core/server/queue suite has 39 passing tests; Core line coverage is 86.93%. Drift simulation covers ±200 ppm over ten minutes of virtual time.
- [x] 2. UDP server with WAV and null outputs. A Windows-to-Linux 440 Hz test produced 5.095 seconds of WAV audio, RMS 8,484, and no overruns.
- [x] 3. ALSA output, systemd deployment and a ten-minute hardware run. The .NET 9.0.20 server and Windows client ran for 600 seconds at a 30 ms target buffer. All 60 client samples had zero underruns, overruns, lost and late frames; all 120 ALSA samples were RUNNING. The process and session stayed unchanged, the server buffer held 20–25 ms, and server CPU was 1.262% of one core. A separate 10 ms run accumulated late frames.
- [x] 4. Windows WASAPI client, continuous silent frames and real playback. Captured tone/WAV analysis and live PC-to-ALSA streaming passed. The owner confirmed clean receiver playback.
- [ ] 5. Latency checks specified by the no-microphone test plan, targeting an estimate below 100 ms. The app estimated 71–82 ms during the ten-minute stream. Nine Windows-to-Linux LAN clicks took 22.96–37.88 ms, median 31.39 ms, from UDP send to WAV write. Earlier Linux loopback measurements were 27.36–28.69 ms, median 28.22 ms. The owner still needs to assess flash/click synchronization. Physical end-to-end latency remains unmeasured.
- [x] 6. WPF app, system tray, settings, server discovery and screenshots. Connect/disconnect against a WAV server, live statistics, gain changes while streaming, discovery of the deployed service and a screenshot in `docs/screenshots` were verified.
- [x] 7. Virtual-device mode and Windows default-output restoration. The owner authorized a separate signed VB-CABLE installation, completed without restarting the test PC. A 440 Hz CABLE Input → CABLE Output → UDP WAV stream passed. All three Windows default-output roles were restored on disconnect and on app restart after a simulated crash. The driver is not bundled.
- [ ] 8. Recovery after network loss, server restart and PC sleep/resume; session takeover. The real WASAPI client recovered after a LAN server restart. Seven Windows integration tests cover silence, late server startup, server restart, capture recreation, terminal rejection and a ten-second UDP outage with same-session recovery within five seconds. Server integration tests cover takeover and reopening the output. Physical cable disconnection and PC sleep/resume remain.
- [ ] 9. Release workflow, complete README/changelog and verified packages. Windows and Linux packages have been built and inspected; the packaged Windows CLI and Linux server start successfully. Manual workflow runs build artifacts without publishing a release. Tag `v0.1.0` remains pending until the hardware checks pass.

## Project rules

- Keep internal inventory, credentials, local configuration and third-party driver binaries out of Git history.
- Use Conventional Commits for meaningful changes.
- Do not use the receiver's dedicated direct-control connection during tests.