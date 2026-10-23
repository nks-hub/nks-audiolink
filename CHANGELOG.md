# Changelog

## Unreleased

- C#/.NET 9 Linux server with direct ALSA output, a systemd service and an installer.
- UDP PCM protocol with optional HMAC authentication, a jitter buffer and audio clock drift correction.
- Windows WASAPI client for playback capture and recording inputs; silent frames preserve the audio timeline.
- Recovery after server outages and capture errors. A rejected client stops and requires a new connection action.
- WPF app with settings, discovery across active IPv4 interfaces, a system tray menu and live statistics.
- Support for a separately installed VB-CABLE driver, with Windows default-output restoration on disconnect and after restarting the app following a crash.
- CLI commands for device listing, server discovery, audio streaming and a test tone.
- Windows x64 and Linux x64 package builds, with publication triggered by a version tag.
- Bundled .NET runtime 9.0.20, release SDK 9.0.318 and runtime license files in the packages.
- Local builds support SDK 9.0.203 and newer stable 9.0 SDKs, fixing NETSDK1141 on installations with SDK 9.0.203. CI covers both the minimum and release SDKs.
- Reject misaligned audio sample indices before they reach the jitter buffer, preventing malformed UDP packets from stopping the server. Reject target buffers that are not multiples of 5 ms before opening the audio output.
- Recover jitter buffering after consecutive packet losses, long gaps and interrupted startup; keep reordered startup packets within the ring window.
- Fix client acceptance/timeout races, slow-start state ordering, long-running sender timer overflow, cancellation and late capture callbacks.
- Prevent an immediate server rejection during real WASAPI startup from hanging capture disposal. The regression harness exercises repeated rejection with an actual Windows audio endpoint.
- Recreate a UDP socket invalidated by Windows sleep/resume (socket error 10022), instead of terminating the stream. Other invalid-argument failures retain their normal error behavior.
- Explicit client and server CLI arguments override configuration files regardless of order; a rejected test tone stops and releases its session promptly.
- Compact borderless desktop window with native resizing, waveform app/tray icons, About, activity history and graceful Quit. Connection controls stay at the top; live measurements remain visible beside configuration, including at the minimum window size.
- Optional read-only Kestrel server dashboard and JSON statistics API, disabled by default and restricted by `allowCidrs`; desktop dashboard launcher with a separate HTTP port, 7356 by default. Session counters and stereo levels remain visible above the expandable buffer history.
- Stereo PCM meters in the client and server dashboard: 300 ms RMS, one-second held peaks and clipping indication. The browser includes 60-sample left/right signal history; the existing buffer graph remains available in an expandable section.
- Clear client signal levels on a server-response timeout and reconnection, preventing stale RMS, held peaks and clipping from appearing after an outage.
- Verify release archives against a public-file allowlist, actual embedded runtime versions, license content, extraction permissions and round-trip file hashes. Include the project's MIT license in both distributions.
- Linux thread and socket settings reduced server CPU to 1.262% of one core during a 600-second physical ALSA stream. All client error samples were zero at a 30 ms target buffer; a separate 10 ms run accumulated late frames.
- English documentation, app controls, tooltips, CLI help and error messages.

The first release remains pending until the hardware and recovery checks in [TODO.md](TODO.md) are complete.
