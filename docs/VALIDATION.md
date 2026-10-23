# Validation

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
