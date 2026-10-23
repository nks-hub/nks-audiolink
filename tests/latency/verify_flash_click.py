"""Verify source media timing; this does not measure playback or physical latency."""
import argparse
import array
import json
from pathlib import Path
import shutil
import subprocess
import sys
import wave


def verify(media: Path, source: Path) -> dict:
    ffmpeg = shutil.which('ffmpeg')
    ffprobe = shutil.which('ffprobe')
    if not ffmpeg or not ffprobe:
        raise RuntimeError('ffmpeg and ffprobe must be available on PATH')
    # Decoded sample/frame order alone would miss a muxed track offset.
    video_timing = json.loads(subprocess.check_output([
        ffprobe, '-v', 'error', '-select_streams', 'v:0', '-show_frames',
        '-show_entries', 'frame=best_effort_timestamp_time', '-of', 'json', str(media)]))
    frame_times = [float(frame['best_effort_timestamp_time']) for frame in video_timing['frames']]
    if len(frame_times) != 1200 or any(abs(time - index / 60) > 0.00051 for index, time in enumerate(frame_times)):
        raise AssertionError('Video timestamps do not describe a continuous 60 fps track starting at zero')
    audio_timing = json.loads(subprocess.check_output([
        ffprobe, '-v', 'error', '-select_streams', 'a:0', '-show_packets',
        '-show_entries', 'packet=pts_time,size', '-of', 'json', str(media)]))
    audio_frames = 0
    for packet in audio_timing['packets']:
        if abs(float(packet['pts_time']) - audio_frames / 48000) > 0.00051:
            raise AssertionError('PCM packet timestamps contain a track offset or discontinuity')
        size = int(packet['size'])
        if size % 4:
            raise AssertionError('PCM packet ends between stereo sample frames')
        audio_frames += size // 4
    if audio_frames != 960000:
        raise AssertionError('PCM packet count does not describe 20 seconds')
    pixels = subprocess.check_output([
        ffmpeg, '-v', 'error', '-i', str(media),
        '-vf', 'crop=2:2:640:360,format=gray', '-an', '-f', 'rawvideo', '-'])
    if len(pixels) != 20 * 60 * 4:
        raise AssertionError(f'Expected 1200 video frames, got {len(pixels) // 4}')
    frames = [i // 4 for i in range(0, len(pixels), 4) if pixels[i] > 200]
    expected = [60 + event * 120 + offset for event in range(10) for offset in range(3)]
    if frames != expected:
        raise AssertionError(f'Flash timing differs: {frames}')
    pcm = subprocess.check_output([
        ffmpeg, '-v', 'error', '-i', str(media), '-vn', '-f', 's16le',
        '-acodec', 'pcm_s16le', '-'])
    with wave.open(str(source), 'rb') as original:
        if (original.getnchannels(), original.getsampwidth(), original.getframerate(), original.getnframes()) != (2, 2, 48000, 960000):
            raise AssertionError('Unexpected source PCM format or duration')
        if pcm != original.readframes(original.getnframes()):
            raise AssertionError('PCM changed during media encoding/decoding')
    samples = array.array('h', pcm)
    if sys.byteorder != 'little':
        samples.byteswap()
    starts = []
    for frame in range(len(samples) // 2):
        if samples[frame * 2] != samples[frame * 2 + 1]:
            raise AssertionError(f'Left/right channels differ at frame {frame}')
        if samples[frame * 2] and (not starts or frame - starts[-1] > 480):
            starts.append(frame)
    if starts != [second * 48000 + 1 for second in range(1, 20, 2)]:
        raise AssertionError(f'Click timing differs: {starts}')
    return {'decodedFlashFrames': frames, 'decodedClickStartSamples': starts,
            'pcmRoundTripExact': True, 'containerTimestampsVerified': True,
            'flashStartSeconds': [frame_times[frame] for frame in frames[::3]],
            'physicalSyncAssessed': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('media', type=Path)
    parser.add_argument('source', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    result = json.dumps(verify(args.media, args.source), indent=2)
    if args.report:
        args.report.write_text(result, encoding='utf-8')
    print(result)
