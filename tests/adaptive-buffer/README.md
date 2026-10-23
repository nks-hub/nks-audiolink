# Optional client/server adaptive-buffer integration

Run on Windows with a supported .NET 9 SDK from the repository root:

```powershell
dotnet run --project tests/adaptive-buffer --configuration Release -- --output TestResults/adaptive-buffer-rc2.json
```

This opt-in test takes about 70 seconds after compilation. It runs the production
`AudioStreamClient` and `AudioServer` over real loopback UDP with a local proxy.
It uses a fresh random authentication key for each run. No key is printed or
persisted. It does not use a microphone, change sound-device settings, connect to
an external server, or send audio to speakers. The source supplies synthetic
nonzero 48 kHz stereo PCM; the output sink counts actual nonzero playout frames.

The three scenarios are:

- `manual`: ordinary 33-byte STATS and an unchanged 30 ms target.
- `loss`: predictive HELLO `0x82` and 45-byte STATS `0xA2`, with the requested
  20–100 ms bounds. The initial target must be 40 ms even though the remembered
  manual value is deliberately 155 ms. Dropping exactly ten consecutive packets
  (50 ms) must raise the target to 50 ms. More than 35 seconds of continuation
  must preserve that learned target under the new 120-second failure hold, with
  exactly ten losses and one underrun and no additional errors.
- `jitter`: a bounded timed proxy gradually varies delivery delay between zero
  and 30 ms, without deliberately dropping packets. A `PredictiveRisk` increase
  must be observed before any loss, late packet or underrun. The entire scenario
  must remain free of those errors. Proxy scheduling lateness is recorded so
  intended network variation can be distinguished from host scheduling delays.

All scenarios require one continuous session and actual nonzero output PCM.
Each STATS observation records actual buffer occupancy and drift correction; a
changed target is not an instantaneous change in buffered audio. Policy changes
also capture the predictor and counters. Both endpoints must release resources.
Use `--scenario manual`, `--scenario loss`, or `--scenario jitter` to select a
single case. Failed real-time assertions print their observations to stderr.

The older RC1 harness expected a 30 → 40 → 35 ms sequence after a loss. That was
the reactive RC1 policy. Its recorded results remain historical; they do not
establish predictive RC2 behavior or its longer failure hold.

This is a real-time integration test, separate from the deterministic controller
unit tests and normal CI. Heavy CPU load, suspend, debugging pauses, or host
scheduling delays can fail its strict clean-continuation checks. Investigate a
failure rather than counting a retry as proof of an uninterrupted pass. It does
not validate physical WASAPI/ALSA device latency or subjective audio quality.
