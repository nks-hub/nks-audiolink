# Validation

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
- Unplug the PC network cable for ten seconds and reconnect it; separately suspend and wake the PC. Observe session recovery and output-device release.

Physical end-to-end latency remains unmeasured. The original plan uses a calculation, a timestamped LAN/WAV test and the owner's video assessment; an independent measurement through the receiver is not an additional release gate.

Cable disconnection and PC suspension require the owner's participation. See [TODO.md](../TODO.md) for phase status.
