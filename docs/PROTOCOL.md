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
| 1 | `format` (1 = manual s16le; 0x81 = legacy reactive adaptive; 0x82 = predictive adaptive) |
| 1 | `frameMs` (5) |
| 2 | Manual `targetLatencyMs` (for example, 30), or packed adaptive settings for format 0x81/0x82 |
| 1 | Client name length N |
| N | UTF-8 client name (up to 64 bytes) |

The client sends `HELLO` at startup and every 2 seconds afterward to keep the session alive. The server replies with `STATS` (accepted) or `REJECT`.

Manual format **1** retains its original byte layout and accepts a 10–200 ms target in 5 ms steps. Adaptive operation is opt-in. Formats **0x81** (RC1 reactive policy) and **0x82** (predictive policy) both carry stereo s16le PCM; they select a buffer-control capability, not a different audio encoding. The body length and UTF-8 name layout are unchanged. Their two-byte target field packs three values, measured in **5 ms units**:

| Bits | Value |
|---|---|
| 0–5 | Initial target / 5 |
| 6–10 | Minimum target / 5 |
| 11–15 | Maximum target / 5 |

All adaptive values must be multiples of 5 ms in **20–100 ms**, with `minimum <= initial <= maximum`. Example: initial 40 ms, minimum 20 ms and maximum 100 ms encode as `8 | (4 << 6) | (20 << 11) = 0xA108`, transmitted as bytes `08 A1`. Malformed ranges are rejected before opening the output. Format 1 never interprets these bits as adaptive settings. Periodic HELLOs retain the initial request; they do not reset the evolving target of an existing session.

A pre-adaptive server parses the unchanged HELLO layout but rejects 0x81/0x82 with `REJECT UnsupportedFormat`. RC1 accepts 0x81 and explicitly rejects 0x82. A predictive-capable server keeps format-1 manual and format-0x81 reactive clients working with their original responses. A predictive client must request 0x82 and wait for accepted extended STATS with marker 0xA2 and matching bounds before sending audio. Neither plain 33-byte acceptance nor legacy marker 0xA1 confirms predictive support. Rejection requires an explicit new connection action, with adaptive mode disabled or a compatible server selected. The current predictive client chooses its independent 40 ms initial target, clamped to the negotiated bounds; a saved manual target is not a predictive input.

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

The fields above total **33 bytes** and remain unchanged for manual sessions. Adaptive sessions receive **45 bytes**, retaining that exact 33-byte prefix and appending:

| Body offset | Size | Field |
|---|---:|---|
| 33 | 1 | Extension marker `0xA1` for legacy reactive / `0xA2` for predictive |
| 34 | 1 | Reason: 0 initial, 1 jitter underrun, 2 packet loss, 3 late packet, 4 stable playback, 5 maximum reached, 6 predicted jitter risk (0xA2 only) |
| 35 | 2 | Current target in milliseconds |
| 37 | 2 | Negotiated minimum target in milliseconds |
| 39 | 2 | Negotiated maximum target in milliseconds |
| 41 | 4 | Number of target changes in this session |

Extended status uses ordinary millisecond values, not packed 5 ms units. Receivers reject unknown markers/reasons, reason 6 with legacy marker 0xA1, invalid bounds/targets and partial or oversized bodies. A server only sends predictive reasons/marker to a client that requested format 0x82; RC1 clients never receive the new marker. `bufferMs` remains measured occupancy and may differ from the current target during a transition. Native ALSA device errors are separate HTTP/journal diagnostics and never count as adaptive network-error evidence.

Format 0x81 retains the RC1 policy: increase up to 10 ms on new jitter underrun/loss/late-packet counters, with a 400-output-frame cooldown; decrease 5 ms after 6,000 clean full output frames.

Format 0x82 predicts need from monotonic UDP arrival timestamps relative to source sample positions. A fixed 512-packet window (about 2.56 seconds at full rate) estimates transit spread as P98 minus P10, adds a 15 ms safety margin and up to 10 ms of rising-spread forecast. A slow EWMA spread baseline supplies the forecast; the resulting estimate smooths upward faster than downward. It evaluates at most every 100 ms, after at least 100 packets and 500 ms of warmup. Unique reordered packets contribute transit observations without moving the forward source reference. Recent duplicates and packets older than the bounded tracking window do not. Source-index gaps distinguish missing packets from arrival-time variation. Arrival/source discontinuities above 250 ms reset learning, preserving the current target and session.

An increase requires three consecutive estimates at least 5 ms above target and uses the same maximum 10 ms step / 400-output-frame cooldown. A decrease requires 6,000 consecutive clean full output frames while estimated need stays at or below the next target (current target minus 5 ms), then lowers the target by 5 ms. The estimate already includes the 15 ms safety margin; RC3 removes RC2's additional 5 ms downward reserve so a quiet link can reach the negotiated minimum. Genuine underrun/loss/unique-late events retain a bounded reactive fallback; startup losses before the first full output frame and duplicate lateness do not count as predictive risk. Waiting for input cannot count as stability; valid silent PCM frames can. The separate increase/decrease thresholds and persistence prevent immediate reversal near the threshold. Rich estimates, warmup state and hysteresis progress are exposed through HTTP/history, while UDP retains the 45-byte acknowledgment/status layout.

After an actual predictive-session underrun/loss/unique-late event, decreases are additionally held for 24,000 clean full output frames (120 seconds). While that hold is active, a recent tail requirement (window maximum transit minus P10, plus 15 ms) above the next target renews it. Thus rare recurring delays filtered out of P98 can preserve a learned safety target without raising it by themselves. Harmless tail variation that fits the next target does not renew the hold. The hold expires after threatening tails disappear and clean playback continues; normal 30-second settling then governs subsequent steps. HTTP reports `FailureHold` during this protection. A new session/reset clears the learned hold.

Both policies keep the existing resampler's phase, bounded ratio and slew while shifting occupancy smoothly. An actual underrun refills to the updated target. Neither policy reconnects, resets source indices nor discards frames simply to reach a smaller target. Prediction cannot guarantee recovery from previously unseen or unbounded network delays.

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
