"""Regenerate the original synthetic sample. Optional authoring tool: Python 3 and Pillow."""
from pathlib import Path
import math
import random
from PIL import Image, ImageDraw

def main():
    w,h=1440,960
    rng=random.Random(151)
    im=Image.new("RGB",(w,h))
    draw=ImageDraw.Draw(im)
    def mix(a,b,v):return tuple(round(x+(y-x)*v) for x,y in zip(a,b))
    horizon=548
    for y in range(h):
        if y<horizon:
            t=y/horizon
            color=mix((53,91,133),(250,182,140),t**1.15)
        else:color=mix((113,148,158),(33,75,93),((y-horizon)/(h-horizon))**.75)
        draw.line((0,y,w,y),fill=color)
    sunx,suny=963,343
    draw.ellipse((sunx-45,suny-45,sunx+45,suny+45),fill=(252,219,168))
    layers=[(452,92,(144,142,164),1.8),(487,67,(94,116,142),5.3),(524,45,(63,92,113),2.6)]
    for base,amplitude,color,phase in layers:
        edge=[]
        for x in range(-3,w+5,3):
            v=(math.sin(x/137+phase)*.54 + math.sin(x/77+phase*1.75)*.25 + math.sin(x/28+phase)*.13+math.sin(x/11)*.055)
            edge.append((x,base+int(amplitude*v)))
        draw.polygon(edge+[(w,horizon),(0,horizon)],fill=color)
    for _ in range(6800):
        y=rng.randrange(horizon,h);x=rng.randrange(w);t=(y-horizon)/(h-horizon)
        glint=abs(x-sunx)<(28+180*t)*rng.uniform(.4,1.4)
        c=mix(im.getpixel((x,y)),(235,192,149) if glint else (163,183,184),rng.uniform(.06,.3) if glint else rng.uniform(.02,.13))
        draw.line((x,y,x+rng.randrange(4,34),y),fill=c)
    for side,base,span,slope in [(0,594,540,.55),(w,586,336,.45)]:
        sign=1 if side==0 else -1
        outline=[(side+sign*x,int(base-(span-x)*slope*.17+6*math.sin(x/41))) for x in range(0,span,4)]
        draw.polygon(outline+[(side,base+43)],fill=(40,68,76))
        for i in range(138):
            n=rng.random(); x=side+sign*span*n; yy=int(base-(span-span*n)*slope*.17+6*math.sin(span*n/41)); ht=int(5+(1-n)*rng.uniform(24,93));c=(37,65,72)
            for k in [0,.21,.42]:
                top=yy-ht+ht*k;wide=ht*(.075+.1*k);draw.polygon([(x,top),(x-wide,top+ht*.57),(x+wide,top+ht*.57)],fill=c)
    # Single sail and reflection supply a small neutral reference in the lake.
    x,y=913,653
    draw.polygon([(x,y-80),(x-1,y-5),(x-43,y-5)],fill=(239,225,199))
    draw.polygon([(x+4,y-64),(x+5,y-5),(x+24,y-5)],fill=(210,151,111))
    draw.polygon([(x-47,y),(x+32,y),(x+21,y+7),(x-33,y+7)],fill=(46,64,68))
    target=Path(__file__).resolve().parents[1]/'examples'/'Stillwater.png'
    target.parent.mkdir(exist_ok=True);im.save(target,format="PNG",optimize=True)
if __name__=="__main__":main()
