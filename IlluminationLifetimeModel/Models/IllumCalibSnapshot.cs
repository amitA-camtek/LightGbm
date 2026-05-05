using Microsoft.ML.Data;

namespace IlluminationLifetimeModel.Models;

public class IllumCalibSnapshot
{
    public string MachineId { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public DateTime RunDate { get; set; }

    public float DaysSincePrevCalibration { get; set; }

    public float Cf0A0 { get; set; }
    public float Cf0A1 { get; set; }
    public float Cf0A2 { get; set; }
    public float Cf0Max { get; set; }
    public float Cf0Min { get; set; }
    public float Cf0Margin { get; set; }

    public float Cf1A0 { get; set; }
    public float Cf1A1 { get; set; }
    public float Cf1A2 { get; set; }
    public float Cf1Max { get; set; }
    public float Cf1Min { get; set; }
    public float Cf1Margin { get; set; }

    public float Cf2A0 { get; set; }
    public float Cf2A1 { get; set; }
    public float Cf2A2 { get; set; }
    public float Cf2Max { get; set; }
    public float Cf2Min { get; set; }
    public float Cf2Margin { get; set; }

    public float Cf3A0 { get; set; }
    public float Cf3A1 { get; set; }
    public float Cf3A2 { get; set; }
    public float Cf3Max { get; set; }
    public float Cf3Min { get; set; }
    public float Cf3Margin { get; set; }

    public float Cf4A0 { get; set; }
    public float Cf4A1 { get; set; }
    public float Cf4A2 { get; set; }
    public float Cf4Max { get; set; }
    public float Cf4Min { get; set; }
    public float Cf4Margin { get; set; }

    public float Cf5A0 { get; set; }
    public float Cf5A1 { get; set; }
    public float Cf5A2 { get; set; }
    public float Cf5Max { get; set; }
    public float Cf5Min { get; set; }
    public float Cf5Margin { get; set; }

    [ColumnName("Label")] public float RulDays { get; set; } = float.NaN;

    public static readonly string[] NumericFeatureNames =
    {
        nameof(DaysSincePrevCalibration),
        nameof(Cf0A0), nameof(Cf0A1), nameof(Cf0A2), nameof(Cf0Max), nameof(Cf0Min), nameof(Cf0Margin),
        nameof(Cf1A0), nameof(Cf1A1), nameof(Cf1A2), nameof(Cf1Max), nameof(Cf1Min), nameof(Cf1Margin),
        nameof(Cf2A0), nameof(Cf2A1), nameof(Cf2A2), nameof(Cf2Max), nameof(Cf2Min), nameof(Cf2Margin),
        nameof(Cf3A0), nameof(Cf3A1), nameof(Cf3A2), nameof(Cf3Max), nameof(Cf3Min), nameof(Cf3Margin),
        nameof(Cf4A0), nameof(Cf4A1), nameof(Cf4A2), nameof(Cf4Max), nameof(Cf4Min), nameof(Cf4Margin),
        nameof(Cf5A0), nameof(Cf5A1), nameof(Cf5A2), nameof(Cf5Max), nameof(Cf5Min), nameof(Cf5Margin),
    };
}
