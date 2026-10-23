# Transport recovery validation

This Windows-only harness runs the production `AudioStreamClient` with real WASAPI loopback capture. By default it creates a production `AudioServer` with a null output on a free local UDP port. It does not change the default audio device, network adapters, power state, existing applications or deployed services.

Run from the repository root:

```powershell
dotnet run --project tests/recovery/RecoveryHarness.csproj --configuration Release -- --report TestResults/recovery-wasapi.json
```

Use `--synthetic` when no audio endpoint is available. For a capture endpoint instead of loopback, use `--capture`; `--device ID` selects an explicit endpoint. `--help` lists all options.

The harness verifies:

1. An accepted session forwards at least twenty audio frames.
2. A private bidirectional UDP proxy drops traffic for ten seconds. No accepted response crosses the closed proxy; the same capture instance and session resume within five seconds after traffic is restored.
3. The receiver created by the harness stops and starts on the same UDP port. The same capture and session resume within five seconds of restart.
4. The harness pauses only its sender thread for 200 ms. After this clock discontinuity, it observes a new HELLO, accepted STATS and resumed audio within five seconds. Timing starts at the actual end of the injected pause.

Recovery requires accepted STATS and at least twenty forwarded audio frames, so its reported duration includes about 100 ms of resumed transport. The JSON report distinguishes real WASAPI from synthetic capture, records session IDs, state changes, recovery durations and audio-frame counts, and returns a nonzero exit status on failure. `nonZeroPcmFrames` confirms captured nonzero samples if audio was playing; silent desktop capture is valid and does not prove content playback. Output is discarded by the null receiver.

An explicitly supplied `--server HOST --port PORT` or `--config FILE` selects an external receiver. The receiver must be available for this test client; the harness does not request takeover. It never restarts an external service, and omits the receiver-restart stage in that mode. Configuration can contain a shared key; the report does not copy it. A configuration file is loaded first and explicit options override it.

The proxy outage is a controlled transport test, not a physical cable-disconnection test. The sender pause exercises clock-discontinuity handling, not actual Windows sleep/resume or audio-driver recreation after wake. The report explicitly leaves physical network disconnection, PC sleep/resume and physical playback verification false. Those checks remain separate release gates.

## Immediate rejection during WASAPI startup

```powershell
dotnet run --project tests/recovery/RecoveryHarness.csproj --configuration Release -- quick-reject --iterations 30 --report TestResults/quick-reject.json
```

This mode repeatedly opens real default-render WASAPI loopback capture against an owned local responder that immediately rejects HELLO with `Busy`. Every attempt must dispose capture and return the terminal rejection within five seconds, without recreating capture. It exercises a NAudio 3.1 startup race that synthetic-source tests cannot reproduce: an early Stop could be overwritten by the capture thread entering Capturing, causing Dispose to block indefinitely. No audio is sent to a deployed receiver and no existing client is interrupted.
