# Form Explanation — Wafer Defect Predictor

What you see in the app window and what each control does.

## Window layout

```
┌──────────────────────────────────────┬────────────────────────────────┐
│ Dataset                              │ Process parameters             │
│ ┌────────────────────────────────┐   │  Temperature (°C)   [ 400 ]    │
│ │ DataGrid: 200 wafer rows       │   │  Pressure (mTorr)   [ 100 ]    │
│ │ - red rows = IsDefective true  │   │  EtchTime (s)       [  60 ]    │
│ │ - white rows = pass            │   │  ParticleCount      [  10 ]    │
│ │ - 7 columns: 6 features + label│   │  FilmThickness (nm) [ 100 ]    │
│ └────────────────────────────────┘   │  Uniformity (%)     [  99 ]    │
│ "200 wafers · 32% defective ·        │  Defective (label)  [ ☐ ]      │
│  source: …\Data\wafers.csv"          │                                │
│                                      │ Dataset actions                │
│                                      │  [Add Row] [Save] [Reset]      │
│                                      │                                │
│                                      │ Model actions                  │
│                                      │  [Train Model] [Predict]       │
│                                      │                                │
│                                      │ Status: …                      │
│                                      │ ┌──────────────────────────┐   │
│                                      │ │ Result: PASS / DEFECTIVE │   │
│                                      │ └──────────────────────────┘   │
└──────────────────────────────────────┴────────────────────────────────┘
```

## Default input values

These are pre-filled when the app opens. They sit at the **optimum** for every parameter — i.e. a "perfect" wafer. Predicting with these defaults should yield **PASS** with very low defect probability.

| Field           | Default     | Meaning                                                       |
|-----------------|-------------|---------------------------------------------------------------|
| Temperature     | **400** °C  | Centre of the safe range (350–450). Drift away → defects.     |
| Pressure        | **100** mTorr | Centre of safe range (50–150).                              |
| EtchTime        | **60** s    | Centre of safe range (30–90).                                 |
| ParticleCount   | **10**      | Comfortably below the 15-particle threshold.                  |
| FilmThickness   | **100** nm  | Target deposition thickness (range 90–110).                   |
| Uniformity      | **99** %    | Comfortably above the 98 % threshold (range 90–100).          |
| Defective ☐     | unchecked   | Used **only by Add Row** as the label of a new training row.  |

## How the inputs are used

The same six textboxes are read by **two** different buttons, with one difference:

- **Add Row** — reads all 6 numbers **plus** the *Defective* checkbox, appends a new labelled row to the dataset (bottom of the grid). The model needs retraining after this, so the **Predict** button gets disabled until you click **Train Model** again.
- **Predict** — reads the 6 numbers, **ignores** the *Defective* checkbox (the model decides the label), and shows a green **PASS** or red **DEFECTIVE** with the probability.

## What the dataset on the left is

- On first launch, `Data/wafers.csv` didn't exist, so the app generated 200 synthetic wafers with `Random(42)` and saved them. That same set loads on every fresh launch (reproducible).
- Each row has the 6 process parameters plus an `IsDefective` boolean.
- Defective rows are tinted light red; the bottom status line shows the count and class balance (e.g. `200 wafers · 32% defective`).
- After clicking **Reset Dataset**, the grid is replaced with a *different* 200 wafers (random seed each click).

## Buttons in detail

### Dataset actions
| Button           | What it does                                                                                            |
|------------------|---------------------------------------------------------------------------------------------------------|
| **Add Row**      | Validates the 6 inputs, reads the *Defective* checkbox, appends a new row. Disables Predict until retrain.|
| **Save Dataset** | Writes the current grid to `Data\wafers.csv` (overwriting). Status line shows the path and row count.   |
| **Reset Dataset**| Confirms, then regenerates 200 fresh wafers with a random seed and overwrites the CSV.                  |

### Model actions
| Button         | What it does                                                                                                |
|----------------|-------------------------------------------------------------------------------------------------------------|
| **Train Model**| Fits LightGBM (binary classification) on the current grid; status shows test-set Accuracy / AUC / F1.       |
| **Predict**    | Disabled until trained. Runs the model on the 6 input fields; result panel goes green (PASS) or red (DEFECTIVE) with probability. |

## Try this end-to-end

| Action                                       | Expected                                              |
|----------------------------------------------|-------------------------------------------------------|
| Click **Train Model**                        | Status: `Accuracy ≈ 0.93 · AUC ≈ 0.97 · F1 ≈ 0.91`    |
| Click **Predict** with defaults              | Green **PASS**, `P(defective)` ≈ 1–5 %                |
| Set ParticleCount=70, Uniformity=92, **Predict** | Red **DEFECTIVE**, `P(defective)` high            |
| Set Defective ☐ = checked, **Add Row**       | New red row appears at the bottom; Predict disables   |
| Click **Train Model** again                  | Predict re-enables; metrics update slightly           |
| Click **Reset Dataset** → Yes                | Grid replaced with a different 200 rows (fresh seed)  |

## Status messages you'll see

| Status text                                        | Meaning                                                         |
|----------------------------------------------------|-----------------------------------------------------------------|
| "Click Train Model to fit the LightGBM classifier…"| Initial state, just after launch.                               |
| "Trained · Accuracy 0.930 · AUC 0.972 · F1 0.910"  | Train succeeded; Predict is now enabled.                        |
| "Row added. Re-train the model to incorporate it." | Add Row succeeded; Predict was disabled.                        |
| "Saved 213 wafers to …\\wafers.csv"                | Save Dataset succeeded.                                         |
| "Dataset reset — 200 fresh wafers generated."      | Reset Dataset succeeded with a new seed.                        |
| "Add Row failed: Temperature is not a number."     | A textbox couldn't be parsed (use `.` as decimal separator).    |
| "Need at least ~20 rows to train."                 | Dataset too small after deletions.                              |
