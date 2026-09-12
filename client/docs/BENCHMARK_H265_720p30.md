# DroidLens — H.265 720p30 resource consumption benchmark

> **⚠️ Archival note.** Measurements below were taken on **2025-08-23** against
> the code state of that time and **may not reflect the current code**.
> The original monitoring scripts were since lost (they lived outside the repo),
> so the runs cannot be replayed 1:1 — the methodology section preserves enough
> detail to recreate them. Treat the numbers as a historical reference point,
> not as current performance claims. Re-running on a fresh build is planned.

- **Builds:** `Debug` vs `Release`, `net10.0-windows`, `win-x64`, self-contained
- **Codec:** H.265 / HEVC over TCP, 720p, 30 fps
- **Decoder:** FFmpeg 7.x (LGPL build) with D3D11VA hardware decoding and CPU fallback
- **Test machine:** 12 logical cores, NVIDIA GeForce RTX 3060 12 GB, Windows 10/11 x64

---

## Methodology

Task Manager averages over 1–2 s, reports `GPU 3D` instead of `Video Decode`,
and gives no Private Bytes — so a custom PowerShell monitor was used:

- 1 sample / 1 s; CPU % normalized by core count
  (`Δ TotalProcessorTime / Δ wallTime / cores * 100`)
- Metrics per sample: process CPU %, total system CPU %, WorkingSet MB,
  Private MB, Handles, Threads, per-PID GPU Engine % (`\GPU Engine(*)\Utilization
  Percentage`), whole-GPU % (`nvidia-smi`)
- 1 CSV row = 1 second; stats: `avg` / `p95` / `max` / `min`
- For fair comparison, a `STABLE (>10 s)` slice excludes the warm-up phase
  (first ~10 s are FFmpeg / UI / queue initialization)

Tests 1–3 ran `Debug` builds; Test 4 ran `Release`
(`dotnet publish -c Release -r win-x64 --self-contained`).

---

## Test 1 — Default (55 s)

H.265 720p30, default bitrate/quality, preview **ON**, `Debug`.

| Metric | ALL (n=55) avg / p95 / max / min | STABLE >10 s (n=44) avg / p95 / max |
|---|---|---|
| **Process CPU %** | 6.34 / 7.65 / 8.29 / 0.45 | **6.53 / 7.63 / 7.7** |
| System CPU % | 13.82 / 17.80 / 20.2 | 13.9 / 17.8 |
| **RAM WorkingSet MB** | 260.78 / 271.80 / 281.0 | **268.50 / 271.50 / 281.0** |
| **RAM Private MB** | 275.31 / 290.90 / 294.9 | **283.87 / 289.60 / 290.9** |
| **GPU Engine %** | 14.19 / 18.30 / 19.2 | **15.25 / 18.30 / 19.2** |
| GPU (nvidia-smi) % | 33.93 / 46.00 / 49.0 | 35.76 / 46.0 |
| Handles / Threads | ~1015 / 98 | stable |

Observations:

- Warm-up `0–3 s`: WorkingSet climbs `105 → 190 → 247 → 269 MB`, then plateaus
  after ~6 s — no leaks.
- Per-PID GPU Engine ~15% confirms D3D11VA was active (without it, CPU would
  have been well above 15%).
- 6.53% on 12 cores ≈ **~78% of one core**. The main cost was not the decode
  itself but `sws_scale` (NV12→RGBA) + frame copies to the UI bitmap and the
  virtual-camera BGR24 conversion.

---

## Test 3 — Tuned run (60 s)

H.265 720p30, **lowered bitrate + preview OFF**, `Debug`.

| Metric | ALL (n=60) avg / p95 / max / min | STABLE >10 s (n=49) avg / p95 / max |
|---|---|---|
| **Process CPU %** | 6.12 / 7.66 / 9.11 / 0.13 | **6.23 / 7.42 / 8.1** |
| **RAM WorkingSet MB** | 257.09 / 268.40 / 274.6 | **260.88 / 265.90 / 268.4** |
| **RAM Private MB** | 265.12 / 279.60 / 296.9 | **269.02 / 271.80 / 272.8** |
| **GPU Engine %** | 9.05 / 9.80 / 13.1 | **9.28 / 9.70 / 9.8** |
| GPU (nvidia-smi) % | 38.25 / 43.00 / 44.0 | 39.49 / 43.0 |
| Handles / Threads | ~1015 / 94 | stable |

Test 1 vs Test 3 (STABLE):

| Metric | Test 1 | Test 3 | Δ |
|---|---|---|---|
| CPU avg | 6.53% | 6.23% | **-4.6%** |
| RAM WS avg | 268.50 MB | 260.88 MB | **-2.8%** |
| RAM Private avg | 283.87 MB | 269.02 MB | **-5.2%** |
| GPU Engine avg | 15.25% | 9.28% | **-39.1%** |

Biggest win came from disabling preview (no per-frame UI render, GPU −39%);
lower bitrate gave a modest CPU/RAM improvement. Both runs stable —
Handles/Threads flat, p95 close to avg.

---

## Test 4 — Release (60 s)

Same stream as Test 3, but a **Release** build.

| Metric | ALL (n=60) avg / p95 / max / min | STABLE >10 s (n=49) avg / p95 / max |
|---|---|---|
| **Process CPU %** | 3.69 / 5.32 / 7.38 / 0.13 | **3.64 / 4.64 / 5.2** |
| **RAM WorkingSet MB** | 250.39 / 264.40 / 268.7 | **254.37 / 258.60 / 259.0** |
| **RAM Private MB** | 262.23 / 286.20 / 293.8 | **265.93 / 270.50 / 270.9** |
| **GPU Engine %** | 8.61 / 9.80 / 13.5 | **8.77 / 9.80 / 10.1** |
| GPU (nvidia-smi) % | 34.83 / 44.00 / 45.0 | 35.22 / 43.0 |
| Handles / Threads | ~1018 / 96 | stable |

Test 1 vs Test 3 vs Test 4 (STABLE):

| Metric | T1 Debug default | T3 Debug tuned | **T4 Release tuned** | Δ T1→T4 |
|---|---|---|---|---|
| CPU avg | 6.53% | 6.23% | **3.64%** | **-44.3%** |
| CPU p95 | 7.63% | 7.42% | **4.64%** | -39% |
| RAM WS avg | 268.50 MB | 260.88 MB | **254.37 MB** | -5.3% |
| GPU Engine avg | 15.25% | 9.28% | **8.77%** | **-42.5%** |

Takeaways:

- Product tuning (Test 1→3): GPU −39%, CPU −4.6%.
- Build flavor (Test 3→4, same code): CPU −41.6% from JIT optimization —
  more than the typical 10–15% rule of thumb.
- 3.64% on 12 cores ≈ **~43% of one core** — in line with native players on
  the same stream (OBS ~3–5%, browser 720p H.265 ~4–8%).
- RAM 254–265 MB is typical for self-contained .NET (runtime + UI + FFmpeg +
  small bounded frame queue). GPU load is decode + UI render, modest on the
  test card.

---

## Reproducing (guidance, no original scripts)

The original `.ps1` monitor and CSVs were not preserved. To recreate:

1. Publish Release: `dotnet publish -c Release -r win-x64 --self-contained`
2. Stream H.265 720p30 from the phone for 60 s (preview OFF, fixed quality).
3. Sample every 1 s for 60 s: process CPU %, total CPU %, WorkingSet, Private
   Bytes, Handles, Threads, per-PID `\GPU Engine(*)\Utilization Percentage`,
   `nvidia-smi` utilization. Normalize CPU by logical core count.
4. Discard the first 10 s (warm-up), report avg / p95 / max.
