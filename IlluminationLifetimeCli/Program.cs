using System.Globalization;
using IlluminationLifetimeModel.Models;
using IlluminationLifetimeModel.Services;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

return args[0].ToLowerInvariant() switch
{
    "build"   => Build(args[1..]),
    "train"   => Train(args[1..]),
    "predict" => Predict(args[1..]),
    _         => UnknownCommand(args[0]),
};

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  IlluminationLifetimeCli build   <ini-root> <replacement-log.csv> <out.csv>");
    Console.WriteLine("  IlluminationLifetimeCli train   <ini-root> <replacement-log.csv> <model.zip>");
    Console.WriteLine("  IlluminationLifetimeCli predict <model.zip> <single-ini-file> <machine-id>");
}

static int Build(string[] args)
{
    if (args.Length < 3) { PrintUsage(); return 1; }
    var (root, logPath, outCsv) = (args[0], args[1], args[2]);

    var replacements = ReplacementLogReader.Read(logPath);
    var result = SnapshotFolderLoader.Build(root, replacements, Console.WriteLine);

    Console.WriteLine();
    Console.WriteLine($"Labeled snapshots:        {result.Labeled.Count}");
    Console.WriteLine($"Right-censored (dropped): {result.RightCensored.Count}");
    Console.WriteLine($"Failed parses:            {result.FailedFiles}");

    WriteSnapshotCsv(outCsv, result.Labeled);
    Console.WriteLine($"Wrote dataset -> {outCsv}");
    return 0;
}

static int Train(string[] args)
{
    if (args.Length < 3) { PrintUsage(); return 1; }
    var (root, logPath, modelPath) = (args[0], args[1], args[2]);

    var replacements = ReplacementLogReader.Read(logPath);
    var result = SnapshotFolderLoader.Build(root, replacements, Console.WriteLine);

    if (result.Labeled.Count < 10)
    {
        Console.Error.WriteLine($"Only {result.Labeled.Count} labeled snapshots — need at least 10.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"Training on {result.Labeled.Count} labeled snapshots ...");

    var regressor = new LifetimeRegressor();
    var metrics = regressor.Train(result.Labeled);

    Console.WriteLine();
    Console.WriteLine("Holdout regression metrics:");
    Console.WriteLine($"  MAE  (days):  {metrics.MeanAbsoluteError:F2}");
    Console.WriteLine($"  RMSE (days):  {metrics.RootMeanSquaredError:F2}");
    Console.WriteLine($"  R^2:          {metrics.RSquared:F3}");

    regressor.Save(modelPath);
    Console.WriteLine($"Saved model -> {modelPath}");
    return 0;
}

static int Predict(string[] args)
{
    if (args.Length < 3) { PrintUsage(); return 1; }
    var (modelPath, iniPath, machineId) = (args[0], args[1], args[2]);

    var regressor = new LifetimeRegressor();
    regressor.Load(modelPath);

    var snap = IniSnapshotParser.Parse(iniPath, machineId);
    var pred = regressor.Predict(snap);

    Console.WriteLine($"Snapshot:           {snap.SourceFile}  ({snap.MachineId}, {snap.RunDate:yyyy-MM-dd HH:mm})");
    Console.WriteLine($"Predicted RUL:      {pred.PredictedRulDays:F1} days");
    return 0;
}

static void WriteSnapshotCsv(string path, IReadOnlyList<IllumCalibSnapshot> rows)
{
    var ci = CultureInfo.InvariantCulture;
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    using var writer = new StreamWriter(path);
    writer.Write("MachineId,SourceFile,RunDate,DaysSincePrevCalibration");
    foreach (var name in IllumCalibSnapshot.NumericFeatureNames.Skip(1))
        writer.Write($",{name}");
    writer.WriteLine(",RulDays");

    foreach (var s in rows)
    {
        writer.Write($"{s.MachineId},{s.SourceFile},{s.RunDate.ToString("o", ci)},{s.DaysSincePrevCalibration.ToString(ci)}");
        writer.Write($",{s.Cf0A0.ToString(ci)},{s.Cf0A1.ToString(ci)},{s.Cf0A2.ToString(ci)},{s.Cf0Max.ToString(ci)},{s.Cf0Min.ToString(ci)},{s.Cf0Margin.ToString(ci)}");
        writer.Write($",{s.Cf1A0.ToString(ci)},{s.Cf1A1.ToString(ci)},{s.Cf1A2.ToString(ci)},{s.Cf1Max.ToString(ci)},{s.Cf1Min.ToString(ci)},{s.Cf1Margin.ToString(ci)}");
        writer.Write($",{s.Cf2A0.ToString(ci)},{s.Cf2A1.ToString(ci)},{s.Cf2A2.ToString(ci)},{s.Cf2Max.ToString(ci)},{s.Cf2Min.ToString(ci)},{s.Cf2Margin.ToString(ci)}");
        writer.Write($",{s.Cf3A0.ToString(ci)},{s.Cf3A1.ToString(ci)},{s.Cf3A2.ToString(ci)},{s.Cf3Max.ToString(ci)},{s.Cf3Min.ToString(ci)},{s.Cf3Margin.ToString(ci)}");
        writer.Write($",{s.Cf4A0.ToString(ci)},{s.Cf4A1.ToString(ci)},{s.Cf4A2.ToString(ci)},{s.Cf4Max.ToString(ci)},{s.Cf4Min.ToString(ci)},{s.Cf4Margin.ToString(ci)}");
        writer.Write($",{s.Cf5A0.ToString(ci)},{s.Cf5A1.ToString(ci)},{s.Cf5A2.ToString(ci)},{s.Cf5Max.ToString(ci)},{s.Cf5Min.ToString(ci)},{s.Cf5Margin.ToString(ci)}");
        writer.WriteLine($",{s.RulDays.ToString(ci)}");
    }
}
