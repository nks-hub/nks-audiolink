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
- Bundled .NET runtime 9.0.20, pinned SDK 9.0.318 and runtime license files in the packages.
- Linux thread and socket settings reduced server CPU to 1.262% of one core during a 600-second physical ALSA stream. All client error samples were zero at a 30 ms target buffer; a separate 10 ms run accumulated late frames.
- English documentation, app controls, tooltips, CLI help and error messages.

The first release remains pending until the hardware and recovery checks in [TODO.md](TODO.md) are complete.