"""Create deterministic flash/click media. Does not play it or measure physical latency."""
import argparse
import array
import hashlib
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys
import wave

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True, help='Directory for generated media (keep it outside the repository)')
parser.add_argument('--verify', action='store_true', help='Decode and verify the generated PCM and flash frames')
args = parser.parse_args()
out = args.output.resolve()
out.mkdir(parents=True, exist_ok=True)
ffmpeg = shutil.which('ffmpeg')
if not ffmpeg:
    raise SystemExit('ffmpeg must be available on PATH')
rate, fps, duration = 48000, 60, 20
events = list(range(1, duration, 2))
samples = array.array('h', [0]) * (rate * duration * 2)
for second in events:
    # 10 ms, -12 dBFS peak, integer number of 1 kHz cycles; no microphone needed.
    for index in range(480):
        value = round(8191 * math.sin(2 * math.pi * index / 48))
        start = (second * rate + index) * 2
        samples[start] = samples[start + 1] = value
if sys.byteorder != 'little':
    samples.byteswap()
wav = out / 'flash-click-source.wav'
with wave.open(str(wav), 'wb') as stream:
    stream.setnchannels(2)
    stream.setsampwidth(2)
    stream.setframerate(rate)
    stream.writeframes(samples.tobytes())
video_filter = 'drawbox=x=480:y=200:w=320:h=320:color=white:t=fill:enable=gte(n\\,60)*lt(mod(n-60\\,120)\\,3)'
mkv = out / 'flash-click-lossless-audio.mkv'
subprocess.run([ffmpeg, '-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i',
    'color=c=0x0F1B28:s=1280x720:r=60:d=20', '-i', str(wav), '-vf', video_filter,
    '-c:v', 'libx264', '-preset', 'fast', '-crf', '18', '-pix_fmt', 'yuv420p',
    '-c:a', 'pcm_s16le', '-t', '20', str(mkv)], check=True)
mp4 = out / 'flash-click.mp4'
subprocess.run([ffmpeg, '-hide_banner', '-loglevel', 'error', '-y', '-i', str(mkv),
    '-c:v', 'copy', '-c:a', 'aac', '-b:a', '256k', '-movflags', '+faststart', str(mp4)], check=True)
manifest = {'durationSeconds': duration, 'videoFps': fps, 'audioSampleRate': rate,
    'audioChannels': 2, 'flashFrames': 3, 'clickSamples': 480,
    'eventSeconds': events, 'firstFlashFrame': 60, 'firstNonzeroAudioSample': rate + 1,
    'sourceAlignmentMilliseconds': 1000 / rate,
    'primaryMedia': mkv.name,
    'compatibleMedia': mp4.name,
    'notes': 'MKV retains exact PCM. MP4 uses AAC and may spread transient energy. '
             'Playback/display/speaker delays and physical synchronization require observation. '
             'Generating this file does not constitute a synchronization pass.',
    'sha256': {file.name: hashlib.sha256(file.read_bytes()).hexdigest() for file in (wav, mkv, mp4)}}
(out / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print(json.dumps(manifest, indent=2))

if args.verify:
    from verify_flash_click import verify
    result = verify(mkv, wav)
    (out / 'verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))