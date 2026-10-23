# Validation

## RC2 verification, 2026-10-23

RC2 replaces the desktop layout, adds predictive buffering with hysteresis and introduces live mute/limiting and explicit busy takeover. The published RC1 remains an immutable historical build. Current checks are distinct from the older physical measurements below.

| Check | RC2 evidence |
|---|---|
| Combined Release build/tests | All seven projects built with zero warnings/errors; 168 portable and 57 Windows client/CLI tests passed together. |
| Core coverage | Portable Cobertura: 96.52% lines and 88.84% branches. |
| Dashboard/release helpers | 16 Node tests and 36 Python package/version tests passed. Browser checks covered predictive capability/estimate/hold state alongside history, cancellation, offline/error recovery and 320 px layout. |
| Desktop layout | Actual detached WPF controls and native windows passed manual/adaptive, process, missing virtual-device and busy states at 800×640 and 960×720. Primary controls fit without scrolling. Caption hit testing, maximize/restore, keyboard focus and both secondary panels passed. |
| Desktop state | Automatic mode hides manual controls without changing the stored manual target. Only acknowledged predictive mode is labeled predictive. Target and occupancy remain distinct; bounded monotonic history breaks across outages. A regression caught and fixed a stale paused-reason label after same-value reconnection. |
| DSP | 18 numeric tests cover exhaustive PCM16/static-gain bypass equivalence, stereo-linked ceiling, 48-frame delay, tail drain/reset, gain/mute/toggle transitions and zero steady allocations. Four actual UDP regressions cover delayed tails, mute/gain retention, post-effect meters and quiet reconnect without stale audio. |
| DSP processing cost | An isolated processor benchmark handled 50 seconds of hot stereo PCM using 140.625 ms CPU in bypass and 171.875 ms with limiting: 0.28125% and 0.34375% of one core respectively, with zero bytes allocated inside the warmed processing loops. These are processor-only costs, not whole-application or hardware measurements. |
| Predictive policy | Deterministic timing tests cover increases before underruns, isolated spikes, alternating jitter without oscillation, ±200 ppm drift, reordered unique packets, duplicates, true loss, discontinuities, warmup, waveform continuity and allocation behavior. |
| Rare failures | Recurrent 25 ms delayed packets at 30/60/90-second spacing exercise a learned 120-second clean-playback hold. Starting deliberately at 20 ms needed two bounded recovery increases before reaching a safe target; subsequent bursts did not cause fresh loss or repeated downward oscillation. The hold eventually releases after risky tail observations cease. This does not claim prediction of unseen spikes. |
| Actual authenticated UDP integration | The public harness passed manual, predictive loss and timed-jitter scenarios through the production sender/server. Manual mode delivered 699 packets and 661 nonzero output frames with zero counters. Predictive loss started independently at 40 ms despite a stored manual value of 155 ms; exactly ten deliberate losses caused one underrun and target 50 ms, retained in the learned hold after 38.002 seconds, with no extra errors or session change. |
| Proactive timing response | A 28.001-second timed-jitter run delivered 5,599 packets and 5,329 nonzero output frames. Target rose 40→50 ms with `PredictiveRisk` at 7.096 seconds while lost/late/underrun counts were still zero; all counters remained zero. The proxy introduced 0–30 ms delay and also observed up to 26.03 ms host scheduling lateness, so this is an application-level integration observation, not an ideal network-only trace or physical audio measurement. |
| Published RC1 compatibility | The new CLI was exercised against the verified published RC1 Linux binary in isolated WSL. Predictive format `0x82` was rejected with an update/manual-mode explanation and no output session; manual mode connected, received ordinary STATS, exited normally and released the session, with zero jitter counters. |

The native process-isolation measurement under RC1 below remains evidence for the unchanged capture implementation. Clipboard content is not overwritten during RC2 UI verification. A fresh deployed RC2 stability run is recorded separately when completed; earlier runs must not be relabeled as predictive-mode hardware evidence.

## Release candidate verification, 2026-10-23

The `0.1.0-rc.1` source adds server-owned numeric history, separate native output counters, safe client diagnostic copy, manual buffer presets and consistent product version reporting. A subsequent comparison follow-up adds application-specific capture and opt-in adaptive buffering. Earlier physical measurements below retain their original scope.

| Check | Candidate evidence |
|---|---|
| Release build | All seven projects built successfully with zero warnings and errors using SDK 9.0.203. |
| Automated .NET tests | 134 portable tests and 48 Windows client/CLI tests passed together. History bounds, range/cursor/epoch resets, native-counter lifetime, diagnostic redaction, version subprocesses, adaptive protocol/controller and application selection/lifecycle are included. |
| Core coverage | Portable-suite Cobertura measured 96.22% lines and 85.82% branches, above the 80% line gate. |
| Dashboard | All 15 Node tests passed. Chromium against actual local Kestrel plus controlled fixtures verified refresh recovery, incremental history, range changes, session gaps, pause/offline/403 recovery, retained closed-session native totals, adaptive target/reason/history and 320 px layout without JavaScript errors or external requests. |
| Desktop | Compiled preset handlers preserved saved values including 30/35/40/80/155/199 ms and correctly selected 20/40/80 ms. Custom and disabled states passed without invoking capture, networking or settings writes. Native layout/focus checks passed at 960×720 and 800×640. |
| Package and version helpers | All 36 Python tests passed, covering strict package validation, deterministic archive creation, SemVer/tag matching, changelog selection and build manifests. All three workflows passed actionlint 1.7.12. |
| Independent source review | Diagnostic fields/redaction, bounded history, access controls, asynchronous request cancellation and native-counter lifetime were reviewed without an actionable defect. |

History is memory-only, limited to 3,600 one-second numeric samples, and collected only while HTTP is enabled. It survives browser refresh but clears on server restart. The HTTP suggestion remains TCP **7356**, never 8080. Client diagnostics contain no audio or shared key and require an explicit clipboard click.

The earlier 600.002-second hardware pass predates the history sampler and extended HTTP native-counter reporting. It is evidence for the output scheduling fix, not a hardware acceptance measurement of these new additions. Listening synchronization and physical cable recovery remain pending. Tagged CI and subsequent deployment evidence are recorded separately when available.

### Application capture and adaptive buffering

- Real native process-loopback activation and capture passed on Windows 10 build 19045. The [public isolation harness](../tests/process-capture/README.md) selected a parent with a 440 Hz audio child while an unrelated process rendered 990 Hz to the same virtual endpoint. Across 96,000 captured PCM samples, RMS was 0.0070711, selected-tone amplitude 0.01000003 and unrelated-tone amplitude approximately 0.0000001065 (about 99 dB lower). Silence after pausing the selected child and terminal notification after parent exit also passed. No default audio routes or production client settings were changed. These fixture results do not establish compatibility with every application or protected audio stream.
- The process identity includes start time and a retained handle, rather than only a reusable PID. Regression checks cover unavailable/reused process identities, cancellation and terminal process-exit handling without capture recreation. The app requires an explicit current selection after restart; it does not silently capture another process with the same name.
- Client negotiation tests verify no PCM before a matching adaptive acknowledgment, retained options on keepalive, safe handling of missing/mismatched acknowledgments and an actionable old-server rejection. Manual HELLO and 33-byte STATS remain byte-compatible; adaptive sessions use the documented format variant and a validated 45-byte STATS extension.
- Actual compatibility with the downloaded `ad56f119` Linux server passed in isolated WSL: adaptive CLI mode exited with the update-or-disable explanation and left no output session open. Manual mode connected, delivered nonzero captured PCM, exited normally and released the session. This validates protocol compatibility, not WSL output latency or physical playback.
- The initial authenticated client/server loss-proxy test delivered nonzero PCM through the production sender and receiver. Manual mode kept target 30 ms and ordinary STATS with zero jitter errors. Adaptive mode, after exactly ten deliberately dropped audio frames, changed target 30→40 ms with one underrun and ten lost frames while retaining the same session. Longer stability evidence is recorded separately from this initial scenario.
- The [public adaptive-buffer integration harness](../tests/adaptive-buffer/README.md) then ran a 38.002-second scenario through the actual authenticated sender/server with a synthetic PCM source and inspecting sink. It observed 7,599 audio packets and 58 STATS updates, one session, exactly ten deliberately lost frames, one underrun and no additional late frames or overruns. Target changed **30→40 ms** after loss and **40→35 ms** with reason `Stable` at about 31 seconds. Actual occupancy declined gradually (75 ms before the decrease, 70 ms at the first stable update and 50 ms by the end). Host scheduling lateness reached 36.4 ms with 28 skipped deadlines, so this is not a physical low-latency or scheduler-perfect result. It verifies adaptation, bounded resampling and session continuity; deterministic triangle-wave tests separately check sample continuity during target changes.
- Windows CI compiles both optional real-audio/integration harness projects on the two supported SDKs. Physical process-isolation and timing scenarios remain opt-in, with their requirements and limits documented beside each harness.

## Review and development, 2026-10-23

The reported NETSDK1141 was reproduced with the installed SDK 9.0.203: `global.json` required 9.0.318 with `latestPatch`. Local selection now permits stable SDK 9.0.203 or later within 9.0 (`latestFeature`). CI explicitly selects both the minimum and release SDK; release packaging explicitly selects 9.0.318. Revision `7c33f7c`, including the final meter-reset and scheduling fixes, passed the [Windows/Linux CI matrix](https://github.com/nks-hub/nks-audiolink/actions/runs/39938086483) and the [manual release-artifact workflow](https://github.com/nks-hub/nks-audiolink/actions/runs/39938088133). The latter built and verified both archives without publishing a tagged release.

The review covered SDK/build configuration, packet parsing, jitter recovery, client sender/receiver lifecycle, configuration/CLI precedence, desktop state/shutdown, and the new optional HTTP monitor. Regressions reproduced a server crash on a misaligned audio index, output opening on unsupported buffer sizes, and stalled playback after consecutive packet loss. Those cases now pass. Additional fixes cover delayed capture callbacks, timer arithmetic for long streams, concurrent connection state, invalid numeric/CIDR settings, and orderly test-tone rejection.

| Check | Current evidence |
|---|---|
| Debug build | Visual Studio 2022 MSBuild rebuilt all seven projects successfully with SDK 9.0.203 into an isolated artifacts directory |
| Release build | All seven projects built with zero warnings and zero errors |
| Automated .NET tests | 94 portable tests and 27 Windows client/CLI tests passed; includes real local UDP and HTTP requests, sender clock-discontinuity recovery, socket recreation after send/receive invalidation and a CLI subprocess rejection test |
| Core coverage | A fresh Release run with `--collect "XPlat Code Coverage"` measured 90.48% line coverage and 82.33% branch coverage; the required 80% line coverage gate passes |
| Dashboard regressions | `node --test tests/dashboard.test.cjs`: 9 passed; rapid pause/resume, stale requests, disconnect/session reset, access-denied recovery, stereo meters, history and invalid level samples |
| Stereo PCM meters | Nine Core tests verify RMS mathematics, stereo independence, silence, peak hold/decay, clipping and allocation behavior. Client integration compares post-gain sent PCM; server integration compares the exact samples presented to the sink. Real client and server meters both reacted to live audio. |
| Meter recovery | A regression reproduced stale RMS/peak/clipping after a three-second server-response timeout. Resetting levels at connection transitions fixes both the outage and quiet same-session reconnection; all 27 client/CLI tests pass. |
| Output scheduling | Seven regressions cover short delays, bounded recovery after long pauses, blocking output pacing and cancellation. An accounting model with four 7 ms wake delays reproduces 196 writes instead of the required 200 in one second; the corrected scheduler preserves all 48,000 output sample frames. This is a deterministic model, not a hardware measurement. |
| Native output diagnostics | Seven tests cover ALSA recovery logging, native error classification, cumulative counts, rate limiting, summaries and diagnostic-write failures. Hardware recovery is reported separately from jitter-buffer counters. |
| Package-verifier regressions | 22 Python tests passed on Windows and Linux, including archive allowlists, runtime inspection, path/permission checks and reproducible archive bytes |
| Browser smoke | Chromium against local Kestrel: desktop/mobile layout, single polling chain, paused/offline/403 states, reconnect, session clearing, text injection, no JavaScript errors or external requests |
| Native desktop UI | Redesigned 960×720 layout and minimum 800×640 verified with real controls: persistent connection actions and live metrics, native caption/edge hit testing, maximize/restore/minimize, keyboard slider operation and focus indicators. The deployed candidate connected to the physical receiver; About/MIT links, gain changes and orderly Quit were checked. |
| Linux standalone | Self-contained linux-x64 with .NET and ASP.NET Core 9.0.20 ran on Ubuntu 22.04/WSL without `dotnet`; HTTP/API, local UDP-to-WAV, recovery after a 200-frame packet gap, BYE and clean SIGINT exit verified |
| Windows standalone | App and CLI published as self-contained single-file win-x64 executables; bundle inspection confirms both .NET and WindowsDesktop 9.0.20. This inspection caught and corrected an initially mixed CLI runtime package. CLI help, discovery, busy rejection and takeover were exercised; the new desktop app replaced the previous instance through orderly Quit. |
| Package audit | NuGet reported no known vulnerable direct or transitive packages using the configured feed. This is not a runtime or operating-system security audit. |

The desktop preview and web screenshot are under `docs/screenshots`. They use a disconnected desktop and controlled example web values, without private device/address data; they are interface illustrations, not benchmark results. The existing HMAC protocol authenticates packets but does not encrypt audio or provide a complete replay-protection mechanism. The web monitor is read-only and restricted by `allowCidrs`; it has no TLS/login and must remain on the intended LAN or an SSH tunnel. Its suggested TCP port is **7356**, separate from UDP audio on 7355; HTTP remains disabled in the default server configuration.

### Additional live checks, 2026-10-23

- A new five-second Windows-to-Linux WAV test used a separate receiver port and the deployed server binary. Goertzel analysis confirmed a dominant 440 Hz tone (energy over 2.5 million times the strongest tested comparison frequency), duration **5.075 s**, RMS **842.03** at gain 0.1. Reported losses, late frames, underruns and overruns were zero.
- The isolated recovery harness used actual WASAPI loopback capture, including nonzero PCM. After a **10,008 ms** proxy outage it resumed within **1,977 ms**; receiver restart took **1,117 ms** and a 200 ms sender-thread pause took **107 ms** from pause end to accepted responses and twenty resumed frames. These controlled interruptions are not a physical cable test.
- Immediate busy rejection exposed a NAudio capture-start/stop race that could hang disposal. The original implementation reproduced a timeout; the corrected implementation completed **30 of 30** real-WASAPI rejection attempts in **10–60 ms**. The packaged CLI then rejected a busy physical receiver promptly while the first client continued streaming.
- Live takeover replaced the existing session and made the previous desktop client stop with an explanatory rejection. The temporary client stopped after its requested duration; the receiver released its ALSA device. Discovery found the deployed service.
- Replacing/restarting the systemd service preserved its configuration and restored the existing client session in **1.197 s**, **1.047 s** after changing HTTP to 7356, and **1.001 s** after the dashboard update. Each observation includes service restart and HTTP polling time.
- A real Windows S3 test exposed socket error **10022**: the connected UDP socket became invalid and the original client stopped. The fix recreates the socket and capture session. A subsequent real S3 cycle resumed streaming automatically **11.72 s after the Windows wake event**, with zero error counters in the observed resumed session. The first diagnostic sleep woke through the owned wake timer; the successful recovery cycle woke through the power button. The five-second requirement in the original plan applies to server restart; it is not asserted for Windows wake.
- A first interrupted live run at a 30 ms buffer recorded one lost/late frame and one underrun during a **30.6 ms** packet-arrival gap. This run is not a zero-underrun pass. The current machine was changed to a 40 ms target for the subsequent stability check; the product default remains 30 ms.
- The subsequent **600.002 s** run at 40 ms, before adding the meters, used **1.1817% of one core**. All 121 ALSA samples were RUNNING, with one unchanged process/session, but one lost/late frame and underrun occurred. The closed session reported a maximum packet-arrival gap of **48.8 ms**. This also fails the zero-underrun gate; no adapter settings were changed based on that observation alone.
- The first **600.002 s** run with the meters used **1.3017% of one core** and all sampled error counters were zero. ALSA remained RUNNING in all samples, but the client session changed, so this is not an uninterrupted-session acceptance pass. All 61 desktop samples showed Connected, zero error counters and a **72–92 ms** latency estimate. A separate header-only capture recorded 129,597 audio packets with zero kernel capture drops, missing sample indices, reordered frames or duplicates; the two observed sessions had maximum arrival gaps of **7.686 ms** and **9.101 ms**. The capture does not establish the cause of the earlier 48.8 ms gap or the session change.
- A separate **600.002 s** meter run retained one process/session and used **1.2067% of one core**, with all 121 samples RUNNING and zero protocol counters. However, ALSA's trigger timestamp changed and its hardware pointer restarted between elapsed **460.107 s and 465.108 s**, without a session/owner change. The pre-fix output code could recover native write errors silently. Consequently, zero jitter-buffer counters and sampled RUNNING states do not establish zero physical-device underruns; this run is not counted as a clean hardware acceptance pass.
- Investigating that observation exposed a scheduling defect: the previous loop permanently skipped output deadlines after short delays. Bounded catch-up fixes that accounting defect, and native ALSA errors/recovery outcomes are now logged. The new deployment restored the existing client in **1.704 s**. One subsequently closed session recorded **9.7 ms** maximum scheduling lateness with zero discarded deadlines and zero native recovery events. This supports the corrected behavior but does not prove the cause of the earlier ALSA transition. The acceptance measurement was interrupted by explicit Disconnect/Connect commands recorded in the client activity log, so that interrupted measurement did not satisfy the ten-minute gate.
- The owner confirmed that the intervening connection/settings changes were manual. A subsequent fresh **600.002 s** run of the corrected server and updated desktop client at a **40 ms** target passed: one unchanged process/session, all **121** ALSA samples RUNNING, one unchanged trigger timestamp/owner and a monotonically increasing hardware pointer. Every jitter counter remained zero, the native journal contained no recovery/error events, and process CPU use was **1.260% of one core**. This completes the current ten-minute hardware gate; it does not replace the pending physical cable or subjective synchronization checks.
- The generated flash/click reference has 60 fps video, exact 48 kHz PCM and 0.0208 ms source alignment. Container timestamps, all flash frames and PCM round-trip were verified. The owner reported that the subjective playback check has **not yet been performed**.

Physical cable disconnection and the owner's flash/click assessment remain open. The historical measurements below retain their original dates and scope.

## Earlier hardware and deployment evidence

Status as of 2026-10-02. CI for revision `af12a5b` passed with 39 portable tests and 7 Windows client tests. The Windows app, CLI, server and documentation are in English. Local configuration and full logs remain outside Git.

| Area | Result |
|---|---|
| Portable core, server, and client queue | 39 automated tests passed, covering the protocol, packet authentication, network jitter buffering, clock drift correction, outputs, and takeover of a busy server |
| Core coverage | 86.93% of lines and 76.01% of branches according to Cobertura; the 80% line coverage requirement was met |
| Windows client | 7 local UDP integration tests passed: silent stream, late server startup, server restart, capture recovery after device error, stopping after busy or unsupported-format rejection, and a ten-second UDP outage |
| Windows → Linux WAV | 440 Hz tone, 5.095 s long, RMS 8,484; zero overruns and late frames |
| Deployed systemd ALSA server | The updated server and Windows client on .NET 9.0.20 streamed for 600 s with a 30 ms target buffer. All 60 sampled client error counts were zero. ALSA was RUNNING in all 120 checks; the process and session remained the same. |
| Device release | After the client disconnected, the service released the ALSA device after the configured interval |
| Real WASAPI client | Captured sound was confirmed in a WAV file; LAN streaming recovered after a systemd service restart |
| Ten-second UDP outage | When the server returned, it accepted the same session within 5 s and audio continued without a new capture instance. The physical cable was not unplugged. |
| Running desktop application | Connect, disconnect, server discovery, statistics, and changing gain while streaming were checked |
| Virtual mode | A real 440 Hz signal through CABLE Input → CABLE Output → UDP → WAV was confirmed by file analysis |
| Default device restoration | All three Windows roles were restored after disconnect and on the next start after a forced application exit |
| Listening | The owner confirmed clean sound on the receiver. Synchronization with video has not yet been confirmed. |
| Volume | Both channels of the hardware mixer's `Speaker` control were at −10 dB. After changing them to 0 dB and returning application gain from 200% to 100%, the owner confirmed that the volume was right. The CABLE device and active application in Windows were at 100% and unmuted. |
| Live PC output | A nonzero signal was present on the selected playback device, the client was connected, and physical ALSA output was RUNNING |
| Prepared packages | The packaged Windows CLI started; the single-file Linux server ran `--help` on the target without an installed .NET runtime |

The deployed English app retained virtual-device mode, a 30 ms target buffer and 100% gain. After the English server was activated with a service restart, its log recorded the existing client session reconnecting two seconds later. Physical ALSA output returned to RUNNING, and all four client error counters were zero when checked. The PC was not restarted.

VB-CABLE was installed separately with the vendor's signed installer after the owner's approval. The test PC was not restarted. Neither the driver nor its installer is part of this project.

## Latency and CPU

During a live stream, the desktop application displayed an estimated 74 ms of delay. The calculation includes the actual capture buffer length, the client queue target, framing, half the network RTT, current server buffer occupancy, and server output queue delay. It excludes unknown delay beyond the physical sound card. End-to-end physical latency and video synchronization have not yet been measured.

The first test on the target Linux host used the original .NET 9.0.4 runtime, a real UDP server writing to WAV on a separate local port, and nine clicks. Packet send and the first nonzero sample were timestamped with the same monotonic clock. The calculation accounted for the sample position within each 5 ms frame. With a 30 ms target buffer, the measured range was 27.36–28.69 ms, median 28.22 ms. The server reported zero underruns, overruns, losses, and late frames. This test did not include Windows, the physical LAN, or ALSA.

A later test sent nine clicks from Windows over the real LAN to a Linux server that recorded the write time of the first nonzero sample in a WAV sink. The interval from UDP send to that write was **22.9607–37.8828 ms**, median **31.393 ms**. Clock offset was estimated from 80 four-timestamp network exchanges, 40 before and 40 after the test. The minimum round-trip time was 0.1064 ms before and 0.1167 ms after. Possible network path asymmetry alone adds approximately ±0.05835 ms of uncertainty; operating system scheduling and clock behavior may add more, unquantified error. The measurement excludes WASAPI capture, ALSA playback, and the receiver. It is not total physical audio latency.

The previous server used about 3% of one core during active ALSA streaming and missed the planned CPU target of below 2%. A trial with blocking UDP receive increased both CPU load and timing variation. Batching two frames did not show a clear saving and added waiting time. Both changes were reverted.

A later `/proc` profile located most CPU use in ThreadPool threads. Isolated comparisons without a sound device used the same .NET 9.0.20 runtime and a stream of 200 PCM packets per second. Each run had 20 s of warm-up and 30 s of measurement:

| Settings | CPU use, one core | Longest audio receive gap |
|---|---:|---:|
| Default thread spinning, normal network completion | 3.87% | 10.1 ms |
| Thread spinning disabled, normal network completion | 2.37% | 13.1 ms |
| Thread spinning disabled, inline completion on one socket thread | 1.40% | 5.6 ms |

All isolated runs had zero underruns and late frames, and the sender missed no deadlines. The Linux binary and systemd unit use the last configuration. They were activated by restarting the service after `systemd-analyze verify`. The Windows client restored its session within one second, and local configuration remained intact.

The first 600 s monitoring run of the new server used a real ALSA output. The process and session did not change. All 120 regular checks found ALSA RUNNING, and the service log contained no error. CPU use was **1.277% of one core**. With the user-selected 10 ms target buffer, however, the loss, late-frame, and underrun counters rose from 3 to 59; overruns remained zero. When that session later disconnected, the server logged a final count of 80 for each of those three counters. This run did not meet the no-underrun requirement.

The second run used the updated Windows client on .NET 9.0.20 and the default **30 ms** target buffer. Over 600 s, the server process and session remained the same, all 120 ALSA checks were RUNNING, and all 60 client samples showed a connection with zero lost or late frames, underruns, and overruns. The new session had no logged error. Server buffer occupancy was 20–25 ms, the application's delay estimate was 71–82 ms, and server CPU use was **1.262% of one core**, below the planned 2% threshold. CPU use was calculated from process CPU tick and monotonic time differences; it is not a percentage of the whole multicore host.

## Remaining hardware checks

- Have the owner assess flash and click synchronization using the prepared test video. Clean audio has already been confirmed.
- Unplug the PC network cable for ten seconds and reconnect it. Observe session recovery and output-device release. Real Windows S3 recovery was tested and fixed on 2026-10-23 as described above.

Physical end-to-end latency remains unmeasured. The original plan uses a calculation, a timestamped LAN/WAV test and the owner's video assessment; an independent measurement through the receiver is not an additional release gate.

Cable disconnection requires someone at the PC. See [TODO.md](../TODO.md) for phase status.
