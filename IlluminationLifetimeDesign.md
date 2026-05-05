# Design — Illumination Lifetime Predictor

Technical design notes for the LightGBM regression that predicts **remaining useful life (RUL)** of an inspection-tool illumination lamp from `IllumCalib_Diff*.ini` calibration snapshots.

The goal: given one calibration snapshot, output **how many days until the next lamp replacement**.

---

## 1. Block diagram

```
┌────────────────────────────────────────────────────────────────────────────┐
│                              INPUT LAYER                                   │
│                                                                            │
│   {root}/                              replacements.csv                    │
│   ├── MachineA/                        ┌────────────────────┐              │
│   │   ├── IllumCalib_Diff.ini          │ MachineId,         │              │
│   │   ├── IllumCalib_Diff_01.ini       │ ReplacementDate    │              │
│   │   └── ...                          │ MachineA,2023-08-15│              │
│   ├── MachineB/                        │ MachineA,2024-01-22│              │
│   │   └── ...                          │ MachineB,2023-11-30│              │
│   └── ...                              └────────────────────┘              │
└──────┬─────────────────────────────────────────┬───────────────────────────┘
       │ INI files                               │ CSV
       ▼                                         ▼
┌────────────────────────────────┐   ┌───────────────────────────────┐
│      IniSnapshotParser         │   │     ReplacementLogReader      │
│  (single .ini → snapshot POCO) │   │  (csv → IList<LampReplacement>)│
└──────────────┬─────────────────┘   └───────────────┬───────────────┘
               │                                     │
               ▼                                     ▼
            ┌──────────────────────────────────────────┐
            │         SnapshotFolderLoader             │
            │  walk root → parse each ini → sort by    │
            │  (machine, runDate) → compute            │
            │  DaysSincePrevCalibration → join with    │
            │  replacement log → assign RulDays label  │
            │  → drop right-censored                   │
            └─────────────────┬────────────────────────┘
                              │ IList<IllumCalibSnapshot> (labeled)
                              ▼
            ┌──────────────────────────────────────────┐
            │           LifetimeRegressor              │
            │  TimeAwareSplit (per machine, last 20%)  │
            │      │                                   │
            │      ▼                                   │
            │  ML.NET pipeline:                        │
            │   OneHotHashEncoding(MachineId, 10b)     │
            │   .Append(Concatenate("Features"))       │
            │   .Append(Regression.Trainers.LightGbm)  │
            │      │                                   │
            │      ▼                                   │
            │  ITransformer ──► PredictionEngine       │
            │                   <Snapshot,RulPrediction>│
            └──────────────────┬───────────────────────┘
                               │
                ┌──────────────┼──────────────────┐
                ▼              ▼                  ▼
          model.zip      RegressionMetrics    RulPrediction
        (Save/Load)     (MAE, RMSE, R²)    (PredictedRulDays)
```

---

## 2. Project layout

```
WaferDefectDemo.sln
├── WaferDefectDemo/            (existing — WPF app, untouched)
├── WaferDefectModel/           (existing — library, untouched)
├── IlluminationLifetimeModel/  (NEW class library, net8.0)
│   ├── IlluminationLifetimeModel.csproj
│   ├── Models/
│   │   ├── IllumCalibSnapshot.cs   ← 38-feature input row + RulDays label
│   │   └── RulPrediction.cs        ← model output (PredictedRulDays)
│   └── Services/
│       ├── IniSnapshotParser.cs    ← .ini → IllumCalibSnapshot
│       ├── ReplacementLogReader.cs ← .csv → IList<LampReplacement>
│       ├── SnapshotFolderLoader.cs ← orchestrates parsing + labelling
│       └── LifetimeRegressor.cs    ← train / predict / save / load
└── IlluminationLifetimeCli/    (NEW console app, net8.0)
    ├── IlluminationLifetimeCli.csproj
    └── Program.cs              ← `build` / `train` / `predict` commands
```

**Layers**
- **Models** — pure POCOs. `IllumCalibSnapshot` carries 38 features + a `[ColumnName("Label")] float RulDays` for the trainer. `RulPrediction` carries `[ColumnName("Score")] float PredictedRulDays` for inference.
- **Services** — all business logic. Only `LifetimeRegressor` touches `MLContext`.
- **CLI** — thin entry point. Three verbs: `build` (data prep only), `train` (data prep + train + save), `predict` (load + parse single INI + predict).

The package reference set mirrors `WaferDefectModel`: `Microsoft.ML 3.0.1` + `Microsoft.ML.LightGbm 3.0.1`.

---

## 3. Feature schema (38 features per snapshot)

| Group | Per channel (×6 channels) | Single-value | Total |
|---|---|---|---|
| Polynomial fit | `CfNA0`, `CfNA1`, `CfNA2` | — | 18 |
| Achieved range | `CfNMax`, `CfNMin` | — | 12 |
| Headroom | `CfNMargin = CfNMax − SystemHighIntensityMaxLimit[N]` | — | 6 |
| Cadence | — | `DaysSincePrevCalibration` | 1 |
| Identity | — | `MachineId` (string, hash-encoded) | 1 |

**Why each group exists**
- **A0/A1/A2** — the voltage→intensity polynomial coefficients drift as the lamp ages. The two sample files showed sub-percent drift over 1 hour; over months that drift becomes the dominant aging signal.
- **Max / Min light intensity** — the achieved gray-level range. A degrading lamp can no longer hit the same maximum at a fixed driver voltage.
- **Margin** — distance between the achieved max and the per-channel upper acceptance limit. A lamp approaching end of life will see this margin shrink (or invert).
- **DaysSincePrevCalibration** — recipe / cadence signal. Calibrations tend to get more frequent as a lamp ages.
- **MachineId** — captures machine-specific drift personalities. Hash-encoded to 10 bits (1024 buckets) so the model learns per-machine offsets without one-hot blow-up.

**Excluded fields (deliberate)**
- `DumperFilterCoeff_*` — constant across both sample files; treat as zero-variance until real data shows otherwise. Easy to add back as `CfNDumper0..5`.
- `[FilterTransparency]`, `[TargetLightResponse]`, `[Voltage]`, `[LampCalibrationValidation]`, `[IlluminationLevelTest]` — recipe-level constants in the sample data. Not predictive at the snapshot level.

---

## 4. Data flow — `build` (dataset preparation)

```
CLI args: build <root> <log.csv> <out.csv>
   │
   ▼
ReplacementLogReader.Read(log.csv)
   │ ► IList<LampReplacement>(MachineId, ReplacementDate)
   ▼
SnapshotFolderLoader.Build(root, replacements, log)
   │
   │  for each subfolder (= machine):
   │    1. enumerate "IllumCalib_Diff*.ini"
   │    2. IniSnapshotParser.Parse(file, machineId)
   │       → reads sections [ColorFilter_0..5],
   │         pulls A0/A1/A2, MaxLightIntensity, MinLightIntensity,
   │         joins with [SystemHighIntensityMaxLimit] for IntensityMargin,
   │         derives RunDate from per-channel timestamps (max).
   │    3. sort snapshots ascending by RunDate
   │    4. fill DaysSincePrevCalibration (0 for first snapshot per machine)
   │    5. for each snapshot:
   │         next = first replacement on same machine where date >= RunDate
   │         RulDays = (next.Date - RunDate).TotalDays   if next exists
   │                 = NaN  → right-censored, dropped from labeled
   │  return DatasetBuildResult(Labeled, RightCensored, FailedFiles)
   ▼
WriteSnapshotCsv(out.csv, result.Labeled)
   │ ► flat CSV: 41 columns (4 metadata + 36 numeric features + 1 cadence + Label)
```

The dataset CSV is for **inspection only** — it is not the training input. Training reads the in-memory snapshot list directly via `MLContext.Data.LoadFromEnumerable`.

---

## 5. Data flow — `train`

```
CLI args: train <root> <log.csv> <model.zip>
   │
   ▼
SnapshotFolderLoader.Build(...)        ← same as above
   │
   ▼
LifetimeRegressor.Train(labeled)
   │
   │  guard: labeled.Count < 10 → throw  (prevents nonsense regression)
   │
   │  TimeAwareSplit(labeled, 0.2)
   │    per machine: sort by RunDate; first 80 % → train, last 20 % → test
   │    (NEVER random — would leak future into past)
   │
   │  pipeline:
   │    OneHotHashEncoding("MachineId" → "MachineIdEncoded", 10 bits)
   │    .Append(Concatenate("Features",
   │            "MachineIdEncoded",
   │            "DaysSincePrevCalibration",
   │            "Cf0A0", "Cf0A1", "Cf0A2", "Cf0Max", "Cf0Min", "Cf0Margin",
   │            ...  // CF1..CF5
   │          ))
   │    .Append(Regression.Trainers.LightGbm(
   │            labelColumnName: "Label",
   │            featureColumnName: "Features"))
   │
   │  pipeline.Fit(trainData)  → ITransformer
   │  model.Transform(testData)
   │  Regression.Evaluate(...) → RegressionMetrics(MAE, RMSE, R²)
   │
   ▼
regressor.Save(model.zip)
   │ ► MLContext.Model.Save(model, schema, path)
```

Default LightGBM hyperparameters (100 trees, 31 leaves, lr=0.2, min-data-in-leaf=10) — same as the existing `DefectClassifier`. Tuning is a separate concern; first prove the pipeline with defaults.

---

## 6. Data flow — `predict`

```
CLI args: predict <model.zip> <single.ini> <machine-id>
   │
   ▼
LifetimeRegressor.Load(model.zip)
   │ ► MLContext.Model.Load → ITransformer + PredictionEngine
   ▼
IniSnapshotParser.Parse(single.ini, machine-id)
   │ ► IllumCalibSnapshot (label is NaN — irrelevant for prediction)
   ▼
regressor.Predict(snapshot)
   │ ► RulPrediction { PredictedRulDays = engine.Predict(...).Score }
   ▼
print "Predicted RUL: X.X days"
```

`DaysSincePrevCalibration` will be 0 for an isolated single-file prediction — this is a known limitation. For best accuracy on production predictions, parse the previous snapshot for the same machine first and compute the gap externally before calling `Predict`.

---

## 7. Why these design choices

**Time-aware split, not random.** `MLContext.Data.TrainTestSplit` shuffles randomly, which is correct for IID classification (the Wafer demo) but **wrong for time-series RUL** — a randomly chosen test row could come earlier than its training neighbours, leaking future drift into past. `TimeAwareSplit` holds out the chronological tail per machine, so the test set always asks "given history up to day X, can we predict the future?" — the deployment scenario.

**Right-censoring → drop, not impute.** Snapshots taken after the most recent known replacement on a machine have no future replacement event yet, so RUL is unknowable. We drop them from training (label = NaN, returned in `DatasetBuildResult.RightCensored`). They can still be parsed and predicted on. A future improvement is proper Cox / survival regression on these — out of scope for v1.

**MachineId as a hash-encoded categorical.** With hundreds of machines, one-hot is wasteful and pure label-encoded ordinals invent false orderings. `OneHotHashEncoding(numberOfBits: 10)` projects to 1024 sparse buckets — collision rate ≪ 5 % for ~50 distinct machines, and trees handle sparse vectors natively.

**No normalization.** LightGBM is tree-based and scale-invariant. Same call shape as the existing `DefectClassifier`.

**Single MLContext, seeded.** Reproducibility on top of a known-good train/test split is more useful than wall-clock-seeded randomness during early development. Mirrors `DefectClassifier._ml = new(seed: 1)`.

**One snapshot per INI = one feature row.** No sliding-window or rolling-feature engineering yet. The existing time signal lives in `DaysSincePrevCalibration` and (for the model) in the implicit drift of A0/A1/A2 across sequential snapshots on the same machine. If MAE turns out poor, a v2 with rolling-mean / first-derivative features is the natural next step.

---

## 8. Verification

**End-to-end smoke test against the two sample INI files in [raw-data/](raw-data/):**

```powershell
# 1. Stage a per-machine layout
New-Item -ItemType Directory -Force -Path C:\temp\illum-test\MachineA | Out-Null
Copy-Item "raw-data\IllumCalib_Diff.ini"    C:\temp\illum-test\MachineA\
Copy-Item "raw-data\IllumCalib_Diff_01.ini" C:\temp\illum-test\MachineA\
"MachineId,ReplacementDate`nMachineA,2023-12-01" | Set-Content C:\temp\illum-test\replacements.csv

# 2. Build the dataset
dotnet run --project IlluminationLifetimeCli -- `
    build C:\temp\illum-test C:\temp\illum-test\replacements.csv C:\temp\illum-test\dataset.csv

# Expected:
#   [MachineA] 2 snapshots (2 labeled, 0 right-censored)
#   Labeled snapshots:        2
#   Wrote dataset -> C:\temp\illum-test\dataset.csv
```

The produced [dataset.csv](file:///C:/temp/illum-test/dataset.csv) should show:
- Two rows, sorted by `RunDate` (the `_01` 06:22 file first, then the unsuffixed 07:25 file).
- `DaysSincePrevCalibration` = 0 on row 1 and ≈0.0434 on row 2 (62 minutes between calibrations).
- `RulDays` = `(2023-12-01 - RunDate).TotalDays` — about 29.7 on both rows.
- `Cf0Margin` = 79 on row 1, 67 on row 2 (matches the +12 delta in [raw-data/ANALYSIS.md](raw-data/ANALYSIS.md)).
- `Cf4Margin` = 42 on row 1 vs −32 on row 2 (the achieved Max overshoots the limit on the newer snapshot).

**Train cannot run on 2 rows by design** — `LifetimeRegressor.Train` throws if labeled.Count < 10. Once you have real historical data, train end-to-end with:

```powershell
dotnet run --project IlluminationLifetimeCli -- `
    train  <root> <replacements.csv> <model.zip>

dotnet run --project IlluminationLifetimeCli -- `
    predict <model.zip> <some-machine>\IllumCalib_Diff.ini <some-machine>
```

---

## 9. Out of scope (deliberate)

- WPF UI integration into [WaferDefectDemo](WaferDefectDemo/) — the CLI is the v1 interface; UI is a separate task.
- Hyperparameter tuning (`numberOfIterations`, `numberOfLeaves`, `learningRate`).
- Survival-style modelling (Cox regression / Kaplan-Meier) for right-censored snapshots.
- Per-channel sub-models (one regressor per ColorFilter) — currently a single model uses all 36 numeric features.
- Confidence intervals on predictions. `Regression.Trainers.LightGbm` returns a point estimate; quantile loss / `LightGbmRegressionTrainer` with custom options can produce intervals later.
- Cross-validation. The single-holdout split is sufficient for a first read on MAE.
- Drift detection (a different problem — "is this snapshot anomalous for its age?").
