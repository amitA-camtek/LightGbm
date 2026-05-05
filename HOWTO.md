# Wafer Defect Predictor — How To

A small WPF + LightGBM demo that trains a binary classifier on synthetic wafer process data and predicts whether a wafer will come out **Pass** or **Defective**.

Project layout:

```
Using Models/
├─ HOWTO.md                           ← you are here
├─ WaferDefectDemo.sln
└─ WaferDefectDemo/
   ├─ WaferDefectDemo.csproj
   ├─ App.xaml(.cs)
   ├─ MainWindow.xaml(.cs)
   ├─ Data/wafers.csv                 ← created on first run
   ├─ Models/   WaferData.cs, DefectPrediction.cs
   └─ Services/ DataGenerator.cs, WaferCsvStore.cs, DefectClassifier.cs
```

---

## 1. How to install the .NET 8 SDK

The project targets **.NET 8 (Windows)** and uses WPF, so you need the **.NET 8 SDK** (the runtime alone is not enough — you can't compile without the SDK).

### Check what you already have

Open PowerShell or a terminal and run:

```
dotnet --list-sdks
```

- **You see lines like `8.0.xxx [C:\Program Files\dotnet\sdk]`** → SDK is already installed, skip to step 2.
- **You see "No .NET SDKs were found"** → install it using one of the options below.

### Option A — winget (recommended, one command)

```
winget install Microsoft.DotNet.SDK.8
```

After it finishes, **close and reopen** your terminal so the new `PATH` is picked up, then verify:

```
dotnet --list-sdks
```

### Option B — interactive installer

1. Go to <https://dotnet.microsoft.com/download/dotnet/8.0>
2. Under **.NET 8.0 SDK**, download the **x64 Installer for Windows**.
3. Run it (≈ 250 MB, accepts defaults).
4. Open a new terminal and run `dotnet --list-sdks` to confirm.

### Option C — Visual Studio 2022

If you'd rather use the IDE, install **Visual Studio 2022** with the **".NET desktop development"** workload — that workload bundles the .NET 8 SDK automatically.

### Uninstall (if needed)

The SDK is reversible: *Settings → Apps → Installed apps → Microsoft .NET SDK 8.0 → Uninstall*.

---

## 2. How to build and run

From the folder that contains the `.sln` file:

```
cd "c:\Users\amita\Desktop\Claude\Using Models"
dotnet restore
dotnet build
dotnet run --project WaferDefectDemo
```

First build downloads NuGet packages (`Microsoft.ML`, `Microsoft.ML.LightGbm`) — give it a minute. After that, `dotnet run` opens the app window.

If you installed Visual Studio instead, double-click `WaferDefectDemo.sln` and press **F5**.

---

## 3. How to use the app

The window is split in two:

- **Left** — the dataset (one row = one wafer). Defective rows are tinted red.
- **Right** — input fields, dataset actions, and model actions.

### First launch

The app looks for `WaferDefectDemo\bin\Debug\net8.0-windows\Data\wafers.csv`. If it isn't there, it generates 200 synthetic wafers using a drift-based logistic model (seeded `Random(42)` so it's reproducible) and writes the CSV. The grid populates automatically.

### Process parameter inputs

Six fields, used by both **Add Row** and **Predict**:

| Field            | Unit     | Optimal | Drift away from optimal raises defect probability |
|------------------|----------|---------|---------------------------------------------------|
| Temperature      | °C       | 400     | ±50 °C                                            |
| Pressure         | mTorr    | 100     | ±50 mTorr                                         |
| EtchTime         | seconds  | 60      | ±30 s                                             |
| ParticleCount    | count    | < 15    | every extra particle hurts                        |
| FilmThickness    | nm       | 100     | ±10 nm                                            |
| Uniformity       | %        | > 98    | every percent below 98 hurts                      |

Plus a **Defective** checkbox — only used by **Add Row** (it's the label for the new training example, ignored by Predict).

### Dataset actions

- **Add Row** — appends the current input values + the Defective checkbox to the grid as a new training example. Adding rows invalidates the trained model, so the **Predict** button is disabled until you re-train.
- **Save Dataset** — writes the current grid back to `Data\wafers.csv`. Status bar shows the path.
- **Reset Dataset** — confirms, then regenerates the seeded 200 rows and overwrites the CSV. Use this when you've experimented and want to go back to a clean slate.

### Model actions

- **Train Model** — fits a LightGBM binary classifier on the current grid. Status line shows test-set metrics, e.g.
  `Trained · Accuracy 0.930 · AUC 0.972 · F1 0.910`. After training, the **Predict** button enables.
- **Predict** — runs the trained model on the six input fields. The result panel shows:
  - Green **PASS** with `P(defective)` low, **or**
  - Red **DEFECTIVE** with `P(defective)` high.

### Try this end-to-end

1. Click **Train Model** → metrics appear, Predict enables.
2. Predict a clean wafer:
   `Temperature=400, Pressure=100, EtchTime=60, ParticleCount=5, FilmThickness=100, Uniformity=99` → green **PASS**.
3. Predict a drifted wafer:
   `Temperature=445, Pressure=140, EtchTime=85, ParticleCount=60, FilmThickness=108, Uniformity=92` → red **DEFECTIVE**.
4. Add your own labelled row, **Save Dataset**, **Train Model** again — metrics will reflect the new data.

### Where the CSV lives

After running, you'll find it at:

```
WaferDefectDemo\bin\Debug\net8.0-windows\Data\wafers.csv
```

Open it in Excel or a text editor — it's a plain comma-separated file with the header `Temperature,Pressure,EtchTime,ParticleCount,FilmThickness,Uniformity,IsDefective`.

---

## Troubleshooting

| Symptom                                                  | Fix                                                                                            |
|----------------------------------------------------------|------------------------------------------------------------------------------------------------|
| `No .NET SDKs were found` on `dotnet build`              | You only have the runtime. Install the **SDK** using section 1.                                |
| `MSB4019: Microsoft.NET.Sdk.WindowsDesktop.props ...`    | You're on a non-Windows SDK. Use the Windows .NET 8 SDK build (section 1).                     |
| `Predict` button stays disabled                          | Click **Train Model** first — Predict only enables after a successful train.                   |
| Metrics look terrible (Accuracy < 0.7)                   | Did you Add a lot of mislabelled rows? Click **Reset Dataset** to restore the seeded sample.   |
| Numbers won't parse in input fields                      | Use `.` as the decimal separator (the app reads with InvariantCulture, not your Windows locale).|
