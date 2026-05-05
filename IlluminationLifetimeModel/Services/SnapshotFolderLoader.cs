using IlluminationLifetimeModel.Models;

namespace IlluminationLifetimeModel.Services;

public record DatasetBuildResult(
    IReadOnlyList<IllumCalibSnapshot> Labeled,
    IReadOnlyList<IllumCalibSnapshot> RightCensored,
    int FailedFiles);

public static class SnapshotFolderLoader
{
    private const string IniPattern = "IllumCalib_Diff*.ini";

    public static DatasetBuildResult Build(
        string rootFolder,
        IReadOnlyList<LampReplacement> replacements,
        Action<string>? log = null)
    {
        if (!Directory.Exists(rootFolder))
            throw new DirectoryNotFoundException($"Root folder not found: {rootFolder}");

        var replacementsByMachine = replacements
            .GroupBy(r => r.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.ReplacementDate).ToList(), StringComparer.OrdinalIgnoreCase);

        var labeled = new List<IllumCalibSnapshot>();
        var censored = new List<IllumCalibSnapshot>();
        int failed = 0;

        foreach (var machineDir in Directory.EnumerateDirectories(rootFolder))
        {
            var machineId = Path.GetFileName(machineDir);
            var iniFiles = Directory.EnumerateFiles(machineDir, IniPattern, SearchOption.TopDirectoryOnly).ToList();
            if (iniFiles.Count == 0)
            {
                log?.Invoke($"[{machineId}] no INI files matching {IniPattern} — skipped");
                continue;
            }

            var snapshots = new List<IllumCalibSnapshot>(iniFiles.Count);
            foreach (var file in iniFiles)
            {
                try
                {
                    snapshots.Add(IniSnapshotParser.Parse(file, machineId));
                }
                catch (Exception ex)
                {
                    failed++;
                    log?.Invoke($"[{machineId}] failed to parse {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            snapshots.Sort((a, b) => a.RunDate.CompareTo(b.RunDate));

            for (int i = 0; i < snapshots.Count; i++)
            {
                snapshots[i].DaysSincePrevCalibration = i == 0
                    ? 0f
                    : (float)(snapshots[i].RunDate - snapshots[i - 1].RunDate).TotalDays;
            }

            replacementsByMachine.TryGetValue(machineId, out var machineReplacements);
            foreach (var snap in snapshots)
            {
                var nextReplacement = machineReplacements?
                    .FirstOrDefault(r => r.ReplacementDate >= snap.RunDate);

                if (nextReplacement is null)
                {
                    snap.RulDays = float.NaN;
                    censored.Add(snap);
                }
                else
                {
                    snap.RulDays = (float)(nextReplacement.ReplacementDate - snap.RunDate).TotalDays;
                    labeled.Add(snap);
                }
            }

            log?.Invoke($"[{machineId}] {snapshots.Count} snapshots ({labeled.Count(s => s.MachineId == machineId)} labeled, {censored.Count(s => s.MachineId == machineId)} right-censored)");
        }

        return new DatasetBuildResult(labeled, censored, failed);
    }
}
