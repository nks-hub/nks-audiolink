# Optional client/server adaptive-buffer integration

Run on Windows with a supported .NET 9 SDK from the repository root:

```powershell
dotnet run --project tests/adaptive-buffer --configuration Release -- --output TestResults/adaptive-buffer.json
```

This opt-in test takes about 42 seconds after compilation. It runs the production
`AudioStreamClient` and `AudioServer` over real loopback UDP with a local proxy.
It uses a fresh random authentication key for each run. No key is printed or
persisted. It does not use a microphone, change sound-device settings, connect to
an external server, or send audio to speakers. The source supplies synthetic
nonzero 48 kHz stereo PCM; the output sink counts actual nonzero playout frames.

The manual scenario checks ordinary 33-byte STATS and an unchanged 30 ms target.
The adaptive scenario drops exactly ten consecutive audio packets (50 ms), checks
45-byte STATS with the requested 20–100 ms bounds, and observes the target rise
from 30 to 40 ms. It then continues for more than 35 seconds, requiring a stable
decrease to 35 ms with no additional network loss or jitter underruns and no
session restart. Each STATS observation records actual buffer occupancy and
drift correction so a changed target is not confused with an instantaneous
change in buffered audio. Both endpoints must release capture/output resources.

This is a real-time integration test, separate from the deterministic controller
unit tests and normal CI. Heavy CPU load, suspend, debugging pauses, or host
scheduling delays can fail its strict clean-continuation checks. Investigate a
failure rather than counting a retry as proof of an uninterrupted pass. It does
not validate physical WASAPI/ALSA device latency or subjective audio quality.
