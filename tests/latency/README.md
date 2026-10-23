# Flash/click synchronization check

This creates a 20-second visual/audio reference for the subjective latency check in the test plan. It needs Python 3, FFmpeg and FFprobe on `PATH`. It does not play audio, change the default audio device, or measure physical latency.

Generate outside the repository (on Windows, replace `python3` with `py -3`):

```sh
python3 tests/latency/generate_flash_click.py --output /path/to/flash-click --verify
```

The primary `flash-click-lossless-audio.mkv` has 60 fps video and exact 48 kHz, 16-bit stereo PCM. A white square flashes for three frames at 1, 3, ..., 19 seconds. Each flash begins with a 10 ms, 1 kHz click at a peak of -12 dBFS. The first nonzero audio sample follows the ideal flash onset by one sample (0.0208 ms). `flash-click.mp4` is an AAC fallback; compression can spread the transient, so prefer the MKV for the assessment.

Verification decodes the media and checks all flash frames, exact PCM round-trip, equal channels, every click's starting sample, and the video/audio container timestamps. `manifest.json` records source parameters and hashes; `verification.json` records the results. These checks establish the timing of the file, not the timing of a display or speaker.

To assess the actual link:

1. Connect AudioLink using the intended capture/output and buffer modes. For the fixed 30 ms baseline, select **Manual** and set 30 ms. To assess **Adaptive**, leave it enabled and let the live target settle; its manual timing controls are intentionally hidden. Confirm the receiver plays the PC's selected output.
2. Open the MKV in a player with audio synchronization correction set to zero. Start with a comfortable receiver volume, then play the reference while viewing the PC display and listening to the actual receiver.
3. Record whether each click appears synchronized with its flash, or whether sound consistently leads/lags. Include the player, display, receiver/output path, buffer mode, observed target and app latency estimate. In Adaptive mode, record the target at the start and end and any changes visible in its graph; do not report the saved manual value as the active target. Playback/display processing and viewing/listening position affect this observation.
4. Keep the result pending until a person observes it. A generated file or a passing verifier is not a synchronization pass and is not a physical end-to-end measurement.

To recheck an existing primary file:

```sh
python3 tests/latency/verify_flash_click.py /path/to/flash-click/flash-click-lossless-audio.mkv /path/to/flash-click/flash-click-source.wav --report /path/to/flash-click/verification.json
```
