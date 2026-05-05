using Microsoft.ML.Data;

namespace WaferDefectModel.Models;

public class WaferData
{
    public DateTime Timestamp { get; set; }

    [LoadColumn(1)] public float Temperature   { get; set; }
    [LoadColumn(2)] public float Pressure      { get; set; }
    [LoadColumn(3)] public float EtchTime      { get; set; }
    [LoadColumn(4)] public float ParticleCount { get; set; }
    [LoadColumn(5)] public float FilmThickness { get; set; }
    [LoadColumn(6)] public float Uniformity    { get; set; }

    [LoadColumn(7), ColumnName("Label")]
    public bool IsDefective { get; set; }
}
