# Install the Linux server

The server is a C#/.NET 9 console application. On Linux, it writes directly to ALSA through `libasound.so.2`. It does not require PulseAudio or PipeWire. These instructions target an x86_64 Debian or Proxmox host with a physical sound device available through `/dev/snd`.

## Requirements

- The build machine needs .NET SDK 9.0.318 or a later patch in the same series, as specified by `global.json`. The target needs `libasound.so.2`, systemd, an `audio` group, and a working ALSA device. A `--self-contained` build does not require a .NET runtime on the target.
- The target must accept UDP on the selected port, 7355 by default. Allow traffic only from a trusted LAN or VPN. PCM audio is not encrypted.
- Before changing the host, check that another application has not reserved the sound device. The installer does not open the device or start or restart the service.

## Build

From the repository root:

```sh
dotnet publish src/NksAudioLink.Server/NksAudioLink.Server.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish/linux-x64
```

Copy `publish/linux-x64/NksAudioLink.Server` and the `deploy/linux` directory to the target. Run the following command there in a root shell. On a machine with `sudo`, you can use `sudo sh ...`. The installer sets permissions on the installed binary.

```sh
sh deploy/linux/install.sh ./NksAudioLink.Server
```

The installer creates the unprivileged `nks-audiolink` user in the `audio` group. It places the binary in `/opt/nks-audiolink`, the systemd unit in `/etc/systemd/system`, and a sample configuration in `/etc/nks-audiolink/server.json` on first installation. Re-running it preserves the configuration and sets its ownership and mode to `root:nks-audiolink 0640`. It rejects symbolic links. Binary replacement is atomic, so files can be updated while the service runs; the new version takes effect after a planned restart. The installer only calls `systemctl daemon-reload`.

## Configure and start

Edit `/etc/nks-audiolink/server.json` before the first start. The sample allows only `127.0.0.0/8`. Set `allowCidrs` to the range of trusted clients. Do not use `0.0.0.0/0` without considering who can reach the server. To authenticate packets, set the same `pskBase64` on the server and client. The key must contain at least 16 random bytes. Keep configurations containing keys out of Git, and restrict access to root and the service group.

Set `sink` to `alsa:default` or, for example, `alsa:plughw:CARD=Device,DEV=0`. Find the ALSA identifier in `/proc/asound/cards` or the output of `NksAudioLink.Server devices`. `plughw` lets ALSA convert the format when a device does not directly accept stereo S16_LE at 48 kHz. The `audio` group must have access to the device. For a removable USB device, prefer a stable ALSA identifier over a card number, which can change after a reboot.

```sh
systemctl enable --now nks-audiolink
systemctl status nks-audiolink
journalctl -u nks-audiolink -f
```

For an update, run the installer again and restart the service at an agreed time:

```sh
systemctl restart nks-audiolink
```

The server opens ALSA only when it accepts a session. `BYE` ends the session immediately. The server releases the sound device after `idleReleaseSec` from the last packet; the default is 5 seconds. For a short isolated test without a sound device, run the server manually with `--sink null` or `--sink wav:/tmp/tone.wav`. The process user must be able to write the WAV file.

### CPU settings

The Linux build sets `System.Threading.ThreadPool.UnfairSemaphoreSpinLimit=0` to stop idle threads from spinning. The systemd unit also sets `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1` and `DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT=1`. Network completions for one UDP socket then run on one socket thread. A separate playback thread still writes audio to the device.

In an isolated test without a sound device on .NET 9.0.20, these settings reduced CPU use from 3.87% to 1.40% of one core, with no underruns or late frames. During a ten-minute stream to physical ALSA with a 30 ms target buffer, the server used 1.262% of one core and every sampled client error counter remained zero. A separate test at 10 ms produced late frames. Opening and closing the output on `HELLO` may briefly hold up the network thread. To compare settings on your host, override them in a systemd drop-in and check the statistics after a planned restart. Running the binary directly does not inherit those two systemd environment variables.

The [.NET 9.0.20 implementation](https://github.com/dotnet/runtime/blob/v9.0.20/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEngine.Unix.cs) documents the socket behavior.

## Verify

```sh
/opt/nks-audiolink/NksAudioLink.Server devices
getent group audio
id nks-audiolink
```

On Windows, send a test tone with the client CLI. Supply the address and, if needed, the key for your network.

```powershell
dotnet run --project src/NksAudioLink.Cli -- test-tone --server SERVER_IP --seconds 10
```

The CLI prints buffer occupancy, underruns, overruns, and lost packets. To check the physical output, select the receiver input connected to the sound device and listen for the tone. Starting the service alone does not verify sustained playback or latency. If late frames or underruns increase, raise the target buffer. The default 30 ms provides more margin than a manually selected 10 ms.

For problems, check `journalctl -u nks-audiolink`, access to `/dev/snd`, the `sink` and `allowCidrs` settings, and the incoming UDP port. `ALSA open` often means that the device is absent or busy. The service does not need PulseAudio, PipeWire, or a desktop session.
