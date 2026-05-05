# Design — Illumination Lifetime Predictor

Technical design notes: components, data flows, and how the LightGBM regressor is trained on `IllumCalib_Diff*.ini` calibration snapshots to predict **remaining useful life (RUL)** of an inspection-tool illumination lamp.

The goal: given one calibration snapshot, output **how many days until the next lamp replacement**.

---

## 1. Solution layout

The code is split into two projects so the ML logic is reusable from any host (CLI today, WPF / service later):

```
WaferDefectDemo.sln
├── IlluminationLifetimeModel/                  ← class library, net8.0
│   ├── Models/
│   │   ├── IllumCalibSnapshot.cs               ← 37 features + RulDays label
│   │   └── RulPrediction.cs                    ← regressor output (Score)
│   ├── Services/
│   │   ├── IniSnapshotParser.cs                ← single .ini → snapshot POCO
│   │   ├── ReplacementLogReader.cs             ← csv → IList<LampReplacement>
│   │   ├── SnapshotFolderLoader.cs             ← walk + parse + label
│   │   └── LifetimeRegressor.cs                ← Train / Predict / Save / Load
│   └── IlluminationLifetimeModel.csproj        ← refs Microsoft.ML, Microsoft.ML.LightGbm
└── IlluminationLifetimeCli/                    ← console app, net8.0
    ├── Program.cs                              ← `build` / `train` / `predict` verbs
    └── IlluminationLifetimeCli.csproj          ← ProjectReference → IlluminationLifetimeModel
```

Only `IlluminationLifetimeModel` references ML.NET. The CLI only knows about the service classes — it never touches `MLContext` directly.

---

## 2. Block diagram

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              INPUT LAYER                                     │
│                                                                              │
│   {root}/                                replacements.csv                    │
│   ├── MachineA/                          ┌────────────────────┐              │
│   │   ├── IllumCalib_Diff.ini            │ MachineId,         │              │
│   │   ├── IllumCalib_Diff_01.ini         │ ReplacementDate    │              │
│   │   └── ...                            │ MachineA,2023-08-15│              │
│   ├── MachineB/                          │ MachineA,2024-01-22│              │
│   │   └── ...                            │ MachineB,2023-11-30│              │
│   └── ...                                └────────────────────┘              │
└──────┬─────────────────────────────────────────┬─────────────────────────────┘
       │ INI files                               │ CSV
       ▼                                         ▼
┌────────────────────────────────┐   ┌────────────────────────────────┐
│      IniSnapshotParser         │   │     ReplacementLogReader       │
│  (single .ini → snapshot POCO) │   │  (csv → IList<LampReplacement>)│
└──────────────┬─────────────────┘   └────────────────┬───────────────┘
               │                                      │
               ▼                                      ▼
            ┌───────────────────────────────────────────────┐
            │           SnapshotFolderLoader                │
            │  walk root → parse each ini → sort by         │
            │  (machine, RunDate) → fill                    │
            │  DaysSincePrevCalibration → join with         │
            │  replacement log → assign RulDays label       │
            │  → drop right-censored                        │
            │  returns DatasetBuildResult                   │
            │       (Labeled, RightCensored, FailedFiles)   │
            └────────────────────┬──────────────────────────┘
                                 │ IReadOnlyList<IllumCalibSnapshot>
                                 ▼
            ┌───────────────────────────────────────────────┐
            │             LifetimeRegressor                 │
            │  TimeAwareSplit (per machine, last 20%)       │
            │      │                                        │
            │      ▼                                        │
            │  ML.NET pipeline:                             │
            │   OneHotHashEncoding(MachineId, 10 bits)      │
            │   .Append(Concatenate("Features", …))         │
            │   .Append(Regression.Trainers.LightGbm)       │
            │      │                                        │
            │      ▼                                        │
            │  ITransformer ──► PredictionEngine            │
            │                   <Snapshot, RulPrediction>   │
            └────────┬────────────┬────────────────┬────────┘
                     │            │                │
                     ▼            ▼                ▼
                model.zip    RegressionMetrics   RulPrediction
              (Save/Load)    (MAE, RMSE, R²)   (PredictedRulDays = Score)
                                                        │
       ─────────────────────────────────────────────────┴──────────────────
                              IlluminationLifetimeCli
                              (build / train / predict verbs)
```

**Layers:**
- **Models** (`IlluminationLifetimeModel.Models`) — pure POCOs. `IllumCalibSnapshot` carries 37 input features + a `[ColumnName("Label")] float RulDays`. `RulPrediction` rebinds the trainer's `Score` column to `PredictedRulDays`.
- **Services (data side)** — `IniSnapshotParser`, `ReplacementLogReader`, `SnapshotFolderLoader`. Pure I/O + transformation; no ML.NET references.
- **Services (ML side)** — `LifetimeRegressor`. The only class that touches `MLContext`.
- **CLI** — thin entry point. Three verbs map directly to the data flows below.

---

## 3. Feature schema (37 features per snapshot)

| Group           | Per channel (×6 channels)              | Single-value                   | Total |
|-----------------|----------------------------------------|--------------------------------|-------|
| Polynomial fit  | `CfNA0`, `CfNA1`, `CfNA2`              | —                              | 18    |
| Achieved range  | `CfNMax`, `CfNMin`                     | —                              | 12    |
| Headroom        | `CfNMargin = CfNMax − Limit[N]`        | —                              | 6     |
| Cadence         | —                                      | `DaysSincePrevCalibration`     | 1     |
| Identity        | —                                      | `MachineId` (string, hashed)   | (used after one-hot-hash, not counted in 37 numeric inputs) |

`IllumCalibSnapshot.NumericFeatureNames` is the canonical list of 37 numeric input columns (cadence first, then channels 0…5 each contributing six floats). The trainer concatenates `["MachineIdEncoded", …NumericFeatureNames]` into the `Features` vector.

**Why each group exists**
- **A0/A1/A2** — voltage→intensity polynomial coefficients. They drift as the lamp ages; over months that drift is the dominant aging signal.
- **Max / Min light intensity** — the achieved gray-level range. A degrading lamp can no longer hit the same maximum at a fixed driver voltage.
- **Margin** — distance between the achieved max and the per-channel `SystemHighIntensityMaxLimit`. Approaching end of life this margin shrinks (or inverts).
- **DaysSincePrevCalibration** — recipe / cadence signal. Calibrations tend to get more frequent as a lamp ages.
- **MachineId** — captures machine-specific drift personalities. `OneHotHashEncoding(numberOfBits: 10)` projects to 1024 sparse buckets — collisions ≪ 5% for ~50 distinct machines, and trees handle sparse vectors natively.

**Excluded fields (deliberate)**
- `DumperFilterCoeff_*` — constant across both reference INIs in [raw-data/](raw-data/); zero-variance until real data shows otherwise.
- `[FilterTransparency]`, `[TargetLightResponse]`, `[Voltage]`, `[LampCalibrationValidation]`, `[IlluminationLevelTest]` — recipe-level constants in the sample data; not predictive at the snapshot level.

---

## 4. Build flow — `build <root> <log.csv> <out.csv>`

Dataset preparation only. Walks the INI tree, joins against the replacement log, drops right-censored rows, writes a flat CSV for inspection.

```
  CLI: build <root> <log.csv> <out.csv>
        │
        ▼
  ReplacementLogReader.Read(log.csv)
        │  ► IReadOnlyList<LampReplacement>(MachineId, ReplacementDate)
        ▼
  SnapshotFolderLoader.Build(root, replacements, log: Console.WriteLine)
        │
        │  group replacements by MachineId (case-insensitive), sort each ascending
        │
        │  for each subfolder (= machine):
        │    1. enumerate "IllumCalib_Diff*.ini" (top-level only)
        │    2. for each file:
        │         try IniSnapshotParser.Parse(file, machineId)
        │         on failure: failed++, log message, continue
        │    3. sort snapshots ascending by RunDate
        │    4. fill DaysSincePrevCalibration:
        │         snap[0]: 0
        │         snap[i]: (snap[i].RunDate - snap[i-1].RunDate).TotalDays
        │    5. for each snapshot:
        │         next = first replacement on same machine where date >= RunDate
        │         if next is null  → snap.RulDays = NaN; censored.Add(snap)
        │         else             → snap.RulDays = (next.Date - RunDate).Days; labeled.Add(snap)
        │
        │  return DatasetBuildResult(Labeled, RightCensored, FailedFiles)
        │
        ▼
  Console.WriteLine summary:
        "Labeled snapshots:        N"
        "Right-censored (dropped): M"
        "Failed parses:            K"
        ▼
  WriteSnapshotCsv(out.csv, result.Labeled)
        │  ► flat CSV: 4 metadata cols + 36 channel feature cols + DaysSincePrevCalibration + RulDays
        ▼
  Console.WriteLine "Wrote dataset -> {out.csv}"
```

The CSV is **for inspection only** — it is not the training input. Training reads the in-memory snapshot list directly via `MLContext.Data.LoadFromEnumerable`, so feature names match the property names exactly (no string-CSV round-trip).

---

## 5. Train flow — `train <root> <log.csv> <model.zip>`

```
  CLI: train <root> <log.csv> <model.zip>
        │
        ▼
  Build dataset (same as §4) ─► result.Labeled
        │
        │  guard: result.Labeled.Count < 10  → exit 1 "need at least 10 labeled snapshots"
        ▼
  LifetimeRegressor.Train(labeled)
        │
        │  guard inside Train: labeled.Count < 10 → throw
        │
        │  TimeAwareSplit(labeled, holdoutFraction = 0.2)
        │     per machine: sort by RunDate
        │                  splitIdx = floor(count * 0.8), clamped to [1, count-1]
        │                  rows[0..splitIdx)  → train
        │                  rows[splitIdx..]   → test
        │     fallback: if test empty but train >1, move last train row → test
        │
        │  ┌────────────────  pipeline (lazy, not yet fit)  ────────────────┐
        │  │                                                                │
        │  │  Categorical.OneHotHashEncoding(                               │
        │  │      outputColumnName: "MachineIdEncoded",                     │
        │  │      inputColumnName : nameof(IllumCalibSnapshot.MachineId),   │
        │  │      numberOfBits    : 10)                                     │
        │  │             │                                                  │
        │  │             ▼                                                  │
        │  │  Transforms.Concatenate("Features",                            │
        │  │      "MachineIdEncoded",                                       │
        │  │      DaysSincePrevCalibration,                                 │
        │  │      Cf0A0, Cf0A1, Cf0A2, Cf0Max, Cf0Min, Cf0Margin,           │
        │  │      …                                                         │
        │  │      Cf5A0, Cf5A1, Cf5A2, Cf5Max, Cf5Min, Cf5Margin)           │
        │  │             │                                                  │
        │  │             ▼                                                  │
        │  │  Regression.Trainers.LightGbm(                                 │
        │  │      labelColumnName  : "Label",                               │
        │  │      featureColumnName: "Features")                            │
        │  │                                                                │
        │  └────────────────────────────────────────────────────────────────┘
        │             │
        │             ▼
        │  _model  = pipeline.Fit(trainData)
        │  _schema = trainData.Schema
        │             │
        │             ▼
        │  preds   = _model.Transform(testData)
        │  metrics = _ml.Regression.Evaluate(preds, "Label")
        │  └► MeanAbsoluteError, RootMeanSquaredError, RSquared, …
        │
        │  _engine = _ml.Model.CreatePredictionEngine<IllumCalibSnapshot, RulPrediction>(_model)
        │
        ▼
  return RegressionMetrics
        │
  CLI prints:
        "MAE  (days):  3.41"
        "RMSE (days):  5.02"
        "R^2:          0.84"
        ▼
  regressor.Save(model.zip)
        │  ► MLContext.Model.Save(_model, _schema, path)
        ▼
  CLI prints "Saved model -> {model.zip}"
```

`MLContext` is constructed once with `seed: 1` so train/test split + LightGBM bagging are reproducible across runs. Default LightGBM hyper-parameters (≈100 trees, 31 leaves, lr ≈ 0.2, min-data-in-leaf ≈ 10) — first prove the pipeline with defaults, tune later.

---

## 6. Predict flow — `predict <model.zip> <single.ini> <machine-id>`

```
  CLI: predict <model.zip> <single.ini> <machine-id>
        │
        ▼
  regressor = new LifetimeRegressor()
  regressor.Load(model.zip)
        │  ► _model  = MLContext.Model.Load(path, out var schema)
        │    _schema = schema
        │    _engine = CreatePredictionEngine<IllumCalibSnapshot, RulPrediction>(_model)
        ▼
  IniSnapshotParser.Parse(single.ini, machine-id)
        │  ► IllumCalibSnapshot
        │       MachineId, SourceFile, RunDate           (from filename + per-channel RunDate)
        │       Cf0..Cf5 × {A0,A1,A2,Max,Min,Margin}     (from [ColorFilter_N] sections)
        │       DaysSincePrevCalibration = 0             (no prior snapshot in single-file mode)
        │       RulDays = NaN                            (irrelevant for prediction)
        ▼
  regressor.Predict(snap)
        │  ► engine.Predict(snap) → RulPrediction { PredictedRulDays = Score }
        ▼
  CLI prints:
        "Snapshot:           IllumCalib_Diff.ini  (MachineA, 2023-11-02 07:25)"
        "Predicted RUL:      28.7 days"
```

**Known limitation:** `DaysSincePrevCalibration` is 0 in single-file mode because the parser has no neighbour to diff against. For best accuracy in production predictions, the caller should locate the previous snapshot for the same machine, compute the gap externally, and assign it to `snap.DaysSincePrevCalibration` before calling `Predict`.

---

## 7. Save / Load model flow

`LifetimeRegressor` wraps `MLContext.Model.Save/Load`:

```
        regressor.Save(path)                    regressor.Load(path)
              │                                       │
              │  guard: _model && _schema != null     │
              ▼                                       ▼
   _ml.Model.Save(_model, _schema, path)     _model  = _ml.Model.Load(path, out var schema)
                                              _schema = schema
                                              _engine = CreatePredictionEngine(_model)
                                                      │
                                                      ▼
                                               IsTrained = true
```

Persisting the schema next to the model is what lets `Load` rebuild a `PredictionEngine` without the original training data.

---

## 8. Column-by-column ML.NET mapping

| Stage              | Source                                                | ML.NET column name   | Type                  |
|--------------------|-------------------------------------------------------|----------------------|-----------------------|
| Load (metadata)    | `IllumCalibSnapshot.MachineId`                        | `MachineId`          | `string`              |
| Load (metadata)    | `IllumCalibSnapshot.SourceFile` / `RunDate`           | `SourceFile`/`RunDate`| `string` / `DateTime` (carried, not used by trainer) |
| Load (numeric)     | `DaysSincePrevCalibration` + 36 channel floats        | (property names)     | `Single`              |
| Load (label)       | `IllumCalibSnapshot.RulDays` (`[ColumnName("Label")]`)| `Label`              | `Single`              |
| OneHotHashEncoding | `MachineId`                                           | `MachineIdEncoded`   | `Vector<Single>[1024]` |
| Concatenate        | `MachineIdEncoded` + 37 numeric                       | `Features`           | `Vector<Single>`      |
| LightGbm regressor | reads `Features` and `Label`                          | (configures itself)  | —                     |
| Predict (output)   | regressor produces                                    | `Score`              | `Single`              |
| Bind back          | `RulPrediction.PredictedRulDays` (`[ColumnName("Score")]`) | `Score`         | `Single`              |

`MachineId`, `SourceFile`, and `RunDate` ride along with each row, but only `MachineId` is consumed by the pipeline. `SourceFile` and `RunDate` are pure metadata for reporting / diagnostics.

### Why a `PredictionEngine`?

`ITransformer.Transform(IDataView)` is **batch** — efficient for thousands of rows. The CLI's `predict` verb is a single row, so we wrap the model in `PredictionEngine<IllumCalibSnapshot, RulPrediction>`:

```csharp
var p = engine.Predict(input);   // ~µs per call
```

It's not thread-safe — fine here because the CLI is single-threaded. If we ever called `Predict` concurrently we'd need `PredictionEnginePool` (from `Microsoft.Extensions.ML`).

---

## 9. INI parsing details

`IniSnapshotParser.Parse(iniPath, machineId)` is a small section-based reader — no third-party library. Behaviour worth knowing:

```
  read all lines:
    skip blank / starting with ';' or '#'
    "[Name]"        → switch current section (case-insensitive, dedup by name)
    "key = value"   → store in current section dict
                      (ignore lines without '=' or before any section header)
  return Dictionary<sectionName, Dictionary<key, value>>

  for channel c in 0..5:
    section = sections["ColorFilter_" + c]   (throws InvalidDataException if missing)
    A0 = section["BilinearVoltageTransform_A0"]
    A1 = section["BilinearVoltageTransform_A1"]
    A2 = section["BilinearVoltageTransform_A2"]
    Max = section["MaxLightIntensity"]
    Min = section["MinLightIntensity"]
    limit  = sections["SystemHighIntensityMaxLimit"]?["ColorFilter_" + c]   (NaN if absent)
    Margin = NaN(limit) ? 0 : Max - limit                                   (default 0 keeps the row trainable)

  RunDate:
    scan each [ColorFilter_c]'s "RunDate" key
    parse with InvariantCulture + AssumeLocal
    keep the latest                                                          (DateTime.MinValue if none parse)
```

All numeric parsing uses `CultureInfo.InvariantCulture` to avoid comma-vs-dot ambiguity on non-en-US machines.

---

## 10. Why these design choices

**Time-aware split, not random.** `MLContext.Data.TrainTestSplit` shuffles randomly, which is correct for IID classification (the Wafer demo) but **wrong for time-series RUL** — a randomly chosen test row could come earlier than its training neighbours, leaking future drift into past. `TimeAwareSplit` holds out the chronological tail per machine, so the test set always asks "given history up to day X, can we predict the future?" — the deployment scenario.

**Right-censoring → drop, not impute.** Snapshots taken after the most recent known replacement on a machine have no future replacement event yet, so RUL is unknowable. We drop them from training (label = NaN, returned in `DatasetBuildResult.RightCensored`). They can still be parsed and predicted on. A future improvement is proper Cox / survival regression on these — out of scope for v1.

**MachineId via hash encoding.** With hundreds of machines, one-hot is wasteful and label-encoded ordinals invent false orderings. `OneHotHashEncoding(numberOfBits: 10)` projects to 1024 sparse buckets — collision rate ≪ 5% for ~50 distinct machines.

**No normalization.** LightGBM is tree-based and scale-invariant. Same call shape as the existing `DefectClassifier`.

**Single seeded `MLContext`.** Reproducibility across runs (same split, same bagging) is more useful than wall-clock-seeded randomness during early development. Mirrors `DefectClassifier._ml = new(seed: 1)`.

**One snapshot per INI = one feature row.** No sliding-window or rolling-feature engineering yet. The time signal lives in `DaysSincePrevCalibration` and (implicitly) in the drift of A0/A1/A2 across sequential snapshots. If MAE turns out poor, a v2 with rolling-mean / first-derivative features is the natural next step.

**`Margin` defaults to 0 when the limits section is absent.** This keeps row count stable when an older INI lacks the `[SystemHighIntensityMaxLimit]` section. For trees, "0 across the board" becomes a low-information feature rather than dropping rows.

---

## 11. Verification — smoke test against the two sample INIs

The repo's [raw-data/](raw-data/) folder ships two real calibration files. They're enough for a parser smoke test (not training — see guard in §5).

```powershell
# 1. Stage a per-machine layout
New-Item -ItemType Directory -Force -Path C:\temp\illum-test\MachineA | Out-Null
Copy-Item "raw-data\IllumCalib_Diff.ini"    C:\temp\illum-test\MachineA\
Copy-Item "raw-data\IllumCalib_Diff_01.ini" C:\temp\illum-test\MachineA\
"MachineId,ReplacementDate`nMachineA,2023-12-01" |
    Set-Content C:\temp\illum-test\replacements.csv

# 2. Build the dataset
dotnet run --project IlluminationLifetimeCli -- `
    build C:\temp\illum-test C:\temp\illum-test\replacements.csv C:\temp\illum-test\dataset.csv

# Expected:
#   [MachineA] 2 snapshots (2 labeled, 0 right-censored)
#   Labeled snapshots:        2
#   Right-censored (dropped): 0
#   Failed parses:            0
#   Wrote dataset -> C:\temp\illum-test\dataset.csv
```

The produced `dataset.csv` should show:
- Two rows, sorted by `RunDate` (the `_01` 06:22 file first, then the unsuffixed 07:25 file).
- `DaysSincePrevCalibration` = 0 on row 1, ≈ 0.0434 on row 2 (≈62 minutes between calibrations).
- `RulDays` = `(2023-12-01 − RunDate).TotalDays` — about 29.7 on both rows.
- `Cf0Margin` = 79 on row 1, 67 on row 2 (matches the +12 delta in [raw-data/ANALYSIS.md](raw-data/ANALYSIS.md)).
- `Cf4Margin` = 42 on row 1, −32 on row 2 (achieved Max overshoots the limit on the newer snapshot).

Once you have ≥10 labeled snapshots from real data:
```powershell
dotnet run --project IlluminationLifetimeCli -- train   <root> <replacements.csv> <model.zip>
dotnet run --project IlluminationLifetimeCli -- predict <model.zip> <some-machine>\IllumCalib_Diff.ini <machine-id>
```

---

## 12. File-to-file map

| Concern                                      | File                                                              |
|----------------------------------------------|-------------------------------------------------------------------|
| Snapshot input schema (37 features + Label)  | `IlluminationLifetimeModel/Models/IllumCalibSnapshot.cs`          |
| Prediction output schema (Score)             | `IlluminationLifetimeModel/Models/RulPrediction.cs`               |
| INI section reader + per-channel extraction  | `IlluminationLifetimeModel/Services/IniSnapshotParser.cs`         |
| Replacement-log CSV reader                   | `IlluminationLifetimeModel/Services/ReplacementLogReader.cs`      |
| Folder walk + labelling + censoring          | `IlluminationLifetimeModel/Services/SnapshotFolderLoader.cs`      |
| MLContext, regressor pipeline, Save/Load     | `IlluminationLifetimeModel/Services/LifetimeRegressor.cs`         |
| ML.NET / LightGBM packages                   | `IlluminationLifetimeModel/IlluminationLifetimeModel.csproj`      |
| `build` / `train` / `predict` verbs          | `IlluminationLifetimeCli/Program.cs`                              |
| CLI build config + project ref               | `IlluminationLifetimeCli/IlluminationLifetimeCli.csproj`          |
| Sample INIs + analysis notes                 | `raw-data/IllumCalib_Diff*.ini`, `raw-data/ANALYSIS.md`           |

---

## 13. Things deliberately left out

- **WPF UI integration into [WaferDefectDemo](WaferDefectDemo/)** — the CLI is the v1 interface; UI is a separate task.
- **Hyper-parameter tuning** (`numberOfIterations`, `numberOfLeaves`, `learningRate`) — defaults first, tune once we have a real MAE baseline.
- **Survival-style modelling** (Cox regression / Kaplan-Meier) for right-censored snapshots.
- **Per-channel sub-models** (one regressor per ColorFilter) — currently a single model uses all 36 channel features at once.
- **Confidence intervals on predictions.** `Regression.Trainers.LightGbm` returns a point estimate; quantile loss / `LightGbmRegressionTrainer.Options` could produce intervals later.
- **Cross-validation.** The single time-aware holdout is sufficient for a first read on MAE; CV on time-series needs walk-forward, which is more code than v1 warrants.
- **Drift detection** ("is this snapshot anomalous for its age?") — different problem, would need an unsupervised model on top of the same features.
- **Parallel `Predict`.** The single `PredictionEngine` is not thread-safe; the CLI is single-threaded, so this is fine for now.
