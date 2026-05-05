using Microsoft.ML;
using Microsoft.ML.Data;
using WaferDefectModel.Models;

namespace WaferDefectModel.Services;

public class DefectClassifier
{
    private readonly MLContext _ml = new(seed: 1);
    private ITransformer? _model;
    private DataViewSchema? _schema;
    private PredictionEngine<WaferData, DefectPrediction>? _engine;

    public bool IsTrained => _engine is not null;

    public BinaryClassificationMetrics Train(IEnumerable<WaferData> rows)
    {
        var data = _ml.Data.LoadFromEnumerable(rows);
        var split = _ml.Data.TrainTestSplit(data, testFraction: 0.2, seed: 1);

        var pipeline = _ml.Transforms.Concatenate(
                "Features",
                nameof(WaferData.Temperature),
                nameof(WaferData.Pressure),
                nameof(WaferData.EtchTime),
                nameof(WaferData.ParticleCount),
                nameof(WaferData.FilmThickness),
                nameof(WaferData.Uniformity))
            .Append(_ml.BinaryClassification.Trainers.LightGbm(
                labelColumnName: "Label",
                featureColumnName: "Features"));

        _model = pipeline.Fit(split.TrainSet);
        _schema = data.Schema;

        var predictions = _model.Transform(split.TestSet);
        var metrics = _ml.BinaryClassification.Evaluate(predictions, labelColumnName: "Label");

        _engine = _ml.Model.CreatePredictionEngine<WaferData, DefectPrediction>(_model);
        return metrics;
    }

    public DefectPrediction Predict(WaferData input)
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
        _engine = _ml.Model.CreatePredictionEngine<WaferData, DefectPrediction>(_model);
    }
}
