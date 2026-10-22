"""Encode actual Unity camera captures, retaining their measured wall-clock timing."""
import csv
import json
import statistics
import subprocess
import sys
from pathlib import Path
import imageio_ffmpeg

folder = Path(sys.argv[1]).resolve()
frames = list(csv.DictReader((folder / 'frames.csv').open()))
ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
for view in range(3):
    manifest = folder / f'view{view}.ffconcat'
    with manifest.open('w', encoding='utf-8') as f:
        f.write('ffconcat version 1.0\n')
        for i, row in enumerate(frames):
            name = f"view{view}-{int(row['index']):05d}.jpg"
            duration = float(frames[i+1]['wall_seconds'])-float(row['wall_seconds']) if i+1<len(frames) else .5
            f.write(f"file '{name}'\nduration {max(.01,duration):.3f}\n")
        f.write(f"file '{name}'\n")
    subprocess.run([ffmpeg,'-y','-loglevel','error','-safe','0','-i',str(manifest),'-r','15','-c:v','libx264','-preset','fast','-crf','21','-pix_fmt','yuv420p','-movflags','+faststart',str(folder/f'view{view}-recording.mp4')],check=True)
subprocess.run([ffmpeg,'-y','-loglevel','error','-i',str(folder/'view0-recording.mp4'),'-i',str(folder/'view1-recording.mp4'),'-filter_complex','hstack=inputs=2','-c:v','libx264','-crf','21','-pix_fmt','yuv420p','-movflags','+faststart',str(folder/'kitchen-and-door.mp4')],check=True)
rows=list(csv.DictReader((folder/'runtime.csv').open()))
fps=[]
for previous,current in zip(rows,rows[1:]):
    dt=float(current['wall_seconds'])-float(previous['wall_seconds'])
    if dt>0:fps.append(int(current['frames'])/dt)
report={'frames':len(frames),'simulation_seconds':float(frames[-1]['sim_seconds']),'wall_seconds':float(frames[-1]['wall_seconds']),'capture_fps':len(frames)/float(frames[-1]['wall_seconds']),'editor_fps_median_recording':statistics.median(fps),'max_a_alive':max(int(r['a_alive']) for r in rows),'max_b_alive':max(int(r['b_alive']) for r in rows),'max_total_alive':max(int(r['a_alive'])+int(r['b_alive']) for r in rows)}
(folder/'recording-summary.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
