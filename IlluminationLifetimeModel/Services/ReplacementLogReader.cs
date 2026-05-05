using System.Globalization;

namespace IlluminationLifetimeModel.Services;

public record LampReplacement(string MachineId, DateTime ReplacementDate);

public static class ReplacementLogReader
{
    public static IReadOnlyList<LampReplacement> Read(string csvPath)
    {
        var rows = new List<LampReplacement>();
        using var reader = new StreamReader(csvPath);

        var header = reader.ReadLine()
            ?? throw new InvalidDataException("Replacement log is empty.");
        var cols = header.Split(',').Select(c => c.Trim()).ToArray();
        int idIdx = Array.FindIndex(cols, c => c.Equals("MachineId", StringComparison.OrdinalIgnoreCase));
        int dateIdx = Array.FindIndex(cols, c => c.Equals("ReplacementDate", StringComparison.OrdinalIgnoreCase));
        if (idIdx < 0 || dateIdx < 0)
            throw new InvalidDataException("Header must contain MachineId and ReplacementDate columns.");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length <= Math.Max(idIdx, dateIdx)) continue;

            var id = parts[idIdx].Trim();
            var dateRaw = parts[dateIdx].Trim();
            if (id.Length == 0 || dateRaw.Length == 0) continue;

            if (!DateTime.TryParse(dateRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
                throw new InvalidDataException($"Could not parse ReplacementDate '{dateRaw}' for MachineId '{id}'.");

            rows.Add(new LampReplacement(id, date));
        }

        return rows;
    }
}
