"""Check a server WAV artifact from `nksaudio test-tone --seconds 5`."""

import array
import math
import sys
import wave


def energy(samples, frequency, rate):
    coefficient = 2 * math.cos(2 * math.pi * frequency / rate)
    previous = current = 0.0
    for sample in samples:
        next_value = sample + coefficient * current - previous
        previous, current = current, next_value
    return previous * previous + current * current - coefficient * previous * current


with wave.open(sys.argv[1], "rb") as recording:
    assert recording.getnchannels() == 2
    assert recording.getsampwidth() == 2
    assert recording.getframerate() == 48000
    duration = recording.getnframes() / recording.getframerate()
    assert 4.8 <= duration <= 5.2, duration
    recording.setpos(2 * recording.getframerate())
    pcm = array.array("h", recording.readframes(recording.getframerate()))

left = pcm[::2]
rms = math.sqrt(sum(sample * sample for sample in left) / len(left))
energies = {hz: energy(left, hz, 48000) for hz in (400, 440, 480)}
assert rms > 500, rms
assert energies[440] > 50 * max(energies[400], energies[480]), energies
print(f"duration={duration:.3f}s rms={rms:.1f} dominant=440Hz")
