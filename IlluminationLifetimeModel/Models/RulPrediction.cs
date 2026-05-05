using Microsoft.ML.Data;

namespace IlluminationLifetimeModel.Models;

public class RulPrediction
{
    [ColumnName("Score")] public float PredictedRulDays { get; set; }
}
