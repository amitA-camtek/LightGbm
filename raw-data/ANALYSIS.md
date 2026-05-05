# IllumCalib Diff — Analysis

This folder contains two illumination-calibration dumps from a Camtek-style optical inspection tool:

| File | Calibration runs (per channel) |
|---|---|
| [IllumCalib_Diff.ini](IllumCalib_Diff.ini) | 2023-11-01 **07:18 → 07:25** (newer) |
| [IllumCalib_Diff_01.ini](IllumCalib_Diff_01.ini) | 2023-11-01 **06:16 → 06:22** (older) |

Same machine, same day, ~62 minutes apart. The `_01` suffix is the older snapshot; the unsuffixed file is the current calibration.

---

## 1. What these files are

- **Format:** Windows INI — sections in `[brackets]`, `key=value` lines, no quoting.
- **Purpose:** output of the lamp / voltage → gray-level calibration routine for the inspection optics. The presence of per-channel `BilinearVoltageTransform_A0/A1/A2` coefficients and `DumperFilterCoeff_0..5` arrays identifies these as calibration-result files (one set of coefficients per color filter), not recipe files.
- **Channels:** six color filters, `ColorFilter_0` … `ColorFilter_5`, each with its own polynomial fit and intensity range.
- **Per-channel record fields:** `RunDate`, `RunSuccess`, `DataExists`, `PolynomOrder`, `MinLightIntensity`, `MaxLightIntensity`, `Enable`, `CompensationFactor`.

---

## 2. Section-by-section reference

### `[TargetLightResponse]`
Two reference targets used during calibration. `Target0Coeff=1.000000` is the primary reference; `Target1Coeff=0.244747` is the secondary (≈24.5 % response of target 0). Identical in both files.

### `[Voltage]` and `[BaseLinearVoltage]`
- `[Voltage] BaseLinearVoltage=-156.673542` — the global lamp-driver base voltage.
- `[BaseLinearVoltage] ColorFilter_0..5 = -1.000000` — per-channel overrides, all `-1` meaning "unset, use the global value above".

### `[FilterTransparency]`
Six neutral-density ("dumper") filter transmissions, monotonic from `0.10` to `1.00`:

| Filter | 0 | 1 | 2 | 3 | 4 | 5 |
|---|---|---|---|---|---|---|
| Transparency | 0.100 | 0.183 | 0.284 | 0.458 | 0.668 | 1.000 |

Identical in both files.

### `[IlluminationPower]`
Per-color-filter lamp power. **Only `ColorFilter_0` is non-zero** (`1.374300`); the other five are `0.000000`. Either all six filters share a single lamp whose power is reported only on CF_0, or five of the six channels are inactive in this configuration. Identical in both files. (See open questions, §5.)

### `[ColorFilter_0]` … `[ColorFilter_5]` — the calibration body
Each block contains:

- **`BilinearVoltageTransform_A0/A1/A2`** — coefficients of the voltage→gray-level fit. With `PolynomOrder=3` and three coefficients, this is a 2nd-degree polynomial: `gray ≈ A0 + A1·V + A2·V²`. The "Bilinear" naming is a tool-internal label; the math is a quadratic.
- **`DumperFilterCoeff_0..5`** — per-channel correction factors layered on top of the global `[FilterTransparency]` values.
- **`Gl0=5.640000`** — zero-light gray-level offset (sensor dark level reference). Identical across all channels and both files.
- **`MinLightIntensity` / `MaxLightIntensity`** — the achieved gray-level range during this calibration run.
- **`Enable=1`, `CompensationFactor=1.000000`, `DataExists=1`, `RunSuccess=1`** — every channel calibrated successfully in both snapshots.
- **`LifeLengthName=Illumination`** — the lamp/component lifetime counter this calibration is bound to.
- **`RunDate`** — the per-channel timestamp (channels are calibrated sequentially over ~6–7 minutes).

`ColorFilter_0` carries two extra fields not present on the others:
- `DumperFilterCoeff_9=-1.000000` — sentinel for an unused 10th filter slot.
- `CompensationFactor_5.00=1.000000` — a 5×-gain compensation entry (CF_1 has the equivalent `CompensationFactor_2.00=1.000000`).

### `[SystemHighIntensityMaxLimit]` and `[SystemHighIntensityMinLimit]`
Acceptance band for the per-channel **`MaxLightIntensity`** result. After calibration, the achieved max should fall inside `[MinLimit, MaxLimit]` per channel, otherwise the calibration is flagged. **This is the section that differs most between the two snapshots — see §3.**

### `[SystemLowIntensityMinLimit]`
All zeros in both files — no enforced floor on the low-intensity end.

### `[IlluminationLevelTest]`
A separate validation step:
```
DataExists=1
Enable=0
ActualNominalGrayLevel=6.91
RunSuccess=0
RunDate=2023-04-11 15:47:53
```
**Disabled** (`Enable=0`), last run on 2023-04-11 (~7 months before these calibrations), and that run **failed** (`RunSuccess=0`). Identical in both files.

### `[LampCalibrationValidation]`
Acceptance criteria for the lamp-validation grab routine:

| Field | Value |
|---|---|
| `TargetIndex` | 0 |
| `NominalGrayLevel` | 128 |
| `NumberImagesForGrabbing` | 10 |
| `MinGrayLevel` | 120 |
| `MaxGrayLevel` | 135 |

Grab 10 images, accept if mean gray level ∈ [120, 135] around target 128. Identical in both files.

---

## 3. Side-by-side comparison

### Identical between the two files
- `[TargetLightResponse]`, `[Voltage]`, `[FilterTransparency]`, `[IlluminationPower]`, `[BaseLinearVoltage]`
- `DumperFilterCoeff_*` for every channel
- `Gl0`, `PolynomOrder`, `Enable`, `CompensationFactor`, `LifeLengthName`
- `[SystemHighIntensityMinLimit]` (same numeric values; see "stray key" below)
- `[SystemLowIntensityMinLimit]`, `[IlluminationLevelTest]`, `[LampCalibrationValidation]`

### Polynomial coefficients — small drift per channel
File 1 (newer 07:xx) **minus** file 2 (older 06:xx):

| Channel | ΔA0 | ΔA1 | ΔA2 |
|---|---:|---:|---:|
| CF_0 | −0.39 | −0.0008 | +1.1e-5  |
| CF_1 | −0.83 | +0.060  | −1.08e-3 |
| CF_2 | +0.29 | +0.0024 | −1.28e-4 |
| CF_3 | +0.34 | −0.029  | +2.77e-4 |
| CF_4 | +0.66 | −0.045  | +4.02e-4 |
| CF_5 | +0.11 | −0.038  | +2.90e-4 |

These are sub-percent shifts on A0 and A1; A2 deltas are ≤ 1e-3. Consistent with re-running the same fit an hour later — lamp warm-up / measurement noise — rather than any real change in the optics.

### `MaxLightIntensity` (achieved range)

| Channel | File 1 | File 2 | Δ |
|---|---:|---:|---:|
| CF_0 | 367  | 367  | 0 |
| CF_1 | 2688 | 2694 | −6 |
| CF_2 | 523  | 524  | −1 |
| CF_3 | 425  | 425  | 0 |
| CF_4 | 1883 | 1883 | 0 |
| CF_5 | 775  | 776  | −1 |

Essentially identical — deltas of 0–6 counts on values up to 2694.

### `[SystemHighIntensityMaxLimit]` — uniformly ~4 % higher in the newer file

| Channel | File 1 (07:xx, newer) | File 2 (06:xx, older) | Δ | Δ % |
|---|---:|---:|---:|---:|
| CF_0 | 300  | 288  | +12  | +4.2 % |
| CF_1 | 2600 | 2500 | +100 | +4.0 % |
| CF_2 | 360  | 346  | +14  | +4.0 % |
| CF_3 | 335  | 322  | +13  | +4.0 % |
| CF_4 | 1915 | 1841 | +74  | +4.0 % |
| CF_5 | 575  | 553  | +22  | +4.0 % |

The flat ~4 % bump across all six channels is the substantive change — it is a single global widening of the upper acceptance band, not a per-channel re-tune.

### Stray key
File 1 has an extra line under `[SystemHighIntensityMinLimit]`:
```
ColorFilter_-1=0
```
Almost certainly a buffer-write artefact / harmless garbage index — there is no `ColorFilter_-1` channel anywhere else.

---

## 4. Interpretation

- **`_01` is the older backup, the unsuffixed file is current.** The convention is "rotate the previous calibration to `_01` before writing the new one."
- **The optics are stable.** Coefficient drift and `MaxLightIntensity` deltas are at the noise floor; rerunning the calibration an hour later gave essentially the same fit.
- **One real change between the two runs:** the upper-limit band (`[SystemHighIntensityMaxLimit]`) widened by ~4 % on every channel. Worth confirming whether that was an intentional spec relaxation, a recipe update, or an automatic recompute from a higher reference target.
- **`[IlluminationLevelTest]` has been disabled and last failed on 2023-04-11**, ~7 months before these calibrations. If this machine is supposed to validate against a target gray level after lamp calibration, that's a gap worth flagging.

---

## 5. Open questions

- Why is only `ColorFilter_0` powered (`IlluminationPower = 1.374`) while CF_1..5 report `0.000`? Either the channels share a single lamp whose power is logged only against CF_0, or five of the six filter positions are inactive in this configuration.
- Was the +4 % widening of `[SystemHighIntensityMaxLimit]` between the 06:xx and 07:xx runs intentional? If so, what triggered it (recipe edit, target recalibration, manual override)?
- What does the `_01` suffix denote in this tool's archive — generation index, retry index, or a fixed two-slot rotation?
