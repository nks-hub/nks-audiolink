# NKS AudioLink transport protocol v1

Transport is UDP on port **7355** by default. All multibyte integers are **little-endian**. Each datagram carries one packet. Packets are smaller than 1,400 bytes to avoid fragmentation on a typical network.

## Common header (16 bytes)

| Offset | Size | Field | Description |
|---|---:|---|---|
| 0 | 4 | `magic` | ASCII `NKAL` |
| 4 | 1 | `version` | `1` |
| 5 | 1 | `type` | See packet types below |
| 6 | 2 | `flags` | Bit flags listed below |
| 8 | 4 | `session` | Random session ID chosen by the client for `HELLO` |
| 12 | 4 | `seq` | Packet sequence number within a session, used for loss statistics |

With the `AUTH` flag, the packet ends in a **16-byte authentication tag**: the first 16 bytes of HMAC-SHA256(psk, the entire packet without the tag). A server configured with a key silently drops packets without a valid tag. It compares tags in constant time.

### Flags

| Bit | Name | Meaning |
|---|---|---|
| 0 | `AUTH` | Packet carries an HMAC authentication tag |
| 1 | `SILENT` | `AUDIO` frame is silent; PCM data is omitted |
| 2 | `TAKEOVER` | `HELLO` takes over a server with another active client |

## Packet types

| Code | Type | Direction |
|---:|---|---|
| 1 | `HELLO` | Client → server |
| 2 | `AUDIO` | Client → server |
| 3 | `BYE` | Client → server |
| 4 | `STATS` | Server → client |
| 5 | `DISCOVER` | Client → broadcast |
| 6 | `DISCOVER_REPLY` | Server → client |
| 7 | `PING` | Client → server |
| 8 | `PONG` | Server → client |
| 9 | `REJECT` | Server → client |

### HELLO body

| Size | Field |
|---:|---|
| 4 | `sampleRate` (48000) |
| 1 | `channels` (2) |
| 1 | `format` (1 = s16le) |
| 1 | `frameMs` (5) |
| 2 | `targetLatencyMs` (for example, 30) |
| 1 | Client name length N |
| N | UTF-8 client name (up to 64 bytes) |

The client sends `HELLO` at startup and every 2 seconds afterward to keep the session alive. The server replies with `STATS` (accepted) or `REJECT`.

### AUDIO body

| Size | Field |
|---:|---|
| 8 | `sampleIndex`: first sample index of the frame since the start of the session |
| 2 | `frameCount`: samples per channel in the frame |
| 0 or frameCount × channels × 2 | Interleaved PCM s16le; empty when `SILENT` is set |

The server places frames by `sampleIndex`. It drops a frame whose index is older than the current playback position and increments `late`. After a gap has expired, it fills the missing positions with silence and increments `lost`.

### BYE

No body. The server ends the session immediately and releases the device after `idleReleaseSec`.

### STATS body (server → client, once per second)

| Size | Field |
|---:|---|
| 1 | `accepted` (1/0) |
| 2 | `bufferMs`: current jitter buffer occupancy |
| 2 | `sinkDelayMs`: output queue delay (ALSA `snd_pcm_delay`) |
| 4 | `underruns` |
| 4 | `overruns` |
| 4 | `lost` |
| 4 | `late` |
| 4 | `ratioPpm`: signed clock drift correction in parts per million |
| 8 | `echoTicks`: timestamp from the client's latest `PING` for RTT calculation; otherwise 0 |

### DISCOVER / DISCOVER_REPLY

`DISCOVER` has no body. It is sent to `255.255.255.255:7355` **and** the directed broadcast address of each active IPv4 interface. This supports PCs with multiple network adapters.

`DISCOVER_REPLY`: `nameLen(1) name`, `version(1)`, `sampleRate(4)`, `channels(1)`, `authRequired(1)`, `sinkLen(1) sink`. The final `sink` string describes the output.

### PING / PONG

`PING`: `ticks(8)` contains the client's `Stopwatch.GetTimestamp()`. `PONG` echoes it so the client can calculate RTT.

### REJECT

`reason(1)`: 1 = busy, 2 = unsupported format, 3 = address denied, 4 = bad authentication. An authentication rejection is sent only if the server configuration allows it; otherwise the packet is silently dropped.

## Session timeline

```text
client                           server
  | HELLO -----------------------> | opens output, buffer empty
  | <----------------------- STATS | accepted=1
  | AUDIO idx=0 -----------------> |
  | AUDIO idx=240 ---------------> | fills to targetLatencyMs, then plays
  | AUDIO SILENT idx=... --------> | PC is silent; stream continues and buffer stays filled
  | HELLO (keepalive, 2 s) ------> |
  | <----------------------- STATS | once per second
  | BYE -------------------------> | session ends
```

If the client sends no packet for 3 seconds, the server ends the session.
