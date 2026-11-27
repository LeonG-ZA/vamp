#!/usr/bin/env python3
"""Procedurally synthesises Sanguine City's sound set (original audio, released with the project).

    python3 Tools/audio/synth_audio.py          # from the repository root; needs numpy + ffmpeg (libvorbis)

Writes Ogg Vorbis files to Assets/Sanguine/Resources/Audio/. Loops are built with circular (FFT)
processing and wrap-around event placement, so they repeat without seams. Deterministic (fixed seeds).
"""
import os
import subprocess
import sys
import tempfile
import wave

import numpy as np

SR = 44100
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.abspath(os.path.join(HERE, '..', '..', 'Assets', 'Sanguine', 'Resources', 'Audio'))
TAU = 2 * np.pi


# ----------------------------------------------------------------------------- building blocks

def rng(seed):
    return np.random.default_rng(seed)


def t_axis(seconds):
    return np.arange(int(seconds * SR)) / SR


def shaped_noise(n, r, slope=0.0, lo=None, hi=None, soft=1.0):
    """Circular noise with spectrum ~ 1/f^slope, optionally band-limited (smooth edges). Loops seamlessly."""
    spec = np.fft.rfft(r.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / SR)
    f[0] = f[1]
    gain = f ** (-slope / 2.0)
    if lo:
        gain *= 1 / (1 + (lo / f) ** (4 * soft))
    if hi:
        gain *= 1 / (1 + (f / hi) ** (4 * soft))
    x = np.fft.irfft(spec * gain, n)
    return x / (np.std(x) + 1e-12)


def band(x, lo=None, hi=None, order=2.0):
    """Circular (zero-phase) band filter."""
    spec = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / SR)
    f[0] = 1e-3
    g = np.ones_like(f)
    if lo:
        g *= 1 / (1 + (lo / f) ** (2 * order))
    if hi:
        g *= 1 / (1 + (f / hi) ** (2 * order))
    return np.fft.irfft(spec * g, len(x))


def smooth_random(n, r, rate_hz, depth=1.0):
    """Slowly varying positive modulation (circular)."""
    m = shaped_noise(n, r, slope=0, hi=rate_hz, soft=2.0)
    m = (m - m.min()) / (m.max() - m.min() + 1e-12)
    return 1 - depth + depth * m


def add_wrapped(dst, src, start):
    """Adds src into dst at start, wrapping around the end (for seamless loops)."""
    n = len(dst)
    start %= n
    end = start + len(src)
    if end <= n:
        dst[start:end] += src
    else:
        k = n - start
        dst[start:] += src[:k]
        dst[:end - n] += src[k:]


def add_at(dst, src, start):
    """Adds src into dst at start, clipped at the end (one-shots)."""
    if start >= len(dst):
        return
    k = min(len(src), len(dst) - start)
    dst[start:start + k] += src[:k]


def env_exp(seconds, decay, attack=0.002):
    t = t_axis(seconds)
    a = np.clip(t / max(attack, 1e-5), 0, 1)
    return a * np.exp(-t / decay)


def sine_sweep(seconds, f0, f1, curve='exp'):
    t = t_axis(seconds)
    if curve == 'exp':
        f = f0 * (f1 / f0) ** (t / seconds)
    else:
        f = f0 + (f1 - f0) * t / seconds
    return np.sin(TAU * np.cumsum(f) / SR)


def resonator(seconds, freq, decay, r=None, noise=0.0):
    t = t_axis(seconds)
    x = np.sin(TAU * freq * t) * np.exp(-t / decay)
    if noise and r is not None:
        x += noise * band(r.standard_normal(len(t)), freq * 0.7, freq * 1.4) * np.exp(-t / (decay * 0.5))
    return x


def reverb(x, seconds=2.5, mix=0.3, r=None, damp=6000, circular=False, predelay=0.02):
    """Convolution reverb with an exponentially decaying, darkening noise tail."""
    r = r or rng(7)
    n_ir = int(seconds * SR)
    t = np.arange(n_ir) / SR
    ir = r.standard_normal(n_ir) * np.exp(-t * 6.9 / seconds)
    ir = band(ir, 120, damp)
    ir[: int(predelay * SR)] = 0
    tail = int(n_ir * 0.1)
    ir[-tail:] *= np.linspace(1, 0, tail) ** 2  # taper: no edge (tick) at the end of the tail
    ir /= np.sqrt(np.sum(ir ** 2)) + 1e-12
    if x.ndim == 1:
        x = x[:, None]
    out = np.zeros_like(x)
    for c in range(x.shape[1]):
        if circular:
            n = len(x)
            irp = np.zeros(n)
            irp[: min(n, n_ir)] = ir[: min(n, n_ir)]
            wet = np.fft.irfft(np.fft.rfft(x[:, c]) * np.fft.rfft(irp), n)
        else:
            m = len(x) + n_ir
            wet = np.fft.irfft(np.fft.rfft(x[:, c], m) * np.fft.rfft(ir, m), m)[: len(x)]
        out[:, c] = (1 - mix) * x[:, c] + mix * wet * 3.0
    return out[:, 0] if out.shape[1] == 1 else out


def pan(x, p):
    """Equal-power pan; p in [-1, 1] (scalar or per-sample)."""
    a = (np.asarray(p) + 1) * np.pi / 4
    return np.stack([x * np.cos(a), x * np.sin(a)], axis=-1)


def normalize(x, peak_db=-1.0, rms_db=None):
    if rms_db is not None:
        rms = np.sqrt(np.mean(x ** 2)) + 1e-12
        x = x * (10 ** (rms_db / 20) / rms)
    peak = np.max(np.abs(x)) + 1e-12
    lim = 10 ** (peak_db / 20)
    if peak > lim:
        x = x * (lim / peak)
    return x


def fade(x, fin=0.002, fout=0.02):
    n = len(x)
    a, b = int(fin * SR), int(fout * SR)
    g = np.ones(n)
    if a:
        g[:a] = np.linspace(0, 1, a)
    if b:
        g[-b:] *= np.linspace(1, 0, b)
    return x * (g if x.ndim == 1 else g[:, None])


def save(name, x, quality=5):
    os.makedirs(OUT, exist_ok=True)
    x = np.clip(x, -1, 1)
    ch = 1 if x.ndim == 1 else x.shape[1]
    pcm = (x * 32767).astype('<i2')
    with tempfile.TemporaryDirectory() as td:
        wav = os.path.join(td, name + '.wav')
        with wave.open(wav, 'wb') as w:
            w.setnchannels(ch)
            w.setsampwidth(2)
            w.setframerate(SR)
            w.writeframes(pcm.tobytes())
        dst = os.path.join(OUT, name + '.ogg')
        # bitexact: fixed Ogg serial numbers, so unchanged sounds produce identical files on every run.
        subprocess.check_call(['ffmpeg', '-y', '-loglevel', 'error', '-i', wav, '-c:a', 'libvorbis', '-q:a', str(quality),
                               '-fflags', '+bitexact', '-flags:a', '+bitexact', dst])
    print(f'  {name:22s} {len(x) / SR:5.2f}s {"stereo" if ch == 2 else "mono  "} {os.path.getsize(dst) / 1024:7.1f} KB')


# ----------------------------------------------------------------------------- ambience and music

def car_pass(seconds, r, busy=1.0):
    """A distant vehicle passing: band-limited tyre/engine noise under a smooth swell."""
    n = int(seconds * SR)
    t = np.arange(n) / SR
    body = band(r.standard_normal(n), 90, 900) + 0.35 * band(r.standard_normal(n), 900, 3500)
    engine = 0.25 * np.sin(TAU * r.uniform(45, 70) * t + 2 * np.sin(TAU * 0.7 * t))
    swell = np.exp(-((t - seconds / 2) / (seconds / 5)) ** 2)
    return (body / (np.std(body) + 1e-9) + engine) * swell * busy


def city_bed(seconds, seed, cars, horns=0, birds=0, siren=True, rumble=0.5):
    r = rng(seed)
    n = int(seconds * SR)
    out = np.zeros((n, 2))
    for c in range(2):
        out[:, c] += rumble * shaped_noise(n, r, slope=2.0, lo=20, hi=160)
        wind = shaped_noise(n, r, slope=1.0, lo=250, hi=1400) * smooth_random(n, r, 0.15, 0.9)
        out[:, c] += 0.07 * wind
    for i in range(cars):
        dur = r.uniform(3.5, 7.0)
        x = car_pass(dur, r, r.uniform(0.12, 0.3))
        p = np.linspace(r.uniform(-0.9, -0.2), r.uniform(0.2, 0.9), len(x)) * (1 if r.random() < 0.5 else -1)
        s = pan(x, p)
        start = int(r.uniform(0, seconds) * SR)
        for c in range(2):
            add_wrapped(out[:, c], s[:, c], start)
    if siren:
        dur = 9.0
        t = t_axis(dur)
        f = 760 + 160 * np.sin(TAU * 0.35 * t)
        tone = np.sin(TAU * np.cumsum(f) / SR) + 0.3 * np.sin(2 * TAU * np.cumsum(f) / SR)
        tone = band(tone, None, 1800) * np.sin(np.pi * t / dur) ** 2 * 0.035
        s = pan(tone, -0.6)
        start = int(r.uniform(0, seconds) * SR)
        for c in range(2):
            add_wrapped(out[:, c], s[:, c], start)
    for i in range(horns):
        dur = r.uniform(0.25, 0.5)
        t = t_axis(dur)
        f0 = r.uniform(330, 440)
        tone = np.sign(np.sin(TAU * f0 * t)) * 0.5 + np.sign(np.sin(TAU * f0 * 1.26 * t)) * 0.5
        tone = band(tone, 200, 2000) * np.minimum(1, np.minimum(t / 0.02, (dur - t) / 0.03)) * 0.05
        s = pan(tone, r.uniform(-0.8, 0.8))
        start = int(r.uniform(0, seconds) * SR)
        for c in range(2):
            add_wrapped(out[:, c], s[:, c], start)
    for i in range(birds):
        dur = r.uniform(0.08, 0.18)
        f0, f1 = r.uniform(2500, 4200), r.uniform(3200, 5800)
        chirp = sine_sweep(dur, f0, f1) * np.sin(np.pi * t_axis(dur) / dur) ** 2 * r.uniform(0.02, 0.05)
        s = pan(chirp, r.uniform(-1, 1))
        start = int(r.uniform(0, seconds) * SR)
        for k in range(r.integers(1, 4)):
            for c in range(2):
                add_wrapped(out[:, c], s[:, c], start + int(k * dur * 1.4 * SR))
    return reverb(out, 1.6, 0.25, r, damp=4000, circular=True)


def rain(seconds, seed):
    r = rng(seed)
    n = int(seconds * SR)
    out = np.zeros((n, 2))
    k_t = t_axis(0.006)
    for c in range(2):
        hiss = shaped_noise(n, r, slope=0.6, lo=900, hi=9000) * smooth_random(n, r, 0.2, 0.35)
        out[:, c] += 0.18 * hiss
        # Thousands of small droplet ticks on hard ground: sparse impulses through a short noisy kernel.
        imp = np.zeros(n)
        idx = r.integers(0, n, int(seconds * 900))
        imp[idx] += r.uniform(0.05, 1.0, len(idx)) ** 2
        kernel = r.standard_normal(len(k_t)) * np.exp(-k_t / 0.0012)
        drops = np.fft.irfft(np.fft.rfft(imp) * np.fft.rfft(kernel, n), n)
        out[:, c] += 0.35 * band(drops, 1500, 11000)
        # Heavier drops and gutter drips: short damped tones.
        for i in range(int(seconds * 6)):
            f = r.uniform(700, 2600)
            d = resonator(0.06, f, r.uniform(0.006, 0.02)) * r.uniform(0.02, 0.08)
            add_wrapped(out[:, c], d, int(r.uniform(0, n)))
        for i in range(int(seconds * 0.7)):
            f0 = r.uniform(1200, 2400)
            d = sine_sweep(0.08, f0, f0 * 0.6) * env_exp(0.08, 0.02) * r.uniform(0.05, 0.12)
            add_wrapped(out[:, c], d, int(r.uniform(0, n)))
    return reverb(out, 0.9, 0.2, r, damp=7000, circular=True)


def music(seconds, seed):
    """Slow minor-key drone score: i - VI - iv - V in D minor with sub bass and distant bells."""
    r = rng(seed)
    n = int(seconds * SR)
    t = np.arange(n) / SR
    out = np.zeros((n, 2))
    hz = lambda midi: 440.0 * 2 ** ((midi - 69) / 12)
    q = lambda f: round(f * seconds) / seconds  # whole cycles per loop -> seamless repeat
    chords = [[38, 50, 53, 57], [34, 50, 53, 58], [31, 50, 55, 58], [33, 49, 52, 57]]  # Dm, Bb, Gm, A
    seg = seconds / len(chords)
    for ci, chord in enumerate(chords):
        centre = (ci + 0.5) * seg
        # Circular raised-cosine window, overlapping its neighbours for slow cross-fades.
        dt = (t - centre + seconds / 2) % seconds - seconds / 2
        w = np.where(np.abs(dt) < seg, 0.5 + 0.5 * np.cos(np.pi * dt / seg), 0.0)
        for vi, note in enumerate(chord):
            f = hz(note)
            for ch in range(2):
                det = 1 + (0.0025 if ch else -0.0025) * (1 + vi * 0.3)
                voice = np.zeros(n)
                bright = 0.55 + 0.45 * np.sin(TAU * t / seconds * 2 + vi)
                for h in range(1, 7):
                    voice += np.sin(TAU * q(f * det * h) * t + r.uniform(0, TAU)) / h * (bright ** (h - 1))
                out[:, ch] += voice * w * (0.16 if vi == 0 else 0.1)
    out += pan(0.22 * np.sin(TAU * q(hz(26)) * t) * (0.8 + 0.2 * np.sin(TAU * t / seconds * 4)), 0)  # D1 sub
    scale = [62, 65, 69, 72, 74, 77]  # D minor tones, one octave up
    for i in range(7):
        f = hz(scale[r.integers(0, len(scale))])
        dur = 5.0
        tt = t_axis(dur)
        bell = sum(a * np.sin(TAU * f * k * tt) * np.exp(-tt / (d)) for k, a, d in [(1, 1, 2.2), (2.76, 0.4, 1.2), (5.4, 0.2, 0.6), (0.5, 0.3, 3.0)])
        s = pan(bell * 0.045 * np.minimum(1, tt / 0.004), r.uniform(-0.7, 0.7))
        start = int(r.uniform(0, seconds) * SR)
        for c in range(2):
            add_wrapped(out[:, c], s[:, c], start)
    return reverb(out, 4.5, 0.45, r, damp=3500, circular=True)


# ----------------------------------------------------------------------------- positional loops

def fire_loop(seconds, seed):
    r = rng(seed)
    n = int(seconds * SR)
    roar = shaped_noise(n, r, slope=1.5, lo=40, hi=500) * smooth_random(n, r, 3.0, 0.6) * 0.5
    imp = np.zeros(n)
    idx = r.integers(0, n, int(seconds * 35))
    imp[idx] = r.uniform(0.1, 1.0, len(idx)) ** 3
    kt = t_axis(0.01)
    crackle = np.fft.irfft(np.fft.rfft(imp) * np.fft.rfft(r.standard_normal(len(kt)) * np.exp(-kt / 0.0015), n), n)
    out = roar + 1.4 * band(crackle, 1200, 9000)
    for i in range(int(seconds * 1.5)):  # wood pops
        add_wrapped(out, resonator(0.05, r.uniform(250, 600), 0.01, r, 0.8) * r.uniform(0.2, 0.5), int(r.uniform(0, n)))
    return out


def neon_loop(seconds, seed):
    r = rng(seed)
    t = t_axis(seconds)
    s = np.sin(TAU * 120 * t)
    buzz = np.tanh(4 * s) * 0.4 + 0.25 * np.sin(TAU * 240 * t) + 0.12 * np.sin(TAU * 360 * t)
    buzz = band(buzz, 90, 3000)
    hiss = 0.08 * band(r.standard_normal(len(t)), 2000, 8000) * smooth_random(len(t), r, 8.0, 0.8)
    flicker = 1 - 0.25 * (smooth_random(len(t), r, 6.0, 1.0) > 0.92)
    return (buzz + hiss) * flicker


def sizzle_loop(seconds, seed):
    r = rng(seed)
    n = int(seconds * SR)
    imp = np.zeros(n)
    idx = r.integers(0, n, int(seconds * 1800))
    imp[idx] = r.uniform(0.05, 1.0, len(idx)) ** 2
    kt = t_axis(0.004)
    fry = np.fft.irfft(np.fft.rfft(imp) * np.fft.rfft(r.standard_normal(len(kt)) * np.exp(-kt / 0.0008), n), n)
    return band(fry, 2500, 12000) + 0.15 * shaped_noise(n, r, 0, 3000, 9000)


def heartbeat_loop(bpm=75, beats=5):
    period = 60.0 / bpm
    n = int(round(period * beats * SR))
    out = np.zeros(n)
    for b in range(beats):
        for start, f, amp, dec in [(0.0, 55, 1.0, 0.09), (0.27, 70, 0.7, 0.07)]:
            tt = t_axis(0.3)
            thump = np.sin(TAU * f * tt * (1 - 0.3 * tt)) * np.exp(-tt / dec) * np.minimum(1, tt / 0.008)
            add_wrapped(out, thump * amp * (1 - 0.04 * (b % 2)), int((b * period + start) * SR))
    return band(out, 25, 400)


# ----------------------------------------------------------------------------- one-shots

def footstep(kind, seed):
    r = rng(seed)
    dur = 0.35
    n = int(dur * SR)
    tt = t_axis(dur)
    x = np.zeros(n)
    x += band(r.standard_normal(n), 1800, 9000) * np.exp(-tt / 0.006) * 0.5      # heel click
    x += np.sin(TAU * r.uniform(85, 130) * tt) * np.exp(-tt / 0.035) * 0.8        # body thump
    scuff = band(r.standard_normal(n), 700, 3500) * np.exp(-np.maximum(tt - 0.03, 0) / 0.04) * (tt > 0.03) * 0.15
    x += scuff
    if kind == 'cobble':
        for k in range(r.integers(2, 4)):
            add_at(x, resonator(0.05, r.uniform(700, 1500), 0.006, r, 1.0) * r.uniform(0.2, 0.45), int(r.uniform(0.004, 0.03) * SR))
    if kind == 'wet':
        splash = band(r.standard_normal(n), 1200, 7000) * np.exp(-np.maximum(tt - 0.01, 0) / r.uniform(0.07, 0.11)) * (tt > 0.01) * 0.45
        x += splash
        for k in range(6):
            add_at(x, resonator(0.04, r.uniform(1500, 3500), 0.005) * r.uniform(0.03, 0.1), int(r.uniform(0.02, 0.15) * SR))
    return fade(normalize(x, -3.0), 0.0005, 0.05)


def bite():
    r = rng(301)
    dur = 0.9
    tt = t_axis(dur)
    n = len(tt)
    x = band(r.standard_normal(n), 250, 2500) * np.exp(-tt / 0.08) * (1 + 0.6 * np.sin(TAU * 38 * tt)) * 0.7
    x += np.sin(TAU * 90 * tt) * np.exp(-tt / 0.05) * 0.6
    gulp = sine_sweep(0.18, 240, 110) * np.sin(np.pi * t_axis(0.18) / 0.18) ** 2 * 0.5
    gulp += band(r.standard_normal(len(gulp)), 150, 900) * np.sin(np.pi * t_axis(0.18) / 0.18) ** 2 * 0.25
    add_at(x, gulp, int(0.42 * SR))
    return fade(normalize(reverb(x, 0.4, 0.15), -2.0))


def strike():
    r = rng(302)
    dur = 0.5
    tt = t_axis(dur)
    x = sine_sweep(dur, 95, 45) * np.exp(-tt / 0.09) * 0.9
    x += band(r.standard_normal(len(tt)), 200, 3500) * np.exp(-tt / 0.018) * 0.6
    x += np.sin(TAU * 210 * tt) * np.exp(-tt / 0.03) * 0.3
    return fade(normalize(x, -1.5))


def thud():
    r = rng(303)
    tt = t_axis(0.8)
    x = sine_sweep(0.8, 70, 35) * np.exp(-tt / 0.15) + band(r.standard_normal(len(tt)), 60, 700) * np.exp(-tt / 0.06) * 0.6
    return fade(normalize(reverb(x, 0.8, 0.2), -2.0))


def power_surge():
    r = rng(310)
    dur = 1.0
    tt = t_axis(dur)
    n = len(tt)
    x = np.zeros(n)
    for i, (lo, hi) in enumerate([(150, 500), (400, 1200), (1000, 3000), (2500, 7000)]):
        centre = 0.15 + i * 0.16
        x += band(r.standard_normal(n), lo, hi) * np.exp(-((tt - centre) / 0.12) ** 2) * 0.5
    x += sine_sweep(dur, 60, 150) * np.sin(np.pi * tt / dur) * 0.5
    hb = heartbeat_loop(140, 2)
    add_at(x, hb * 0.6, int(0.05 * SR))  # the blood quickens
    return fade(normalize(reverb(x, 1.0, 0.25), -2.0))


def power_allure():
    tt = t_axis(2.6)
    hz = lambda m: 440.0 * 2 ** ((m - 69) / 12)
    x = np.zeros(len(tt))
    for k, m in enumerate([81, 85, 88, 93]):  # A major shimmer
        vib = 1 + 0.004 * np.sin(TAU * 5.5 * tt + k)
        x += np.sin(TAU * hz(m) * np.cumsum(vib) / SR) * np.minimum(1, tt / (0.15 + 0.08 * k)) * np.exp(-tt / 1.0) * 0.25
    return fade(normalize(reverb(x, 2.5, 0.45), -3.0))


def power_lash():
    r = rng(312)
    tt = t_axis(0.9)
    n = len(tt)
    swish = band(r.standard_normal(n), 1500, 9000) * np.exp(-((tt - 0.12) / 0.05) ** 2) * 0.5
    crack = np.zeros(n)
    k = int(0.17 * SR)
    crack[k:k + 60] = r.standard_normal(60) * np.exp(-np.arange(60) / 12)
    crack = band(crack, 500, 14000) * 6
    wet = band(r.standard_normal(n), 200, 1500) * np.exp(-np.maximum(tt - 0.18, 0) / 0.12) * (tt > 0.18) * 0.35
    return fade(normalize(reverb(swish + crack + wet, 0.7, 0.2), -1.0))


def power_command():
    r = rng(313)
    dur = 1.8
    tt = t_axis(dur)
    saw = sum(np.sin(TAU * 55 * h * tt) / h for h in range(1, 30))
    voice = band(saw, 300, 520, 2.5) + 0.7 * band(saw, 700, 1000, 2.5) + 0.3 * band(saw, 2300, 2700, 2.5)
    voice *= np.minimum(1, tt / 0.12) * np.exp(-tt / 0.9) * 0.6
    boom = sine_sweep(dur, 60, 30) * np.exp(-tt / 0.4) * 0.7
    return fade(normalize(reverb(voice + boom, 3.0, 0.4), -2.0))


def power_iron_skin():
    r = rng(340)
    tt = t_axis(2.0)
    f0 = 330.0
    x = sum(a * np.sin(TAU * f0 * k * tt + r.uniform(0, TAU)) * np.exp(-tt / d)
            for k, a, d in [(1, 1, 1.2), (2.32, 0.7, 0.8), (4.25, 0.45, 0.5), (6.63, 0.3, 0.3), (9.1, 0.15, 0.15)])
    x *= np.minimum(1, tt / 0.001) * 0.4
    x += band(r.standard_normal(len(tt)), 2000, 10000) * np.exp(-tt / 0.01) * 0.5
    x += np.sin(TAU * 70 * tt) * np.exp(-tt / 0.08) * 0.5
    return fade(normalize(reverb(x, 1.2, 0.25), -2.0))


def power_earthshaker():
    r = rng(341)
    dur = 3.0
    tt = t_axis(dur)
    n = len(tt)
    x = sine_sweep(dur, 62, 24) * np.exp(-tt / 0.7) * 1.0
    x += band(r.standard_normal(n), 30, 220) * np.exp(-tt / 0.9) * 0.6
    imp = np.zeros(n)
    idx = r.integers(int(0.05 * SR), int(1.6 * SR), 120)
    imp[idx] = r.uniform(0.1, 1.0, len(idx)) ** 2 * np.exp(-idx / SR / 0.6)
    kt = t_axis(0.01)
    debris = np.fft.irfft(np.fft.rfft(imp) * np.fft.rfft(r.standard_normal(len(kt)) * np.exp(-kt / 0.002), n), n)
    x += band(debris, 800, 7000) * 1.2
    return fade(normalize(reverb(x, 2.0, 0.3), -1.0))


def power_glamour():
    r = rng(342)
    dur = 1.8
    n = int(dur * SR)
    x = np.zeros(n)
    for i in range(42):
        t0 = r.uniform(0, 1.2)
        f = r.uniform(2600, 6500) * (1 - 0.35 * t0 / 1.2)
        tt = t_axis(0.25)
        add_at(x, np.sin(TAU * f * tt) * np.exp(-tt / 0.05) * r.uniform(0.05, 0.15), int(t0 * SR))
    tt = t_axis(dur)
    x += band(r.standard_normal(n), 4000, 12000) * np.sin(np.pi * np.minimum(tt / 1.4, 1)) ** 2 * 0.06
    return fade(normalize(reverb(x, 2.0, 0.4), -4.0))


def power_rapture():
    r = rng(343)
    dur = 3.2
    tt = t_axis(dur)
    hz = lambda m: 440.0 * 2 ** ((m - 69) / 12)
    voices = np.zeros(len(tt))
    for m in [57, 61, 64, 69, 73]:  # A major, spread
        for det in (-0.004, 0.0, 0.004):
            f = hz(m) * (1 + det)
            voices += sum(np.sin(TAU * f * h * tt + r.uniform(0, TAU)) / h for h in range(1, 12)) * 0.05
    choir = band(voices, 650, 950, 1.5) + 0.7 * band(voices, 1050, 1350, 1.5) + 0.25 * band(voices, 2500, 2900, 1.5)
    env = np.minimum(1, tt / 0.6) * np.exp(-np.maximum(tt - 0.6, 0) / 1.1)
    return fade(normalize(reverb(choir * env * (1 + 0.05 * np.sin(TAU * 5 * tt)), 3.0, 0.45), -3.0))


def power_siphon():
    r = rng(344)
    dur = 1.1
    tt = t_axis(dur)
    n = len(tt)
    x = np.zeros(n)
    for i, (lo, hi) in enumerate([(250, 600), (500, 1100), (900, 1900), (1500, 3000)]):
        x += band(r.standard_normal(n), lo, hi) * np.exp(-((tt - (0.12 + 0.13 * i)) / 0.09) ** 2) * (1 + 0.5 * np.sin(TAU * 31 * tt)) * 0.4
    gulp = sine_sweep(0.2, 260, 110) * np.sin(np.pi * t_axis(0.2) / 0.2) ** 2 * 0.45
    add_at(x, gulp, int(0.72 * SR))
    return fade(normalize(reverb(x, 0.6, 0.2), -2.0))


def power_crimson_mist():
    r = rng(345)
    dur = 1.6
    tt = t_axis(dur)
    n = len(tt)
    x = np.zeros(n)
    for i, (lo, hi) in enumerate([(5000, 10000), (2500, 6000), (1200, 3200), (500, 1500)]):  # descending hiss
        x += band(r.standard_normal(n), lo, hi) * np.exp(-((tt - (0.2 + 0.18 * i)) / 0.22) ** 2) * 0.35
    x += band(r.standard_normal(n), 40, 200) * np.sin(np.pi * tt / dur) * 0.3
    return fade(normalize(reverb(x, 1.8, 0.35), -3.0))


def power_majesty():
    r = rng(346)
    dur = 2.6
    tt = t_axis(dur)
    hz = lambda m: 440.0 * 2 ** ((m - 69) / 12)
    x = np.zeros(len(tt))
    env = np.minimum(1, tt / 0.18) * np.exp(-np.maximum(tt - 0.5, 0) / 0.9)
    bright = np.minimum(1, tt / 0.3)
    for m in [50, 57, 62, 69]:  # D and A: open, regal fifths
        f = hz(m)
        x += sum(np.sin(TAU * f * h * tt + r.uniform(0, TAU)) / h * (bright ** (h * 0.5)) for h in range(1, 14)) * 0.12
    x = band(x * env, 80, 3500)
    return fade(normalize(reverb(x, 2.8, 0.4), -2.0))


def power_dominate():
    r = rng(347)
    dur = 2.4
    tt = t_axis(dur)
    saw = sum(np.sin(TAU * 41 * h * tt) / h for h in range(1, 40))
    voice = band(saw, 250, 420, 2.5) + 0.6 * band(saw, 600, 850, 2.5) + 0.25 * band(saw, 2200, 2600, 2.5)
    voice *= np.minimum(1, tt / 0.25) * np.exp(-np.maximum(tt - 0.4, 0) / 0.9) * 0.8
    swell = band(r.standard_normal(len(tt)), 200, 2000) * (tt / dur) ** 3 * np.exp(-np.maximum(tt - 0.35, 0) / 0.05) * (tt < 0.45) * 0.4
    x = voice + swell
    bt = t_axis(dur - 0.4)
    add_at(x, sine_sweep(dur - 0.4, 50, 22) * np.exp(-bt / 0.5) * np.minimum(1, bt / 0.004) * 0.9, int(0.4 * SR))
    return fade(normalize(reverb(x, 3.0, 0.4), -2.0))


def _gauss(tt, t0, w):
    return np.exp(-((tt - t0) / w) ** 2)


def power_shroud():
    """Umbren Shroud: a dark, descending breath as the shadows fold shut."""
    r = rng(350)
    dur = 1.7
    tt = t_axis(dur)
    n = len(tt)
    x = np.zeros(n)
    for i, (lo, hi) in enumerate([(2500, 6000), (1400, 3200), (700, 1800), (300, 900), (120, 450)]):
        x += band(r.standard_normal(n), lo, hi) * _gauss(tt, 0.15 + 0.17 * i, 0.2) * (0.25 + 0.05 * i)
    x += np.sin(TAU * 55 * tt + 0.4 * np.sin(TAU * 0.8 * tt)) * np.sin(np.pi * np.clip(tt / 1.3, 0, 1)) ** 2 * 0.25
    return fade(normalize(reverb(x, 1.6, 0.3, damp=3000), -4.0), 0.002, 0.2)


def power_whispers():
    """Umbren Whispers: half-heard voices from every direction, and the squeak of the rats who carry them."""
    r = rng(351)
    dur = 2.6
    n = int(dur * SR)
    x = np.zeros(n)
    for v in range(9):
        vd = r.uniform(0.45, 0.9)
        vt = t_axis(vd)
        breath = r.standard_normal(len(vt))
        f1, f2 = r.uniform(500, 900), r.uniform(1300, 2300)
        voice = band(breath, f1 * 0.8, f1 * 1.25) * 0.6 + band(breath, f2 * 0.85, f2 * 1.2) * 0.5 + band(breath, 3000, 7000) * 0.5
        syll = np.clip(np.sin(TAU * r.uniform(4, 7) * vt + r.uniform(0, TAU)), 0, 1) ** 1.5
        voice *= syll * np.sin(np.pi * vt / vd) ** 2 * r.uniform(0.25, 0.5)
        add_at(x, voice, int(r.uniform(0.0, dur - vd - 0.2) * SR))
    for k in range(4):
        st = t_axis(0.07)
        squeak = sine_sweep(0.07, r.uniform(4200, 5600), r.uniform(2800, 3500)) * np.sin(np.pi * st / 0.07) ** 2 * 0.12
        add_at(x, squeak, int(r.uniform(0.2, 2.2) * SR))
    return fade(normalize(reverb(x, 1.4, 0.35), -5.0), 0.002, 0.3)


def power_undercroft():
    """Umbren Undercroft: a grate scrapes aside, air rushes down a shaft, water drips in the dark below."""
    r = rng(352)
    dur = 3.0
    tt = t_axis(dur)
    n = len(tt)
    x = np.zeros(n)
    scrape = band(r.standard_normal(n), 180, 1400) * (0.6 + 0.4 * np.abs(np.sin(TAU * 23 * tt))) * _gauss(tt, 0.25, 0.18) * 0.6
    x += scrape
    for i, (lo, hi) in enumerate([(1500, 4000), (700, 2000), (250, 900), (80, 300)]):    # falling rush of air
        x += band(r.standard_normal(n), lo, hi) * _gauss(tt, 0.55 + 0.2 * i, 0.22) * 0.3
    bt = t_axis(dur - 1.2)
    add_at(x, sine_sweep(dur - 1.2, 60, 32) * np.exp(-bt / 0.4) * np.minimum(1, bt / 0.01) * 0.7, int(1.2 * SR))
    for k in range(5):
        add_at(x, resonator(0.25, r.uniform(1400, 2600), 0.03) * r.uniform(0.1, 0.22), int(r.uniform(1.4, 2.7) * SR))
    return fade(normalize(reverb(x, 2.8, 0.45, damp=4000), -3.0), 0.002, 0.4)


def growl(seconds, f0, f1, r, rough=26.0):
    """A throaty animal growl: a buzzing harmonic tone, rattled and breathy."""
    tt = t_axis(seconds)
    f = f0 * (f1 / f0) ** (tt / seconds)
    ph = np.cumsum(TAU * f / SR)
    tone = sum(np.sin(k * ph) / k for k in range(1, 12))
    rattle = 0.55 + 0.45 * np.abs(np.sin(TAU * rough * tt + 0.6 * np.sin(TAU * 3.1 * tt)))
    breath = band(r.standard_normal(len(tt)), 300, 2500) * 0.35
    return np.tanh(1.6 * (tone * 0.5 + breath) * rattle)


def power_pounce():
    """Varga Pounce: a rising rush, a heavy landing and a snarl."""
    r = rng(353)
    dur = 1.1
    tt = t_axis(dur)
    n = len(tt)
    x = np.zeros(n)
    for i, (lo, hi) in enumerate([(300, 900), (800, 2200), (2000, 5000)]):              # rising leap
        x += band(r.standard_normal(n), lo, hi) * _gauss(tt, 0.08 + 0.07 * i, 0.07) * 0.35
    lt = t_axis(dur - 0.27)
    land = sine_sweep(dur - 0.27, 120, 42) * np.exp(-lt / 0.1) * np.minimum(1, lt / 0.003) * 0.9
    land += band(r.standard_normal(len(lt)), 100, 2500) * np.exp(-lt / 0.025) * 0.6
    add_at(x, land, int(0.27 * SR))
    gt = t_axis(0.5)
    add_at(x, growl(0.5, 95, 80, r) * np.sin(np.pi * gt / 0.5) ** 1.5 * 0.35, int(0.38 * SR))
    return fade(normalize(reverb(x, 0.9, 0.18), -2.0), 0.002, 0.08)


def howl(seconds, base, r):
    """One long howl: a rising glide, a wavering hold and a falling tail."""
    tt = t_axis(seconds)
    k = tt / seconds
    rise = np.clip(k / 0.3, 0, 1)
    fall = np.clip((k - 0.72) / 0.28, 0, 1)
    f = base * (0.6 + 0.4 * (1 - (1 - rise) ** 2)) * (1 + 0.04 * k) * (1 - 0.3 * fall ** 1.5)
    f *= 1 + 0.012 * np.sin(TAU * 5.4 * tt) * np.clip((k - 0.25) / 0.2, 0, 1)
    ph = np.cumsum(TAU * f / SR)
    tone = np.sin(ph) + 0.35 * np.sin(2 * ph) + 0.12 * np.sin(3 * ph) + 0.05 * np.sin(4 * ph)
    env = np.minimum(1, k / 0.12) * (1 - fall ** 2)
    breath = band(r.standard_normal(len(tt)), 500, 3000) * 0.08
    return (tone + breath) * env


def power_howl():
    """Varga Pack Howl: a long howl, answered by the strays of the district."""
    r = rng(354)
    dur = 5.0
    n = int(dur * SR)
    x = np.zeros(n)
    add_at(x, howl(2.4, 520, r) * 0.8, 0)
    for t0, base, amp in [(1.5, 470, 0.32), (2.2, 585, 0.26), (2.7, 430, 0.2)]:
        add_at(x, band(howl(1.9, base, r), 200, 2200) * amp, int(t0 * SR))
    return fade(normalize(reverb(x, 3.2, 0.45, damp=4500), -2.0), 0.003, 0.6)


def power_unchained():
    """Varga Unchained: a heartbeat slams, then a roar climbs out of the chest."""
    r = rng(355)
    dur = 1.9
    tt = t_axis(dur)
    x = np.zeros(len(tt))
    bt = t_axis(0.3)
    add_at(x, np.sin(TAU * 52 * bt * (1 - 0.3 * bt)) * np.exp(-bt / 0.08) * np.minimum(1, bt / 0.006) * 0.9, 0)
    gt = t_axis(1.5)
    roar = growl(1.5, 85, 150, r, rough=31.0) * np.minimum(1, gt / 0.35) * np.exp(-np.maximum(gt - 0.9, 0) / 0.25)
    add_at(x, roar * 0.6, int(0.25 * SR))
    return fade(normalize(reverb(x, 1.2, 0.22), -2.0), 0.002, 0.15)


def torpor():
    r = rng(320)
    dur = 3.0
    tt = t_axis(dur)
    x = sine_sweep(dur, 85, 28) * np.exp(-tt / 0.9) * 0.9
    x += band(r.standard_normal(len(tt)), 40, 300) * np.exp(-tt / 0.5) * 0.4
    return fade(normalize(reverb(x, 3.0, 0.45), -2.0))


def embrace():
    """The Embrace: the fledgling's racing heartbeat slows until it beats as one with the sire's,
    over a slowly swelling D-minor chord and a rush of blood."""
    r = rng(340)
    dur = 6.5
    tt = t_axis(dur)
    x = np.zeros(len(tt))
    hz = lambda m: 440.0 * 2 ** ((m - 69) / 12)

    def beat(f, amp):
        bt = t_axis(0.32)
        one = np.zeros(int(0.62 * SR))
        for start, ff, a, dec in [(0.0, f, 1.0, 0.09), (0.27, f * 1.27, 0.7, 0.07)]:
            add_at(one, np.sin(TAU * ff * bt * (1 - 0.3 * bt)) * np.exp(-bt / dec) * np.minimum(1, bt / 0.008) * a, int(start * SR))
        return one * amp

    meet = 3.15
    for t0 in (0.3, 1.25, 2.2):                       # the sire: slow and steady
        add_at(x, beat(48, 0.8), int(t0 * SR))
    for t0 in (0.15, 0.75, 1.45, 2.25):               # the fledgling: racing, slowing to match
        add_at(x, beat(66, 0.5), int(t0 * SR))
    for i, t0 in enumerate((meet, meet + 0.95, meet + 1.9)):   # one heart
        add_at(x, beat(52, 1.15 - 0.3 * i) + beat(66, 0.35 - 0.1 * i)[:int(0.62 * SR)], int(t0 * SR))

    env = np.clip((tt - 0.4) / 2.8, 0, 1) ** 2 * np.exp(-np.maximum(tt - meet, 0) / 1.6)
    chord = np.zeros(len(tt))
    for m, a in [(38, 1.0), (45, 0.7), (50, 0.6), (53, 0.5), (57, 0.4), (64, 0.25)]:
        vib = 1 + 0.004 * np.sin(TAU * r.uniform(4.6, 5.6) * tt + r.uniform(0, TAU))
        ph = np.cumsum(TAU * hz(m) * vib / SR)
        chord += sum(np.sin(k * ph) / k ** 1.4 for k in range(1, 6)) * a
    x += band(chord, 50, 3000) * env * 0.09

    rush = np.sin(np.pi * np.clip((tt - 1.0) / 2.2, 0, 1)) ** 2
    x += band(r.standard_normal(len(tt)), 250, 2200) * rush * 0.12
    return fade(normalize(reverb(x, 3.0, 0.4), -2.0), 0.002, 0.6)


def sting_danger():
    r = rng(330)
    dur = 2.2
    tt = t_axis(dur)
    hz = lambda m: 440.0 * 2 ** ((m - 69) / 12)
    x = np.zeros(len(tt))
    for m in [45, 46, 51, 57, 58]:  # clustered minor seconds and a tritone
        f = hz(m)
        x += sum(np.sin(TAU * f * h * tt + r.uniform(0, TAU)) / h for h in range(1, 8)) * 0.12
    x *= np.minimum(1, tt / 0.01) * np.exp(-tt / 0.8) * (1 + 0.3 * np.sin(TAU * 7 * tt))
    x += sine_sweep(dur, 70, 40) * np.exp(-tt / 0.25) * 0.6
    return fade(normalize(reverb(x, 2.5, 0.35), -2.0))


def chime():
    tt = t_axis(2.0)
    hz = lambda m: 440.0 * 2 ** ((m - 69) / 12)
    x = np.zeros(len(tt))
    for start, m in [(0.0, 76), (0.14, 83)]:
        b = sum(a * np.sin(TAU * hz(m) * k * tt) * np.exp(-tt / d) for k, a, d in [(1, 1, 0.9), (2.0, 0.3, 0.4), (3.0, 0.15, 0.2)])
        add_at(x, b * np.minimum(1, tt / 0.003) * 0.3, int(start * SR))
    return fade(normalize(reverb(x, 1.5, 0.3), -4.0))


def dawn_bell():
    tt = t_axis(9.0)
    x = np.zeros(len(tt))
    f0 = 196.0
    partials = [(0.5, 0.6, 5.0), (1.0, 1.0, 3.2), (1.2, 0.55, 2.4), (1.5, 0.4, 1.8), (2.0, 0.5, 1.5), (2.5, 0.25, 1.0), (3.0, 0.2, 0.8)]
    one = sum(a * np.sin(TAU * f0 * k * tt) * np.exp(-tt / d) for k, a, d in partials) * np.minimum(1, tt / 0.003)
    for i in range(3):
        add_at(x, one * (0.9 - 0.1 * i), int(i * 2.6 * SR))
    x = band(x, 60, 5000)
    return fade(normalize(reverb(x, 4.0, 0.5, damp=3000), -3.0), 0.001, 0.5)


def click():
    tt = t_axis(0.05)
    return fade(normalize(np.sin(TAU * 1700 * tt) * np.exp(-tt / 0.006), -6.0), 0.0002, 0.005)


# ----------------------------------------------------------------------------- main

def main():
    print('Synthesising Sanguine City audio ->', OUT)
    save('amb_night_city', normalize(city_bed(40.0, 11, cars=9), rms_db=-24, peak_db=-3), 4)
    save('amb_day_city', normalize(city_bed(30.0, 12, cars=16, horns=3, birds=25, siren=False, rumble=0.6), rms_db=-24, peak_db=-3), 4)
    save('amb_rain', normalize(rain(30.0, 13), rms_db=-25, peak_db=-3), 4)
    save('music_night', normalize(music(64.0, 14), rms_db=-23, peak_db=-4), 4)
    save('loop_fire', normalize(fire_loop(8.0, 21), rms_db=-18, peak_db=-2))
    save('loop_neon', normalize(neon_loop(4.0, 22), rms_db=-22, peak_db=-4))
    save('loop_sizzle', normalize(sizzle_loop(3.0, 23), rms_db=-20, peak_db=-3))
    save('loop_heartbeat', normalize(heartbeat_loop(75, 5), -2.0))
    for kind, seed in (('hard', 100), ('cobble', 200), ('wet', 300)):
        for i in range(4):
            save(f'step_{kind}_{i + 1}', footstep(kind, seed + i))
    save('sfx_bite', bite())
    save('sfx_strike', strike())
    save('sfx_thud', thud())
    save('power_surge', power_surge())
    save('power_allure', power_allure())
    save('power_lash', power_lash())
    save('power_command', power_command())
    save('power_iron_skin', power_iron_skin())
    save('power_earthshaker', power_earthshaker())
    save('power_glamour', power_glamour())
    save('power_rapture', power_rapture())
    save('power_siphon', power_siphon())
    save('power_crimson_mist', power_crimson_mist())
    save('power_majesty', power_majesty())
    save('power_dominate', power_dominate())
    save('power_shroud', power_shroud())
    save('power_whispers', power_whispers())
    save('power_undercroft', power_undercroft())
    save('power_pounce', power_pounce())
    save('power_howl', power_howl())
    save('power_unchained', power_unchained())
    save('sfx_torpor', torpor())
    save('sfx_embrace', embrace())
    save('sting_danger', sting_danger())
    save('sting_good', chime())
    save('sfx_dawn_bell', dawn_bell())
    save('ui_click', click())
    total = sum(os.path.getsize(os.path.join(OUT, f)) for f in os.listdir(OUT))
    print(f'Done: {len(os.listdir(OUT))} files, {total / 1e6:.2f} MB')


if __name__ == '__main__':
    sys.exit(main())
