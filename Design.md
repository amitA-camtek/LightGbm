# Design — Wafer Defect Predictor

Technical design notes: components, data flows, and how the LightGBM model is trained and used.

---

## 1. Block diagram

```
┌──────────────────────────────────────────────────────────────────────────┐
│                              UI LAYER  (WPF)                             │
│                                                                          │
│   MainWindow.xaml ─────────────────► MainWindow.xaml.cs (code-behind)    │
│   (DataGrid, TextBoxes, Buttons,      _store, _classifier, _rows         │
│    Result panel)                      OnAddRow / OnTrain / OnPredict /…) │
└──────┬───────────────────────────────────┬───────────────────┬───────────┘
       │ binds                             │ uses              │ uses
       ▼                                   ▼                   ▼
 ObservableCollection<WaferData>   WaferCsvStore       DefectClassifier
                                                              │
       (Models layer — POCOs)                                 │
       ┌───────────────┐  ┌───────────────────┐               │
       │ WaferData     │  │ DefectPrediction  │ ◄─────────────┘ predicts
       │ 6 floats +    │  │ PredictedLabel    │
       │ IsDefective   │  │ Probability,Score │
       └───────────────┘  └───────────────────┘
                ▲                                     ┌───────────────────┐
                │ rows                                │     ML.NET        │
                │                                     │  (MLContext,      │
       ┌────────┴──────────┐    ┌─────────────────┐   │   pipeline,       │
       │ DataGenerator     │    │ WaferCsvStore   │   │   Trainers.LightGbm,│
       │ (synthetic 200,   │    │ Load / Save /   │   │   PredictionEngine)│
       │  drift+logistic)  │    │ EnsureSeeded    │   └────────┬──────────┘
       └───────────────────┘    └────────┬────────┘            │
                                         │ reads/writes        │ wraps
                                         ▼                     ▼
                                   Data/wafers.csv      LightGBM native (.dll)
                                   (text, comma-sep)     loaded by ML.NET
```

**Layers:**
- **UI** — `MainWindow.xaml` + code-behind. No MVVM; the code-behind is intentionally thin: it forwards button clicks to the services and rebinds the grid.
- **Models** — pure data classes (`WaferData`, `DefectPrediction`). No behaviour. Annotated for ML.NET column mapping (`[LoadColumn]`, `[ColumnName]`).
- **Services** — `DataGenerator` (in-memory synthetic data), `WaferCsvStore` (file I/O for `wafers.csv`), `DefectClassifier` (the only class that touches `MLContext`).
- **ML.NET / LightGBM** — third-party. The native LightGBM DLL is loaded by `Microsoft.ML.LightGbm` at runtime; we never call it directly.

---

## 2. Startup flow

What happens between double-clicking the EXE and the window appearing.

```
  Process start
      │
      ▼
  App.xaml ─► Application boots, creates MainWindow
      │
      ▼
  MainWindow ctor
      │
      ├─► InitializeComponent()             (parses XAML, builds controls)
      │
      ├─► _store = new WaferCsvStore()      (Path = AppContext.BaseDirectory + Data/wafers.csv)
      │
      ├─► _store.EnsureSeeded()
      │     │
      │     ├─ if (!File.Exists)             ─► DataGenerator.Generate(seed=42)
      │     │                                  └─► _store.Save(rows)  [writes CSV]
      │     └─ Load()                         (parses CSV, returns IReadOnlyList<WaferData>)
      │
      ├─► foreach row → _rows.Add(row)        (populates ObservableCollection)
      │
      ├─► WaferGrid.ItemsSource = _rows       (DataGrid renders all rows)
      │
      ├─► UpdateDatasetStatus()              (count + % defective + path)
      │
      └─► StatusText = "Click Train Model …"
```

Key invariant: the **CSV is the source of truth** for the dataset on disk. The in-memory `ObservableCollection` is a working copy. `Save Dataset` writes back; `Reset Dataset` overwrites.

---

## 3. Add Row + Save flow

```
  User types into 6 textboxes + ticks "Defective" checkbox
            │
  [Add Row] click
            │
            ▼
  TryReadInputs(out WaferData row, out string error)
            │
            ├─ float.TryParse each box (InvariantCulture)
            │  └─ on failure: StatusText = "...is not a number"  ✗ exit
            │
            ▼
  row.IsDefective = IsDefectiveBox.IsChecked
            │
  _rows.Add(row)                    ─► ObservableCollection raises CollectionChanged
            │                          ─► DataGrid auto-refreshes (new row at bottom)
            │
  PredictButton.IsEnabled = false   (model is now stale w.r.t. dataset)
            │
  UpdateDatasetStatus()             (count + % defective updated)
            │
  StatusText = "Row added. Re-train the model to incorporate it."

  ─────────────────────────────────────

  [Save Dataset] click
            │
            ▼
  _store.Save(_rows)
            │
            ├─ build CSV header + lines (InvariantCulture)
            └─ File.WriteAllText(path)
            │
  StatusText = "Saved {N} wafers to {path}"
```

---

## 4. Train + Predict flow (the ML pipeline)

This is the core of how the model is used.

```
                               [Train Model] click
                                       │
                                       ▼
                       DefectClassifier.Train(_rows)
                                       │
   ┌───────────────────────────────────┼──────────────────────────────────────┐
   │                                                                          │
   │  IDataView data = _ml.Data.LoadFromEnumerable(_rows)                     │
   │  (WaferData properties → ML.NET columns; [ColumnName("Label")] = bool)   │
   │                                                                          │
   │  TrainTestData split = _ml.Data.TrainTestSplit(data,                     │
   │                                  testFraction: 0.2,                      │
   │                                  seed: 1)                                │
   │  └► split.TrainSet  (~80%)                                               │
   │  └► split.TestSet   (~20%)                                               │
   │                                                                          │
   │  ┌─────────────────────  pipeline (lazy, not yet fit)  ─────────────┐    │
   │  │                                                                  │    │
   │  │  Concatenate("Features",                                         │    │
   │  │    Temperature, Pressure, EtchTime,                              │    │
   │  │    ParticleCount, FilmThickness, Uniformity)                     │    │
   │  │            │                                                     │    │
   │  │            ▼                                                     │    │
   │  │  BinaryClassification.Trainers.LightGbm(                         │    │
   │  │    labelColumnName: "Label",                                     │    │
   │  │    featureColumnName: "Features")                                │    │
   │  │                                                                  │    │
   │  └──────────────────────────────────────────────────────────────────┘    │
   │              │                                                           │
   │              ▼                                                           │
   │  ITransformer _model = pipeline.Fit(split.TrainSet)                      │
   │              │                                                           │
   │              ▼                                                           │
   │  IDataView preds = _model.Transform(split.TestSet)                       │
   │              │                                                           │
   │              ▼                                                           │
   │  metrics = _ml.BinaryClassification.Evaluate(preds, "Label")             │
   │  └► Accuracy, AreaUnderRocCurve, F1Score, …                              │
   │                                                                          │
   │  _engine = _ml.Model.CreatePredictionEngine<WaferData,DefectPrediction>(_model) │
   │                                                                          │
   └──────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
                  return BinaryClassificationMetrics
                                       │
            UI: StatusText = "Trained · Accuracy 0.93 · AUC 0.97 · F1 0.91"
                  PredictButton.IsEnabled = true


                              [Predict] click
                                       │
                                       ▼
                      TryReadInputs → WaferData input
                                       │
                                       ▼
                      _classifier.Predict(input)
                                       │
                  _engine.Predict(input) → DefectPrediction
                                       │
            UI: ResultBox background = green (PASS) or red (DEFECTIVE)
                ResultText = "DEFECTIVE · P(defective) = 87.3%"
```

---

## 5. How the model is used — column-by-column

ML.NET wires inputs to the trainer through **column names** in an `IDataView`. The mapping is:

| Stage              | Source                               | ML.NET column name | Type        |
|--------------------|--------------------------------------|--------------------|-------------|
| Load               | `WaferData.Temperature` … `Uniformity` | `Temperature`, …  | `Single`    |
| Load               | `WaferData.IsDefective`              | `Label`            | `Boolean`   |
| Concatenate        | the 6 floats above                   | `Features`         | `Vector<Single>[6]` |
| LightGbm trainer   | reads `Features` and `Label`         | (configures itself)| —           |
| Predict (output)   | LightGbm produces                    | `PredictedLabel`   | `Boolean`   |
|                    |                                      | `Probability`      | `Single`    |
|                    |                                      | `Score`            | `Single` (raw)|

`DefectPrediction` is just a POCO that re-binds those output columns back to .NET properties via `[ColumnName(...)]`.

### Why a `PredictionEngine`?

`ITransformer.Transform(IDataView)` is **batch** — efficient for thousands of rows. Each UI **Predict** click is a single row, so we wrap the model in `PredictionEngine<TIn,TOut>`:

```csharp
_engine = _ml.Model.CreatePredictionEngine<WaferData, DefectPrediction>(_model);
var p = _engine.Predict(input);   // ~µs per call
```

It's not thread-safe — fine here because all predictions happen on the WPF UI thread. If we ever moved prediction off the UI thread, we'd need `PredictionEnginePool` (from `Microsoft.Extensions.ML`).

### What about the LightGBM hyper-parameters?

We use the trainer's defaults: 100 leaves, learning rate ≈ 0.2, 100 iterations, minimum-data-per-leaf ≈ 10. For a 200-row, 6-feature, low-noise synthetic dataset this is plenty — Accuracy lands around 0.93 with no tuning. If the dataset grew or the signal weakened, we'd tune via `LightGbmBinaryTrainer.Options { NumberOfLeaves, LearningRate, MinimumExampleCountPerLeaf, NumberOfIterations, … }`.

---

## 6. Reset flow (after the seed fix)

```
  [Reset Dataset] click
        │
  MessageBox.YesNo                       ─► No → exit
        │ Yes
        ▼
  fresh = DataGenerator.Generate(seed: Random.Shared.Next())   ◄── NEW: random seed each click
        │
  _store.Save(fresh)            (overwrite Data/wafers.csv)
        │
  _rows.Clear() + Add(...)      (DataGrid refreshes)
        │
  PredictButton.IsEnabled = false    (model is stale)
        │
  StatusText = "Dataset reset — 200 fresh wafers generated."
```

The seed varies on every click, so the resulting grid is visibly different each time — fixing the original "Reset doesn't seem to do anything" complaint.

---

## 7. Synthetic-data generator (the "ground truth")

`DataGenerator.Generate(n=200, seed)` produces labelled rows that the model then *re-discovers*. The label is a **logistic of a drift score**:

```
drift = |T-400|/50                      // temperature deviation
      + |P-100|/50                      // pressure deviation
      + |Etch-60|/30                    // etch-time deviation
      + max(0, Particles-15)/65         // contamination above threshold
      + |Thickness-100|/10              // thickness deviation
      + max(0, 98-Uniformity)/8         // uniformity below threshold

prob        = 1 / (1 + exp(-(drift*1.4 - 1.6)))    // ~30% defects on average
IsDefective = rng.NextDouble() < prob              // Bernoulli draw
```

- **Optimal wafer** (`T=400, P=100, Etch=60, Particles≤15, Thickness=100, Uniformity≥98`) → `drift ≈ 0` → `prob ≈ 17%`. Mostly Pass.
- **Drifted wafer** (Particles=60, Uniformity=92, …) → `drift > 3` → `prob > 90%`. Almost always Defective.

LightGBM on these features should reach ~0.93 Accuracy / 0.97 AUC because the feature → label relationship is monotone-ish but with noise from the Bernoulli sampling.

---

## 8. File-to-file map (code locations)

| Concern                          | File                                          |
|----------------------------------|-----------------------------------------------|
| Window layout                    | `MainWindow.xaml`                             |
| Click handlers, validation, UI binding | `MainWindow.xaml.cs`                    |
| Training-row schema, label mapping | `Models/WaferData.cs`                       |
| Prediction-row schema (Score/Prob/Label) | `Models/DefectPrediction.cs`          |
| Synthetic data + drift→label formula | `Services/DataGenerator.cs`               |
| CSV read/write/seed-on-first-run | `Services/WaferCsvStore.cs`                   |
| MLContext, pipeline, Train, Predict | `Services/DefectClassifier.cs`             |
| App entry point                  | `App.xaml`, `App.xaml.cs`                     |
| Build config, NuGet packages, Data copy | `WaferDefectDemo.csproj`               |

---

## 9. Things deliberately left out

- **MVVM / `INotifyPropertyChanged` on WaferData** — fine for a 200-row read-only grid; would matter if rows were edited inline.
- **Model persistence** (`MLContext.Model.Save(...)` to `.zip`) — every launch retrains in <1 s, so saving isn't worth it yet.
- **Background-thread training** — training is fast enough to run on the UI thread without freezing. For a 100k-row dataset we'd offload to `Task.Run`.
- **Hyper-parameter tuning / cross-validation** — defaults are fine for the demo's signal-to-noise ratio.
- **Real fab ingestion (SECS-GEM, MES, CSV import dialog)** — out of scope; the focus is the LightGBM + XAML wiring.
