# LightGBM — Notes

Reference notes on what LightGBM is, how it works, and what hardware it needs.

---

## 1. What it is

**LightGBM** ("Light Gradient Boosting Machine") is a gradient-boosted decision tree (GBDT) framework released by Microsoft Research in 2017. Same family as XGBoost and `sklearn.GradientBoostingClassifier`, but with engineering tricks that make it much faster and lighter on memory.

In ML.NET it's exposed via `Microsoft.ML.LightGbm`, which wraps the native `lib_lightgbm.dll` shipped in the NuGet package.

---

## 2. The three "light" tricks

### Histogram-based split finding
Continuous features are bucketed into ~256 bins up front. Splits are evaluated on the histograms, not on raw sorted values. Turns split-finding from `O(n_features × n_rows × log n)` into `O(n_features × n_bins)`.

### GOSS — Gradient-based One-Side Sampling
During training, rows with **large gradients** (the ones the current ensemble is getting wrong) are kept in full; rows with small gradients are randomly subsampled. Most of the accuracy of the full data, a fraction of the compute.

### EFB — Exclusive Feature Bundling
Sparse features that are rarely non-zero at the same time get bundled into a single "feature." For wide one-hot-encoded data this collapses thousands of columns into hundreds.

---

## 3. Leaf-wise growth (the visible difference vs XGBoost)

```
       Level-wise (classic GBM, default XGBoost)        Leaf-wise (LightGBM)
              .                                                   .
            /   \                                              /     \
           .     .                                            .       .
          / \   / \                                          / \
         .   . .   .                                        .   .
                                                           / \
                                                          .   .
```

LightGBM picks the leaf with the **biggest loss reduction** and grows there, regardless of depth. Faster convergence, but it can also overfit faster on small datasets unless you cap `NumberOfLeaves` or `MinimumExampleCountPerLeaf`.

---

## 4. How the model works — the 30-second version

LightGBM is **gradient boosting on decision trees**. The training loop:

```
ensemble = [ ]                                 # starts empty (predicts 0)

for round in 1..NumberOfIterations (default 100):
    residuals = current_predictions - true_labels   # gradients of log-loss
    new_tree  = best_tree_that_predicts(residuals)  # trained to fit the errors
    ensemble.append(new_tree * LearningRate)        # shrunk contribution

predict(x) = sum(tree(x) for tree in ensemble)      # raw score
probability = 1 / (1 + exp(-score))                 # logistic squash
label = probability > 0.5
```

Two key choices in each round:
- **Which leaf to grow next** — pick the leaf with the biggest loss reduction (leaf-wise), not the deepest level.
- **Where to split a leaf** — for each feature, build a 256-bin histogram of gradients in that leaf, scan the bins for the best split point.

Per tree:
- Pick the feature + threshold that most reduces residual loss.
- Recurse until `NumberOfLeaves` (default 31) or `MinimumExampleCountPerLeaf` (default 10) blocks further splits.

Each new tree only has to learn the **errors** of the ensemble so far — that's why each tree is small and the whole thing stays fast on CPU. **No matrix multiplications, no activations** — nothing GPUs are designed to accelerate.

---

## 5. Does it need a GPU?

**No.** LightGBM is a CPU-first algorithm and `Microsoft.ML.LightGbm` ships only the **CPU build** of the native DLL. Your project trains entirely on the CPU and will continue to do so unless you go out of your way to swap in a GPU build.

Even on huge datasets, the GPU benefit for gradient boosting is modest (typically 2–5×) — nothing like the 50–100× you'd see for deep learning. Tree training is bound by histogram-bin updates and split-finding, which are already cache-friendly on CPU.

| Situation                                                 | GPU worth it?                          |
|-----------------------------------------------------------|----------------------------------------|
| Your wafer dataset (1 000 rows × 6 features)              | No — trains in a few hundred ms.       |
| 10 M+ rows × hundreds of features                         | Yes — ~3× speedup typical.             |
| 1 000s of models for hyper-parameter sweeps               | Maybe — parallelize on CPU instead.    |

If you ever wanted it: the GPU build is a separately-compiled `lib_lightgbm.dll` from <https://github.com/microsoft/LightGBM> with `-DUSE_GPU=1` (CUDA or OpenCL). Drop it into the app's output folder, replacing the CPU one. Microsoft does **not** ship this in NuGet — manual step.

---

## 6. Trainers exposed by ML.NET

| Task                       | Trainer                                                  |
|----------------------------|----------------------------------------------------------|
| Binary classification      | `BinaryClassification.Trainers.LightGbm`  (used here)    |
| Multi-class classification | `MulticlassClassification.Trainers.LightGbm`             |
| Regression                 | `Regression.Trainers.LightGbm`                           |
| Ranking                    | `Ranking.Trainers.LightGbm`                              |

All four wrap the same native DLL — only the loss function and output columns differ.

---

## 7. What's happening when this project calls it

```csharp
_ml.BinaryClassification.Trainers.LightGbm(
    labelColumnName:   "Label",        // bool → 0/1
    featureColumnName: "Features")     // dense float[6]
```

with default hyper-params resolves to:

| Hyper-param                  | Default | What it does                                                            |
|------------------------------|---------|-------------------------------------------------------------------------|
| `NumberOfIterations`         | 100     | How many trees to fit (boosting rounds).                                |
| `NumberOfLeaves`             | 31      | Max leaves per tree. Lower → smoother fit, less overfitting.            |
| `LearningRate`               | 0.2     | Shrinkage applied to each new tree's contribution.                      |
| `MinimumExampleCountPerLeaf` | 10      | A leaf can't be smaller than this. Strongest anti-overfit knob.         |
| `Booster`                    | gbdt    | Vanilla GBDT. Alternatives: `dart`, `goss`.                             |
| Loss                         | logloss | Binary cross-entropy → calibrated `Probability` column.                 |

Each tree sees `Features` (6 floats) and `Label` (bool), greedily picks the feature + threshold that splits the residuals best, and the next tree fits the *errors* of the previous ensemble. The final score is `Σ tree_i(x)`; logistic-squashed it becomes `Probability`; thresholded at 0.5 it becomes `PredictedLabel`.

---

## 8. End-to-end on your machine, for this project

```
WaferGrid (1000 rows, 6 features + Timestamp)
        │
        ▼
LoadFromEnumerable → IDataView          ◄── all CPU, in-process
        │
TrainTestSplit (800 / 200)
        │
Concatenate("Features", 6 cols)
        │
LightGbm.Fit ──► P/Invoke into lib_lightgbm.dll (CPU)
        │           ↓
        │      builds 100 trees of ≤31 leaves each, in milliseconds
        │
ITransformer ─► PredictionEngine ─► single-row inference (~µs)
```

**No GPU, no Python, no cloud, no internet** — everything happens inside the WPF process on your CPU.

---

## 9. When to reach for it / when not

| Reach for LightGBM                                              | Skip LightGBM                                                     |
|-----------------------------------------------------------------|-------------------------------------------------------------------|
| Tabular data, mix of numeric + categorical                      | Images, audio, text → use a deep model                            |
| < 10 M rows fits on one machine                                 | Streaming / online learning → use SGD or Vowpal Wabbit            |
| You want strong baselines fast and don't want to tune much      | You need a fully interpretable, single tree → use a decision tree |
| Many features, some sparse                                      | Tiny dataset (< ~200 rows) → linear/logistic regression is safer  |

---

## 10. Tuning tips (in priority order)

1. **`MinimumExampleCountPerLeaf`** — single biggest anti-overfit lever. Raise it on small/noisy data.
2. **`NumberOfLeaves`** — go down (15–31) for noisy data, up (63–127) for big clean datasets.
3. **`LearningRate` × `NumberOfIterations`** — halve the rate, double the iterations → almost always a small win.
4. **Early stopping** via `EarlyStoppingRound` on a validation split — cheap insurance.
5. **`Booster = "goss"`** — same trick from the original paper, ~2× faster training, tiny accuracy loss.

---

## 11. Two pieces ML.NET hides from you

- **The native DLL.** `lib_lightgbm.dll` lives next to your EXE after `dotnet publish`. ML.NET marshals the `IDataView` into the C library via P/Invoke. That's why the very first `Fit` call has a tiny startup cost.
- **Categorical features.** ML.NET expects categoricals to be either one-hot (`OneHotEncoding`) or hashed (`Hash`) **before** the LightGBM trainer. Native LightGBM has built-in categorical handling, but ML.NET doesn't expose it directly.

---

## 12. Why it suits the wafer-defect data here

- **Tabular**, low-dimensional (6 numeric features) — its sweet spot.
- **Noisy labels** (Bernoulli draw from a logistic) — gradient boosting with shrinkage is robust to noise.
- **Non-linear feature interactions** (drift score sums absolute deviations across 6 features) — trees recover this naturally; logistic regression would not.
- **Small dataset** (a few hundred to a few thousand rows) — fits in milliseconds, so retraining after every Add Row / Reset stays interactive.
- **Time-aware data** (Timestamp + slow drift over 180 days) — LightGBM handles the trend through ordinary feature splits; for explicit forecasting you'd still combine it with lag features rather than swap it out.
