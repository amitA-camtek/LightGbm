using IlluminationLifetimeModel.Models;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace IlluminationLifetimeModel.Services;

public class LifetimeRegressor
{
    private readonly MLContext _ml = new(seed: 1);
    private ITransformer? _model;
    private DataViewSchema? _schema;
    private PredictionEngine<IllumCalibSnapshot, RulPrediction>? _engine;

    public bool IsTrained => _engine is not null;

    public RegressionMetrics Train(IReadOnlyList<IllumCalibSnapshot> labeled)
    {
        if (labeled.Count < 10)
            throw new InvalidOperationException($"Need at least 10 labeled snapshots; got {labeled.Count}.");

        var (trainSet, testSet) = TimeAwareSplit(labeled, holdoutFraction: 0.2);

        var trainData = _ml.Data.LoadFromEnumerable(trainSet);
        var testData = _ml.Data.LoadFromEnumerable(testSet);

        var featureInputs = new[] { "MachineIdEncoded" }
            .Concat(IllumCalibSnapshot.NumericFeatureNames)
            .ToArray();

        var pipeline = _ml.Transforms.Categorical.OneHotHashEncoding(
                outputColumnName: "MachineIdEncoded",
                inputColumnName: nameof(IllumCalibSnapshot.MachineId),
                numberOfBits: 10)
            .Append(_ml.Transforms.Concatenate("Features", featureInputs))
            .Append(_ml.Regression.Trainers.LightGbm(
                labelColumnName: "Label",
                featureColumnName: "Features"));

        _model = pipeline.Fit(trainData);
        _schema = trainData.Schema;

        var predictions = _model.Transform(testData);
        var metrics = _ml.Regression.Evaluate(predictions, labelColumnName: "Label");

        _engine = _ml.Model.CreatePredictionEngine<IllumCalibSnapshot, RulPrediction>(_model);
        return metrics;
    }

    public RulPrediction Predict(IllumCalibSnapshot input)
    {
        if (_engine is null)
            throw new InvalidOperationException("Model is not trained yet. Call Train(...) first.");
        return _engine.Predict(input);
    }

    public void Save(string path)
    {
        if (_model is null || _schema is null)
            throw new InvalidOperationException("Train before saving.");
        _ml.Model.Save(_model, _schema, path);
    }

    public void Load(string path)
    {
        _model = _ml.Model.Load(path, out var schema);
        _schema = schema;
        _engine = _ml.Model.CreatePredictionEngine<IllumCalibSnapshot, RulPrediction>(_model);
    }

    private static (List<IllumCalibSnapshot> train, List<IllumCalibSnapshot> test) TimeAwareSplit(
        IReadOnlyList<IllumCalibSnapshot> rows, double holdoutFraction)
    {
        var train = new List<IllumCalibSnapshot>();
        var test = new List<IllumCalibSnapshot>();

        foreach (var group in rows.GroupBy(r => r.MachineId))
        {
            var sorted = group.OrderBy(r => r.RunDate).ToList();
            int splitIdx = (int)Math.Floor(sorted.Count * (1.0 - holdoutFraction));
            if (splitIdx <= 0) splitIdx = 1;
            if (splitIdx >= sorted.Count) splitIdx = sorted.Count - 1;

            for (int i = 0; i < sorted.Count; i++)
            {
                if (i < splitIdx) train.Add(sorted[i]);
                else test.Add(sorted[i]);
            }
        }

        if (test.Count == 0 && train.Count > 1)
        {
            test.Add(train[^1]);
            train.RemoveAt(train.Count - 1);
        }

        return (train, test);
    }
}
