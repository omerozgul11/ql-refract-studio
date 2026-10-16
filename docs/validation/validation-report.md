# Refraction Studio — Scientific validation report

Generated 2026-10-11 02:01 UTC by `refraction-studio-cli validate`, version 1.0.0.

Each row compares the software's result with an independently known value: a hand calculation, a closed-form solution, or a synthetic earth model whose parameters are known exactly. Tolerances reflect the stated accuracy of each method.

**Result: 37 of 37 cases pass.**

## Engineering formulas (hand calculations)

| Case | Input | Expected | Actual | Difference | Tolerance | Result | Reference |
|---|---|---:|---:|---:|---:|---|---|
| Poisson's ratio | Vp 1800, Vs 400 m/s | 0.47403  | 0.47403  | 0 | ±1e-12 | PASS | ν = (Vp²−2Vs²)/(2(Vp²−Vs²)) |
| Shear modulus | ρ 1900 kg/m³, Vs 400 m/s | 3.04e+8 Pa | 3.04e+8 Pa | 0 | ±0.001 | PASS | G = ρVs² |
| Young's modulus | as above | 8.9621e+8 Pa | 8.9621e+8 Pa | 0 | ±0.001 | PASS | E = 2G(1+ν) |
| Bulk modulus | as above | 5.7507e+9 Pa | 5.7507e+9 Pa | 0 | ±0.001 | PASS | K = ρ(Vp² − 4Vs²/3) |
| E cross-check | as above | 8.9621e+8 Pa | 8.9621e+8 Pa | 1.1921e-7 | ±0.001 | PASS | E = 9KG/(3K+G) |
| Vs30 | 200 m/s 0–5 m, 400 m/s 5–20 m, 800 m/s below | 400 m/s | 400 m/s | 0 | ±1e-9 | PASS | 30/Σ(h/V) = 30/0.075 |
| Gardner density | Vp 2000 m/s | 2073.09495 kg/m³ | 2073.09495 kg/m³ | 0 | ±1e-9 | PASS | Gardner et al. (1974) |
| Brocher density | Vp 2000 m/s | 1905.392 kg/m³ | 1905.392 kg/m³ | 2.2737e-13 | ±1e-6 | PASS | Brocher (2005) eq. 1 |
| Feet → metres | 1 ft | 0.3048 m | 0.3048 m | 0 | ±1e-15 | PASS | NIST SP 811 |
| pcf → kg/m³ | 1 pcf | 16.01846 kg/m³ | 16.01846 kg/m³ | 0 | ±1e-9 | PASS | NIST SP 811 |
| ksf → Pa | 1 ksf | 47880.25898 Pa | 47880.25898 Pa | -7.276e-12 | ±1e-6 | PASS | NIST SP 811 |
| Straight-ray distance | x 30 m, Δz 40 m | 50 m | 50 m | 0 | ±1e-12 | PASS | R = √(x² + z²) |

## Ray tracing (closed-form travel times)

| Case | Input | Expected | Actual | Difference | Tolerance | Result | Reference |
|---|---|---:|---:|---:|---:|---|---|
| Homogeneous medium | 1500 m/s, receiver at (80, −20) m | 0.05497 s | 0.05514 s | 1.6889e-4 | ±2.7487e-4 | PASS | t = R/V; tolerance 0.5 %: the worst direction-dependent error of the 5-node network is about 0.34 % (unit test) |
| Head wave, two layers | 500/2000 m/s, h = 5 m, x = 100 m | 0.06936 s | 0.0694 s | 3.1161e-5 | ±1e-4 | PASS | t = x/V₂ + 2h√(1/V₁²−1/V₂²) |
| Linear velocity gradient | v = 400 + 40·z, x = 80 m | 0.10474 s | 0.10487 s | 1.3817e-4 | ±0.00105 | PASS | t = (2/g)·asinh(gx/2v₀) |

## Layered methods (synthetic models with known answers)

| Case | Input | Expected | Actual | Difference | Tolerance | Result | Reference |
|---|---|---:|---:|---:|---:|---|---|
| Intercept-time: V₁ | 500/2000 m/s, h = 5 m | 500 m/s | 500 m/s | 0 | ±0.5 | PASS | Pooled direct-wave slope |
| Intercept-time: V₂ | 500/2000 m/s, h = 5 m | 2000 m/s | 2000 m/s | 4.5475e-13 | ±2 | PASS |  |
| Intercept-time: max depth error | 2 positions | 5 m | 5 m | 8.8818e-16 | ±0.01 | PASS |  |
| Plus–minus: V₁ | 500/2000 m/s, h = 5 m | 500 m/s | 500 m/s | 0 | ±0.5 | PASS | Pooled direct-wave slope |
| Plus–minus: V₂ | 500/2000 m/s, h = 5 m | 2000 m/s | 2000 m/s | -1.819e-12 | ±2 | PASS |  |
| Plus–minus: max depth error | 12 positions | 5 m | 5 m | 1.7764e-15 | ±0.01 | PASS |  |
| GRM (XY = 2 m): V₁ | 500/2000 m/s, h = 5 m | 500 m/s | 500 m/s | 0 | ±0.5 | PASS | Pooled direct-wave slope |
| GRM (XY = 2 m): V₂ | 500/2000 m/s, h = 5 m | 2000 m/s | 2000 m/s | -1.819e-12 | ±2 | PASS |  |
| GRM (XY = 2 m): max depth error | 12 positions | 5 m | 5 m | 1.7764e-15 | ±0.01 | PASS |  |
| Delay-time: V₁ | 500/2000 m/s, h = 5 m | 500 m/s | 500 m/s | 0 | ±0.5 | PASS | Pooled direct-wave slope |
| Delay-time: V₂ | 500/2000 m/s, h = 5 m | 2000 m/s | 2000 m/s | -1.1007e-9 | ±2 | PASS |  |
| Delay-time: max depth error | 29 positions | 5 m | 5 m | -6.8079e-12 | ±0.01 | PASS |  |
| Dipping refractor: V₂ | 600/2500 m/s, 5° dip | 2500 m/s | 2500 m/s | 0 | ±0.01 | PASS | Exact two-layer dip formulas |
| Dipping refractor: depth at x = -1 m | vertical depth | 3.91251 m | 3.91251 m | 1.3323e-15 | ±0.001 | PASS |  |
| Dipping refractor: depth at x = 47 m | vertical depth | 8.11197 m | 8.11197 m | -1.7764e-15 | ±0.001 | PASS |  |
| Three layers (delay-time): V₃ | 400/1200/3000 m/s, h 3 + 8 m | 3000 m/s | 3000 m/s | -3.3478e-9 | ±3 | PASS | Layer stripping |
| Three layers (delay-time): refractor 2 depth | mean of all positions | 11 m | 11 m | -1.3782e-9 | ±0.1 | PASS |  |

## Tomography and Vs30 (synthetic surveys)

| Case | Input | Expected | Actual | Difference | Tolerance | Result | Reference |
|---|---|---:|---:|---:|---:|---|---|
| Gradient model: median velocity error | 244 well-covered cells | 0 fraction | 0.01207 fraction | 0.01207 | ±0.05 | PASS | v = 400 + 40·z |
| Gradient model: RMS travel-time residual | exact picks | 0 s | 2.9313e-4 s | 2.9313e-4 | ±5e-4 | PASS |  |
| Sample site Vs30 at x = 30 m | automatic SH picks + tomography | 552.64234 m/s | 571.94791 m/s | 19.30557 | ±27.63212 | PASS | True Vs30 of the synthetic model (±5 %) |
| Sample site Vs30 at x = 70.5 m | automatic SH picks + tomography | 581.30819 m/s | 599.54838 m/s | 18.24018 | ±29.06541 | PASS | True Vs30 of the synthetic model (±5 %) |
| Sample site Vs30 at x = 110 m | automatic SH picks + tomography | 659.00786 m/s | 675.66744 m/s | 16.65958 | ±32.95039 | PASS | True Vs30 of the synthetic model (±5 %) |

Automated unit tests (`dotnet test`) cover the same calculations and many more edge cases; this report documents the quantitative agreement.
