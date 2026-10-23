# Real application-capture verification

This opt-in Windows test launches controlled audio processes and uses the production process-loopback source. It requires an active, separately installed **CABLE Input** endpoint. It never falls back to a physical/default output, changes Windows routing, sends UDP, or controls the production client. If another application monitors CABLE Output, it can hear the two quiet test tones; run this on an isolated virtual endpoint.

```powershell
dotnet run --project tests/process-capture -- --virtual-cable
```

The selected parent process launches a child rendering 440 Hz. An unrelated process renders 990 Hz to the same virtual endpoint. The test checks actual captured PCM for the child tone and exclusion of the unrelated tone, checks silence when the child pauses, then checks that the parent's exit reports a terminal capture error. Only processes launched by this harness are stopped. The final JSON contains numeric results and the OS version, without endpoint IDs or user names.

Process loopback is probed through NAudio's MIT-licensed `ActivateAudioInterfaceAsync` wrapper, using `AUDIOCLIENT_ACTIVATION_PARAMS` and inclusion of the selected process tree. Microsoft documents the original API with a build 20348 requirement; updated Windows 10 builds can also support it. The product checks the activation result and reports unsupported capability instead of silently capturing all audio. See [Microsoft's API](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_activation_params), [NAudio's API](https://naudio.github.io/NAudio/api/NAudio.Wave.WasapiRecorderBuilder.html) and [OBS's compatibility guide](https://obsproject.com/kb/application-audio-capture-guide).

This harness is separate from the ordinary unit suite because it needs real Windows audio services and an installed virtual endpoint. A build or mocked source test alone does not demonstrate process isolation.
