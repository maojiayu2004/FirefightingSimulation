"""Pixel evidence from smoke-only Unity cameras. Occupancy is not physical concentration."""
from pathlib import Path
from PIL import Image
import json
import sys

root=Path(sys.argv[1]).resolve()
report={}
for run in ('before','after','validation'):
    folder=root/run
    if not folder.exists():continue
    measurements={}
    for path in sorted(folder.glob('*.png')):
        if not (path.name.startswith('view2-') or path.name.startswith('diagnostic0-') or path.name.startswith('diagnostic1-') or path.name.startswith('diagnostic2-')):continue
        image=Image.open(path).convert('RGB')
        values=[max(rgb) for rgb in image.getdata()]
        metrics={'width':image.width,'height':image.height,'max_channel':max(values),'mean_max_channel':sum(values)/len(values),'pixels_over_5':sum(v>5 for v in values),'coverage_over_5_percent':100*sum(v>5 for v in values)/len(values),'coverage_over_15_percent':100*sum(v>15 for v in values)/len(values)}
        if path.name.startswith('diagnostic2-'):
            center=[];rim=[]
            for y in range(image.height):
                z=4.5+(.5-(y+.5)/image.height)*11
                for x in range(image.width):
                    wx=.5+((x+.5)/image.width-.5)*(11*image.width/image.height)
                    value=values[y*image.width+x]
                    if -3<wx<2 and 5<z<8:center.append(value)
                    if -4.5<wx<3.45 and 3.6<z<9.5 and min(wx+4.5,3.45-wx,z-3.6,9.5-z)<.6:rim.append(value)
            metrics['central_roi_coverage_over_5_percent']=100*sum(v>5 for v in center)/len(center)
            metrics['central_roi_mean']=sum(center)/len(center)
            metrics['wall_rim_mean']=sum(rim)/len(rim)
            metrics['rim_to_center_mean_ratio']=metrics['wall_rim_mean']/max(.001,metrics['central_roi_mean'])
        measurements[path.name]=metrics
    report[run]=measurements
(root/'pixel-measurements.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
