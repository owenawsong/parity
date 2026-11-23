"""Build Pairside's 48 kHz HRIR grid from MIT's original diffuse.zip archive.

Usage: python tools/pack-kemar.py path/to/diffuse.zip
Requires NumPy and SciPy only for regeneration. Source and terms: THIRD-PARTY-NOTICES.txt.
"""
import hashlib
import io
import re
import sys
import zipfile
from pathlib import Path

import numpy as np
from scipy.io.wavfile import read
from scipy.signal import resample_poly

def refine_azimuths(grid):
    """Align neighboring arrivals before blending to preserve treble during turns."""
    rows, columns, ears, taps = grid.shape
    fine = np.empty((rows, (columns - 1) * 4 + 1, ears, taps), dtype="<i2")
    fine[:, ::4] = grid
    # The stored filters run backward. Estimate lags and apply shifts in that
    # same coordinate system. Zero padding avoids wrapping either end of a tap.
    offsets = np.arange(-9, 10)
    window = np.kaiser(len(offsets), 8)

    def shifted(signal, delay):
        kernel = np.sinc(offsets - delay) * window
        return np.convolve(signal, kernel / kernel.sum(), "same")

    for row in range(rows):
        for col in range(columns - 1):
            for ear in range(ears):
                a, b = grid[row, col:col + 2, ear].astype(float)
                corr = np.correlate(b, a, "full")[taps - 6:taps + 5]
                peak = np.argmax(corr)
                lag = float(peak - 5)
                if 0 < peak < len(corr) - 1:
                    left, center, right = corr[peak - 1:peak + 2]
                    if center > min(left, right):
                        lag += .5 * (left - right) / (left - 2 * center + right)
                for step in (1, 2, 3):
                    t = step / 4
                    arrival = (1 - t) * shifted(a, t * lag) + t * shifted(b, (t - 1) * lag)
                    fine[row, col * 4 + step, ear] = np.rint(arrival).clip(-32768, 32767)
    return fine


if __name__ == "__main__":
    source = Path(sys.argv[1]).read_bytes()
    if hashlib.sha256(source).hexdigest() != "a48e73137390626ef1e45d1de6a4d98530c6aa570e71b40f7c4442241ed5fa22":
        raise ValueError("Expected MIT KEMAR diffuse.zip (44.1 kHz).")

    # Each filter is reversed for a vectorized dot product over chronological samples.
    # The half-sphere is mirrored at rendering time using KEMAR's symmetrical ear pair.
    grid = np.zeros((14, 37, 2, 144), dtype="<i2")
    with zipfile.ZipFile(io.BytesIO(source)) as archive:
        for row, elevation in enumerate(range(-40, 91, 10)):
            measured = {}
            for name in archive.namelist():
                m = re.fullmatch(fr"elev{elevation}/H{elevation}e(\d+)a.wav", name)
                if not m:
                    continue
                rate, raw = read(io.BytesIO(archive.read(name)))
                assert rate == 44100 and raw.shape == (128, 2)
                # Scale the sampled impulse to retain the filter's original integral.
                impulse = resample_poly(raw.astype(float), 160, 147, axis=0) * (147 / 160)
                measured[int(m[1])] = impulse
            # 50° elevation ends at 176°. The mirrored pair straddles the rear axis.
            if 180 not in measured:
                final = measured[max(measured)]
                measured[180] = np.repeat(final.mean(axis=1, keepdims=True), 2, axis=1)
            angles = sorted(measured)
            for col, angle in enumerate(range(0, 181, 5)):
                low = max(a for a in angles if a <= angle)
                high = min(a for a in angles if a >= angle)
                blend = (angle - low) / (high - low) if high > low else 0
                impulse = measured[low] * (1 - blend) + measured[high] * blend
                taps = np.rint(impulse).T
                assert np.max(np.abs(taps)) <= 32767
                grid[row, col, :, -taps.shape[1]:] = taps[:, ::-1]
    target = Path(__file__).resolve().parents[1] / "src/WPods/Audio/kemar48.bin"
    target.write_bytes(refine_azimuths(grid).tobytes())
