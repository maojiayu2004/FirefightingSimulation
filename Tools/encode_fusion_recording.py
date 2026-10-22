"""Encode real Unity overlay captures using their measured wall-clock intervals."""
import csv
from pathlib import Path
import subprocess
import sys
import imageio_ffmpeg

folder = Path(sys.argv[1]).resolve()
rows = list(csv.DictReader((folder / "frames.csv").open()))
manifest = folder / "overlay.ffconcat"
with manifest.open("w", encoding="utf-8") as out:
    out.write("ffconcat version 1.0\n")
    for index, row in enumerate(rows):
        name = f"overlay-{int(row['index']):05d}.png"
        duration = float(rows[index + 1]["wall_seconds"]) - float(row["wall_seconds"]) if index + 1 < len(rows) else 1
        out.write(f"file '{name}'\nduration {max(.01, duration):.3f}\n")
    out.write(f"file '{name}'\n")
subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-loglevel", "error", "-safe", "0", "-i", str(manifest), "-r", "15", "-c:v", "libx264", "-crf", "21", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(folder / "smoke-fusion.mp4")], check=True)
print(f"Encoded {len(rows)} actual captures; simulation {rows[-1]['sim_seconds']} s, wall {rows[-1]['wall_seconds']} s")
