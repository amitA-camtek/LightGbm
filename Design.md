# Design — Wafer Defect Predictor

Technical design notes: components, data flows, and how the LightGBM classifier and SSA forecaster are trained and used.

---

## 1. Solution layout

The code is split into two projects so the ML logic is reusable from any host (WPF, CLI, tests, services):

```
WaferDefectDemo.sln
├── WaferDefectModel/                  ← class library, net8.0
│   ├── Models/
│   │   ├── WaferData.cs               ← training row schema (+ Timestamp)
│   │   └── DefectPrediction.cs        ← trainer output schema
│   ├── Services/
│   │   ├── DefectClassifier.cs        ← LightGBM Train/Predict/Save/Load
│   │   └── DefectRateForecaster.cs    ← SSA daily-defect-rate forecast
│   └── WaferDefectModel.csproj        ← refs Microsoft.ML, Microsoft.ML.LightGbm,
│                                         Microsoft.ML.TimeSeries
└── WaferDefectDemo/                   ← WPF app, net8.0-windows
    ├── App.xaml(.cs)
    ├── MainWindow.xaml(.cs)
    ├── Services/
    │   ├── DataGenerator.cs           ← synthetic rows (drift + logistic)
    │   └── WaferCsvStore.cs           ← Data/wafers.csv I/O + seed-on-launch
    ├── Data/                          ← copied to bin on build
    └── WaferDefectDemo.csproj         ← ProjectReference → WaferDefectModel
```

Only `WaferDefectModel` references ML.NET. The WPF project knows nothing about MLContext — it consumes `DefectClassifier` / `DefectRateForecaster` as black boxes.

---

## 2. Block diagram

```
┌──────────────────────────────────────────────────────────────────────────┐
│                    UI LAYER  (WaferDefectDemo, WPF)                      │
│                                                                          │
│   MainWindow.xaml ─────────► MainWindow.xaml.cs                          │
│   (DataGrid, TextBoxes,         _store, _classifier, _forecaster, _rows  │
│    Buttons, Result panel,       OnAddRow / OnTrain / OnPredict /         │
│    Forecast text panel)         OnSaveModel / OnLoadModel / OnForecast)  │
└──┬────────────────────┬─────────────────────────┬─────────────┬──────────┘
   │ binds              │ uses                    │ uses        │ uses
   ▼                    ▼                         ▼             ▼
ObservableCollection  WaferCsvStore        DefectClassifier   DefectRateForecaster
<WaferData>           DataGenerator        (LightGBM)         (SSA time-series)
                      ─────────────        ─────────────────────────────────
                      WaferDefectDemo      WaferDefectModel  (class library)
                      .Services            .Services
                                                  │
                                                  ▼
       (Models layer — POCOs in WaferDefectModel.Models)
       ┌────────────────┐  ┌────────────────────┐
       │ WaferData      │  │ DefectPrediction   │
       │ Timestamp +    │  │ PredictedLabel     │
       │ 6 floats +     │  │ Probability, Score │
       │ IsDefective    │  └────────────────────┘
       └────────────────┘
                                                  │ wraps
                                                  ▼
                                          ┌──────────────────────────────┐
                                          │           ML.NET             │
                                          │  MLContext, pipelines,       │
                                          │  Trainers.LightGbm,          │
                                          │  Forecasting.ForecastBySsa,  │
                                          │  PredictionEngine,           │
                                          │  TimeSeriesEngine            │
                                          └──────────────┬───────────────┘
                                                         │
                                                         ▼
                                               LightGBM native (.dll)
                                              loaded by ML.NET runtime
```

**Layers:**
- **UI** — `MainWindow.xaml` + code-behind. No MVVM; the code-behind is intentionally thin and forwards button clicks to the services.
- **Models** — pure POCOs in `WaferDefectModel.Models`. Annotated for ML.NET column mapping (`[LoadColumn]`, `[ColumnName]`).
- **Services (UI-side)** — `DataGenerator` (synthetic rows), `WaferCsvStore` (file I/O for `wafers.csv`).
- **Services (ML-side)** — `DefectClassifier` (the only class that touches `MLContext` for classification), `DefectRateForecaster` (its own MLContext for the time-series pipeline).
- **ML.NET / LightGBM** — third-party. The native LightGBM DLL is loaded by `Microsoft.ML.LightGbm` at runtime; we never call it directly. SSA forecasting comes from `Microsoft.ML.TimeSeries`.

---

## 3. Startup flow

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
      ├─► _store       = new WaferCsvStore()         (Path = AppContext.BaseDirectory + Data/wafers.csv)
      ├─► _classifier  = new DefectClassifier()
      ├─► _forecaster  = new DefectRateForecaster()
      │
      ├─► _store.EnsureSeeded()
      │     │
      │     ├─ if (Exists)  ─► check header matches current schema
      │     │                  └─ if header drifted  ─► overwrite with fresh DataGenerator.Generate()
      │     ├─ else          ─► DataGenerator.Generate(seed=42)
      │     │                  └─► _store.Save(rows)  [writes CSV]
      │     └─ Load()        (parses CSV → IReadOnlyList<WaferData>)
      │
      ├─► foreach row → _rows.Add(row)        (populates ObservableCollection)
      ├─► WaferGrid.ItemsSource = _rows       (DataGrid renders rows)
      ├─► UpdateDatasetStatus()              (count + % defective + path)
      └─► StatusText = "Click Train Model …"
```

Key invariant: the **CSV is the source of truth** for the dataset on disk. The in-memory `ObservableCollection` is a working copy. `Save Dataset` writes back; `Reset Dataset` overwrites.

`EnsureSeeded` does a header check on existing files so that older CSVs (e.g. without `Timestamp`) are auto-replaced rather than parsed and crashing.

---

## 4. Add Row + Save flow

```
  User types into 6 textboxes + ticks "Defective" checkbox
            │
  [Add Row] click
            │
            ▼
  TryReadInputs(out WaferData row, out string error)
            │
            ├─ float.TryParse each box (InvariantCulture)
            │  └─ on failure: StatusText = "...is not a number"   ✗ exit
            │
            ▼
  row.IsDefective = IsDefectiveBox.IsChecked
  row.Timestamp   = DateTime.Now                  ◄── stamps the row at insertion time
            │
  _rows.Add(row)                    ─► ObservableCollection raises CollectionChanged
            │                          ─► DataGrid auto-refreshes
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
            ├─ build CSV header + lines (InvariantCulture, ISO-8601 "o" timestamps)
            └─ File.WriteAllText(path)
            │
  StatusText = "Saved {N} wafers to {path}"
```

The CSV header is now:
```
Timestamp,Temperature,Pressure,EtchTime,ParticleCount,FilmThickness,Uniformity,IsDefective
```

---

## 5. Train + Predict flow (LightGBM classification)

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
   │  │  Concatenate("Features",                                         │    │
   │  │    Temperature, Pressure, EtchTime,                              │    │
   │  │    ParticleCount, FilmThickness, Uniformity)                     │    │
   │  │            │                                                     │    │
   │  │            ▼                                                     │    │
   │  │  BinaryClassification.Trainers.LightGbm(                         │    │
   │  │    labelColumnName: "Label",                                     │    │
   │  │    featureColumnName: "Features")                                │    │
   │  └──────────────────────────────────────────────────────────────────┘    │
   │              │                                                           │
   │              ▼                                                           │
   │  ITransformer _model = pipeline.Fit(split.TrainSet)                      │
   │  _schema = data.Schema                                                   │
   │              │                                                           │
   │              ▼                                                           │
   │  IDataView preds = _model.Transform(split.TestSet)                       │
   │              │                                                           │
   │              ▼                                                           │
   │  metrics = _ml.BinaryClassification.Evaluate(preds, "Label")             │
   │  └► Accuracy, AreaUnderRocCurve, F1Score, …                              │
   │                                                                          │
   │  _engine = _ml.Model.CreatePredictionEngine<WaferData,DefectPrediction>(_model)
   │                                                                          │
   └──────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
                  return BinaryClassificationMetrics
                                       │
            UI: StatusText = "Trained · Accuracy 0.93 · AUC 0.97 · F1 0.91"
                  PredictButton.IsEnabled = true
                  IsTrained = true   (DefectClassifier exposes a bool guard)


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

Train requires at least ~20 rows; the UI checks `_rows.Count` and refuses with a status message otherwise.

---

## 6. Save / Load model flow

`DefectClassifier` wraps `MLContext.Model.Save/Load` so the trained pipeline can be persisted to a single `.zip` and replayed without re-training:

```
  [Save Model] click                            [Load Model] click
        │                                              │
        │  guard: IsTrained?                           │
        ▼                                              ▼
  SaveFileDialog (*.zip)                        OpenFileDialog (*.zip)
        │                                              │
        ▼                                              ▼
  _classifier.Save(path)                        _classifier.Load(path)
        │                                              │
        ├─ _ml.Model.Save(_model, _schema, path)       ├─ _model = _ml.Model.Load(path, out var schema)
        │                                              ├─ _schema = schema
        │                                              ├─ _engine = CreatePredictionEngine(...)
        ▼                                              ▼
  StatusText = "Model saved to {path}"          PredictButton.IsEnabled = true
                                                StatusText = "Model loaded from {path}…"
```

Saving the schema alongside the model is what lets `Load` rebuild a `PredictionEngine` without seeing any training data.

---

## 7. How the classifier model is used — column-by-column

ML.NET wires inputs to the trainer through **column names** in an `IDataView`. The mapping is:

| Stage              | Source                                | ML.NET column name | Type                |
|--------------------|---------------------------------------|--------------------|---------------------|
| Load               | `WaferData.Timestamp`                 | `Timestamp`        | `DateTime` (unused by classifier; used by forecaster) |
| Load               | `WaferData.Temperature` … `Uniformity`| `Temperature`, …   | `Single`            |
| Load               | `WaferData.IsDefective`               | `Label`            | `Boolean`           |
| Concatenate        | the 6 floats above                    | `Features`         | `Vector<Single>[6]` |
| LightGbm trainer   | reads `Features` and `Label`          | (configures itself)| —                   |
| Predict (output)   | LightGbm produces                     | `PredictedLabel`   | `Boolean`           |
|                    |                                       | `Probability`      | `Single`            |
|                    |                                       | `Score`            | `Single` (raw)      |

`DefectPrediction` is just a POCO that re-binds those output columns back to .NET properties via `[ColumnName(...)]`.

`Timestamp` rides along with each row but is **not** in the `Concatenate` step, so the classifier ignores it. It's there for the forecaster (Section 9) and for the operator's situational awareness in the grid.

### Why a `PredictionEngine`?

`ITransformer.Transform(IDataView)` is **batch** — efficient for thousands of rows. Each UI **Predict** click is a single row, so we wrap the model in `PredictionEngine<TIn,TOut>`:

```csharp
_engine = _ml.Model.CreatePredictionEngine<WaferData, DefectPrediction>(_model);
var p = _engine.Predict(input);   // ~µs per call
```

It's not thread-safe — fine here because all predictions happen on the WPF UI thread. If we ever moved prediction off the UI thread, we'd need `PredictionEnginePool` (from `Microsoft.Extensions.ML`).

### LightGBM hyper-parameters

We use the trainer's defaults: ~100 leaves, learning rate ≈ 0.2, 100 iterations, minimum-data-per-leaf ≈ 10. For a 1000-row, 6-feature, low-noise synthetic dataset this is plenty — Accuracy lands around 0.93. To tune, swap the trainer call for `LightGbmBinaryTrainer.Options { NumberOfLeaves, LearningRate, MinimumExampleCountPerLeaf, NumberOfIterations, … }`.

---

## 8. Reset flow

```
  [Reset Dataset] click
        │
  MessageBox.YesNo                       ─► No → exit
        │ Yes
        ▼
  fresh = DataGenerator.Generate(seed: Random.Shared.Next())   ◄── random seed each click
        │  (defaults: n=1000 rows, days=180)
        │
  _store.Save(fresh)            (overwrite Data/wafers.csv)
        │
  _rows.Clear() + Add(...)      (DataGrid refreshes)
        │
  PredictButton.IsEnabled = false    (model is stale)
        │
  StatusText = "Dataset reset — {N} fresh wafers generated."
```

The seed varies on every click, so the resulting grid is visibly different each time.

---

## 9. Forecast flow (SSA time-series)

The forecaster predicts **daily defect rate** (a number in [0,1]) for the next 14 days, with a 95% confidence interval, using Singular Spectrum Analysis (SSA) from `Microsoft.ML.TimeSeries`.

```
                              [Forecast] click
                                       │
                                       ▼
                _forecaster.Forecast(_rows, daysAhead: 14)
                                       │
   ┌───────────────────────────────────┼──────────────────────────────────────┐
   │                                                                          │
   │  // 1. Aggregate rows → one DefectRate per calendar day                  │
   │  daily = rows.GroupBy(r => r.Timestamp.Date)                             │
   │             .Select(g => new DailyRate {                                 │
   │                  Date = g.Key,                                           │
   │                  DefectRate = g.Count(r => r.IsDefective) / g.Count()    │
   │             })                                                           │
   │             .OrderBy(d => d.Date)                                        │
   │                                                                          │
   │  guard: daily.Count ≥ 30  (else throw — needs enough history)            │
   │                                                                          │
   │  // 2. Pick window/series sizes from the data length                     │
   │  windowSize   = max(7, daily.Count / 12)                                 │
   │  seriesLength = min(daily.Count, max(windowSize*4, 60))                  │
   │                                                                          │
   │  // 3. Build & fit SSA pipeline                                          │
   │  pipeline = ml.Forecasting.ForecastBySsa(                                │
   │     outputColumnName: "Forecast",                                        │
   │     inputColumnName : "DefectRate",                                      │
   │     windowSize, seriesLength,                                            │
   │     trainSize = daily.Count, horizon = 14,                               │
   │     confidenceLevel = 0.95f,                                             │
   │     confidenceLowerBoundColumn: "LowerBound",                            │
   │     confidenceUpperBoundColumn: "UpperBound")                            │
   │                                                                          │
   │  model  = pipeline.Fit(data)                                             │
   │  engine = model.CreateTimeSeriesEngine<DailyRate, RateForecast>(ml)      │
   │  forecast = engine.Predict()                                             │
   │                                                                          │
   │  // 4. Project onto future calendar days, clamp to [0,1]                 │
   │  for i in 0..13:                                                         │
   │      ForecastPoint(lastDate + (i+1) days,                                │
   │                    clamp(forecast.Forecast[i],   0, 1),                  │
   │                    clamp(forecast.LowerBound[i], 0, 1),                  │
   │                    clamp(forecast.UpperBound[i], 0, 1))                  │
   │                                                                          │
   └──────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
                IReadOnlyList<ForecastPoint> rendered into ForecastText:
                  "Date         Defect%   95% CI
                   2026-05-06    32.4%   [25.1% – 39.7%]
                   2026-05-07    33.0%   [25.0% – 41.0%]
                   …"
```

Why SSA: defect rate is a **noisy, non-stationary** univariate series. SSA decomposes it into trend + oscillatory components and is robust enough to extrapolate without us hand-tuning ARIMA orders. The window heuristic (`max(7, n/12)`) keeps SSA from over-fitting short series while still capturing weekly-scale patterns.

The forecaster owns its own `MLContext` because the time-series engine is stateful and the lifetimes don't need to align with the classifier's.

---

## 10. Synthetic-data generator (the "ground truth")

`DataGenerator.Generate(n=1000, seed=42, days=180)` produces labelled rows that the model then *re-discovers*. Two things are happening simultaneously:

**Per-row drift score → Bernoulli label** (same logistic as before):
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

**Slow drift over wall-clock time** (so the *daily* defect rate also has a trend the forecaster can pick up):
- Rows are spread evenly across `days` days, ending at `DateTime.Today`.
- Temperature creeps up by up to **+8 °C** linearly over the window.
- Particle count creeps up by up to **+20** linearly over the window.
- These drifts feed into `drift` above, so daily defect-rate rises gradually — simulating fab-tool degradation between maintenance cycles.

So:
- **Optimal wafer at day 0** (`T=400, P=100, Etch=60, Particles≤15, Thickness=100, Uniformity≥98`) → `drift ≈ 0` → `prob ≈ 17%`. Mostly Pass.
- **Drifted wafer late in the window** (Particles≈60–80, Temperature pushed by `driftT`) → `drift > 3` → `prob > 90%`. Almost always Defective.

LightGBM on these features should reach ~0.93 Accuracy / 0.97 AUC. SSA on the per-day rate should produce a visibly upward-sloping forecast.

---

## 11. File-to-file map

| Concern                                  | File                                                  |
|------------------------------------------|-------------------------------------------------------|
| Window layout                            | `WaferDefectDemo/MainWindow.xaml`                     |
| Click handlers, validation, UI binding   | `WaferDefectDemo/MainWindow.xaml.cs`                  |
| Synthetic data + drift→label formula     | `WaferDefectDemo/Services/DataGenerator.cs`           |
| CSV read/write/seed-on-first-run         | `WaferDefectDemo/Services/WaferCsvStore.cs`           |
| App entry point                          | `WaferDefectDemo/App.xaml(.cs)`                       |
| WPF build config + Data copy             | `WaferDefectDemo/WaferDefectDemo.csproj`              |
| Training-row schema + label mapping      | `WaferDefectModel/Models/WaferData.cs`                |
| Prediction-row schema (Score/Prob/Label) | `WaferDefectModel/Models/DefectPrediction.cs`         |
| MLContext, classifier pipeline, Save/Load| `WaferDefectModel/Services/DefectClassifier.cs`       |
| MLContext, SSA forecasting pipeline      | `WaferDefectModel/Services/DefectRateForecaster.cs`   |
| ML.NET / LightGBM / TimeSeries packages  | `WaferDefectModel/WaferDefectModel.csproj`            |

---

## 12. Things deliberately left out

- **MVVM / `INotifyPropertyChanged` on WaferData** — fine for a read-only grid; would matter if rows were edited inline.
- **Background-thread training** — training is fast enough on the UI thread without freezing. For >>10k rows we'd offload to `Task.Run` and use `PredictionEnginePool`.
- **Hyper-parameter tuning / cross-validation** — defaults are fine for the demo's signal-to-noise ratio.
- **Real fab ingestion (SECS-GEM, MES, CSV import dialog)** — out of scope; the focus is the LightGBM + SSA + XAML wiring.
- **Forecast charting** — the forecast is rendered as plain text. A `LiveCharts` / `OxyPlot` line chart with confidence band would be the obvious next step.
- **Forecast feedback loop** — the forecaster is read-only; we don't yet alert operators when projected defect rate crosses a threshold.
