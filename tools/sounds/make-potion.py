# Makes Elixar's Potion sounds (sounds/potion-*.wav): wood notes, bubbles, a cork pop, fizz and swooshes, all synthesized.
# python3 tools/sounds/make-potion.py   (needs numpy)
import os, sys; here = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, here)
from soundlib import *
import numpy as np
rng=np.random.default_rng(7)
def bubble(f0, dur=0.07, rise=2.2, vol=1.0):
    n=int(dur*SR); t=np.arange(n)/SR
    f=f0*(1+(rise-1)*(t/dur)**1.5)            # pitch slides up like a rising bubble
    ph=2*np.pi*np.cumsum(f)/SR
    e=np.exp(-t/(dur*0.45)); e[:int(.002*SR)]*=np.linspace(0,1,int(.002*SR))
    return np.sin(ph)*e*vol
def shaped_noise(dur, centers, width=0.35, vol=1.0, envshape=None):
    # noise whose colour follows a list of centre frequencies over time (a swoosh)
    n=int(dur*SR); hop=256; win=1024
    out=np.zeros(n+win); w=np.hanning(win)
    frames=(n)//hop
    fr=np.fft.rfftfreq(win,1/SR)
    for k in range(frames):
        c=np.interp(k/frames, np.linspace(0,1,len(centers)), centers)
        x=rng.standard_normal(win)*w
        X=np.fft.rfft(x)*np.exp(-0.5*(np.log(np.maximum(fr,1)/c)/width)**2)
        out[k*hop:k*hop+win]+=np.fft.irfft(X)*w
    out=out[:n]
    if envshape is not None:
        out*=np.interp(np.linspace(0,1,n), np.linspace(0,1,len(envshape)), envshape)
    return out/ (np.max(np.abs(out))+1e-9)*vol
def cork(vol=1.0):
    n=int(0.09*SR); t=np.arange(n)/SR
    thump=np.sin(2*np.pi*np.cumsum(180*np.exp(-t/0.02)+90)/SR)*np.exp(-t/0.025)
    click=shaped_noise(0.09,[2500,1500],0.5,1,[1,0.3,0.05,0,0,0])
    return (thump*0.9+click*0.5)*vol
def sparkle(dur=0.6, count=9, lo=1800, vol=0.5, start=0):
    out=np.zeros(int(dur*SR))
    scale=[1046.5,1174.7,1318.5,1568,1760,2093,2349,2637,3136]
    for i in range(count):
        f=scale[rng.integers(len(scale))]*(2 if lo>2000 else 1)
        s=note(f,0.25,'minimal',vol*(1-i/count*0.6))
        st=int((start+i*dur/count*0.8+rng.uniform(0,0.02))*SR)
        out[st:st+len(s)]+=s[:max(0,len(out)-st)]
    return out
def mix(total, parts):
    out=np.zeros(int(total*SR))
    for st,s in parts:
        s=s.copy(); k=min(len(s),int(0.03*SR)); s[-k:]*=np.linspace(1,0,k)**2
        i=int(st*SR); m=min(len(s),len(out)-i); out[i:i+m]+=s[:m]
    return out
def w(f,d=0.6,v=1.0): return note(f,d,'wood',v)
N.update({'D5':587.33,'B5':987.77,'D6':1174.66,'G6':1567.98})
S={}
S['info']=mix(0.8,[(0,bubble(420,0.08,2.0,.8)),(0.05,w(N['E5']))])
S['question']=mix(0.9,[(0,bubble(380,0.07,1.9,.7)),(0.09,bubble(520,0.07,2.1,.8)),(0.12,w(N['C5'],.6,.6)),(0.22,w(N['G5']))])
S['warning']=mix(0.95,[(0,w(N['A4'])),(0.02,bubble(220,0.1,1.6,.6)),(0.18,w(N['A4'],.6,.85)),(0.2,bubble(240,0.1,1.6,.5))])
S['error']=mix(1.0,[(0,w(N['E5'])),(0.11,w(N['B4'],.6,.95)),(0.22,w(N['F4'],.6,.9))]+[(0.03+i*0.07,bubble(320-i*45,0.08,0.7,.45)) for i in range(4)])  # sinking glugs
S['done']=mix(1.6,[(0.0+i*0.06,bubble(300+i*90,0.06,2.3,.45)) for i in range(6)]+[(0.35,w(N['C5'],.6,.8)),(0.46,w(N['E5'],.6,.85)),(0.57,w(N['G5'],.6,.9)),(0.68,w(N['C6'],.8,1)),(0.7,sparkle(0.6,8,1800,.35))])
S['alert']=mix(1.1,[(0,shaped_noise(0.45,[5000,7000,6000],0.4,.35,[0,1,.6,.3,0])),(0.05,w(N['B5'])),(0.14,w(N['G5'],.6,.9)),(0.26,w(N['B5'])),(0.35,w(N['G5'],.6,.9))])
S['scan']=mix(0.9,[(0,shaped_noise(0.28,[500,2500],0.45,.55,[0,1,.8,0])),(0.22,w(N['G5'],.6,.9)),(0.31,w(N['C6']))])
S['undo']=mix(1.0,[(0,shaped_noise(0.3,[2600,500],0.45,.55,[0,.8,1,0])),(0.22,w(N['G5'],.6,.9)),(0.3,w(N['E5'],.6,.9)),(0.38,w(N['C5'],.7,1))])
S['update']=mix(1.1,[(0,sparkle(0.5,7,2100,.4)),(0.25,w(N['G5'],.6,.85)),(0.38,w(N['C6'])),(0.51,w(N['G5'],.6,.8))])
S['better']=mix(1.4,[(i*0.07,bubble(280+i*70,0.06,2.4,.5)) for i in range(5)]+[(0.1,w(N['C5'],.6,.8)),(0.17,w(N['D5'],.6,.82)),(0.24,w(N['E5'],.6,.85)),(0.31,w(N['G5'],.6,.9)),(0.38,w(N['A5'],.8,1)),(0.45,sparkle(0.4,5,1800,.3))])
S['mail']=mix(0.8,[(0,w(N['C5'],.4,.8)),(0.05,shaped_noise(0.35,[700,4000],0.4,.6,[0,1,.7,0])),(0.2,w(N['C6']))])
S['open']=mix(1.3,[(0,cork(1.0)),(0.08,shaped_noise(0.4,[6000,8000],0.35,.25,[1,.6,.3,0])),(0.15,w(N['C5'],.84,.7)),(0.195,w(N['E5'],.84,.7)),(0.24,w(N['G5'],.84,.75)),(0.285,w(N['B5'],.96,.8)),(0.3,sparkle(0.5,6,1800,.25))])
S['pc']=mix(0.95,[(0,w(N['A4'])),(0.12,w(N['A4'],.6,.9)),(0.24,w(N['E5'])),(0.02,bubble(250,0.09,1.7,.4)),(0.26,bubble(330,0.08,2,.45))])
out = os.path.join(here, '..', '..', 'sounds')
for k,x in S.items(): save(os.path.join(out, 'potion-%s.wav'%k), room(x,0.5))
