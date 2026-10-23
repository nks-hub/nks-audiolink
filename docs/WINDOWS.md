# Windows client

The desktop application requires Windows 10 or later. The `win-x64` release includes the .NET runtime. Building from source requires .NET SDK 9.0.203 or later in the 9.0 series, as specified by `global.json`:

```powershell
dotnet run --project src/NksAudioLink.App
```

Enter the server address or click **Find**, choose an audio source, set **Gain**, then click **Connect**. Expand **Buffer & startup** to change **Target buffer**, which controls the server jitter buffer size. Gain can be changed while streaming; disconnect before changing the other settings. Connection controls stay at the top, and the right-hand status panel shows estimated delay, server buffer occupancy, output queue delay, network round-trip time, and counts of lost and late frames, underruns, and overruns.

Choose a manual buffer preset: **Low latency — 20 ms**, **Balanced — 40 ms**, or **Robust — 80 ms**. A larger buffer tolerates more network variation but adds delay. The slider still accepts custom values; **Custom** indicates a value outside those three presets. The initial default remains **30 ms**, and opening the app preserves the saved target instead of applying a preset. Both controls are disabled while streaming; there is no automatic buffer adjustment.

Closing the window leaves the application in the notification area. Its icon menu lets you connect, disconnect, reopen the window, or quit. **Start with Windows** creates a startup entry only for the current user. Settings are stored in `%AppData%\NksAudioLink\settings.json`; the shared key is not saved there.

The compact borderless window has its own minimize, maximize/restore, and close buttons. Drag its title bar to move it, double-click the title bar to maximize, and use its edges to resize. Closing still hides it in the tray; **Quit AudioLink** waits for stream shutdown and default-device restoration. The app, executable and tray share the AudioLink waveform icon. **About** shows the installed version and links to documentation, issue reporting, the MIT license and third-party notices.

The activity log keeps a bounded history of status changes for the current app session. **Copy diagnostics** copies the product/build version, non-secret stream settings, available statistics and signal levels, and the last 50 activity entries. Server/device/user identifiers and the shared key are removed. Nothing is copied until you click the button; the report contains no audio recording. Live statistics are cleared when the connection ends or becomes unavailable. **Open server monitor** opens the server's optional HTTP monitor using its separate HTTP port, initially **7356**; [enable the server dashboard](INSTALL.md#optional-server-dashboard) first. UDP audio continues to use port 7355.

The desktop underruns and overruns are server jitter-buffer counters. Native sound-card recoveries appear separately in the server HTTP monitor and Linux journal; zero jitter counters alone do not prove uninterrupted physical playback.

**Sent level** shows separate left/right RMS bars in dBFS, one-second held peak markers and a clipping indicator. It measures actual PCM after the client gain adjustment and before UDP transmission; it does not measure the receiver's analog output. RMS uses a 300 ms window. Silent packets let the meters decay, and a stopped stream clears them. The visible client updates these meters every 50 ms; hidden/minimized windows stop UI polling. Gain and both meters stay visible above the footer even when the setup area scrolls.

## Audio modes

- **All PC audio:** WASAPI loopback from the selected playback device. Audio remains available on the local output.
- **Virtual cable:** captures CABLE Output and sets the Windows default playback device to CABLE Input while connected. Requires a separately installed VB-CABLE driver.
- **Other input:** captures the selected recording device without changing the Windows default playback device.

Some Windows applications use a fixed playback device. To route them through the virtual mode, select the system default or CABLE Input in those applications.

## If playback is quiet

Check the player volume, the application's level in the Windows volume mixer, and the playback device level. In virtual mode, **CABLE Input** is the playback device and **CABLE Output** is the capture device. Each can have its own level and mute setting. **Gain 100%** in AudioLink preserves the input level. Higher gain can distort a loud signal.

The Linux sound device's hardware mixer can also lower the level. If `amixer` is installed, find the card number in `/proc/asound/cards` and read its control:

```sh
amixer -c 1 sget Speaker
```

This command only reads the setting. Replace `1` with your card number and `Speaker` with its control name. If audio from the wrong application is playing, check that application's selected playback device. If late frames or underruns increase, raise **Target buffer**. A manually selected 10 ms leaves less margin for network variation than 30 ms.

## Set up VB-CABLE

VB-CABLE is a separate driver from VB-Audio. Download Pack45 from its [official site](https://vb-audio.com/Cable/), extract the entire archive, and run `VBCABLE_Setup_x64.exe` as administrator. You can check its signature first:

```powershell
Get-AuthenticodeSignature .\VBCABLE_Setup_x64.exe |
    Format-List Status, SignerCertificate
```

The signature should be valid and the signer should be BUREL VINCENT. Confirm installation in the separate installer and restart the PC if the vendor's instructions require it. Then click **Refresh** in AudioLink. Both CABLE Input and CABLE Output must be active.

The driver is not included in the AudioLink release. Its bundled license requires the author's consent before integration into another program's installer. AudioLink therefore links to the vendor and detects an existing installation. [VB-Audio](https://vb-audio.com/Services/licensing.htm) describes the terms and donationware model.

Before changing the default playback device, AudioLink records the previous device for all three Windows roles. It restores them on disconnect and exit, or at the next launch after a forced exit. It does not overwrite a manual device change made while streaming. If restoration fails because the previous device is unavailable, the application reports an error and keeps the backup at `%AppData%\NksAudioLink\default-playback-backup.json`.

## CLI

```powershell
dotnet run --project src/NksAudioLink.Cli -- devices
dotnet run --project src/NksAudioLink.Cli -- discover
dotnet run --project src/NksAudioLink.Cli -- send --server SERVER_IP --seconds 15 --gain 0.5
dotnet run --project src/NksAudioLink.Cli -- test-tone --server SERVER_IP --seconds 10 --gain 0.01
```

`send` supports `--mode loopback|capture`, `--device ID`, `--latency MS`, `--port`, `--config FILE`, and `--takeover true` to deliberately take over a busy server. Without `--seconds`, it runs until Ctrl+C. `--gain` accepts a value from 0 to 4 and uses a decimal point. The CLI does not change the default playback device when it captures an input; the desktop application handles automatic routing.
