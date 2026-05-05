using Microsoft.ML.Data;

namespace WaferDefectModel.Models;

public class DefectPrediction
{
    [ColumnName("PredictedLabel")] public bool IsDefective { get; set; }
    public float Probability { get; set; }
    public float Score       { get; set; }
}
