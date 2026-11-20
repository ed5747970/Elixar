# Shared helpers for tools/sounds/make-potion.py: notes, a small room echo, and saving 22 kHz 16-bit mono WAV files.
import numpy as np, wave, sys, os
SR=44100
def env(n, a=0.004, d=0.35):
    t=np.arange(n)/SR
    e=np.exp(-t/d); att=int(a*SR)
    e[:att]*=np.linspace(0,1,att)
    return e
def note(f, dur, kind, vol=1.0):
    n=int(dur*SR); t=np.arange(n)/SR
    if kind=='wood':   # marimba-like: fundamental + 4x and 10x partials, fast decay
        s=np.sin(2*np.pi*f*t)*env(n,0.003,0.28)+0.35*np.sin(2*np.pi*f*3.93*t)*env(n,0.002,0.05)+0.08*np.sin(2*np.pi*f*9.2*t)*env(n,0.001,0.015)
    elif kind=='glass': # soft bell: inharmonic partials, longer decay, gentle shimmer
        s=(np.sin(2*np.pi*f*t)*env(n,0.004,0.6)+0.45*np.sin(2*np.pi*f*2.0*t+0.3)*env(n,0.003,0.35)
           +0.22*np.sin(2*np.pi*f*2.76*t)*env(n,0.002,0.22)+0.1*np.sin(2*np.pi*f*5.4*t)*env(n,0.001,0.08))
        s*=1+0.03*np.sin(2*np.pi*5*t)
    else:               # minimal: pure tone, soft attack, short
        s=np.sin(2*np.pi*f*t)*env(n,0.008,0.09)+0.12*np.sin(2*np.pi*f*2*t)*env(n,0.008,0.05)
    k=min(len(s),int(0.04*SR)); s[-k:]*=np.linspace(1,0,k)**2
    return s*vol
def seq(parts, kind, total):
    out=np.zeros(int(total*SR))
    for start,f,dur,vol in parts:
        s=note(f,dur,kind,vol); i=int(start*SR); out[i:i+len(s)]+=s[:len(out)-i]
    return out
def room(x, amt):
    y=x.copy()
    for d,g in ((0.031,0.5),(0.047,0.4),(0.071,0.3),(0.113,0.2)):
        k=int(d*SR); z=np.zeros_like(x); z[k:]=x[:-k]*g; y+=z*amt
    return y
def save(name, x):
    x=x/np.max(np.abs(x))*0.5   # about -6 dB: never loud
    fade=int(0.02*SR); x[-fade:]*=np.linspace(1,0,fade)
    with wave.open(name,'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(22050)
        w.writeframes((x[::2]*32767).astype('<i2').tobytes())
N={'C5':523.25,'D5':587.33,'E5':659.25,'F5':698.46,'G5':783.99,'A5':880.0,'B5':987.77,'C6':1046.5,'E6':1318.5,'G4':392.0,'A4':440.0,'B4':493.88,'E4':329.63,'F4':349.23}
