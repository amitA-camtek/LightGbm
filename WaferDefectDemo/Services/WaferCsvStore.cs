using System.Globalization;
using System.IO;
using System.Text;
using WaferDefectModel.Models;

namespace WaferDefectDemo.Services;

public class WaferCsvStore
{
    private const string Header = "Timestamp,Temperature,Pressure,EtchTime,ParticleCount,FilmThickness,Uniformity,IsDefective";

    public string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "wafers.csv");

    public bool Exists() => File.Exists(Path);

    public IReadOnlyList<WaferData> Load()
    {
        var lines = File.ReadAllLines(Path);
        var rows = new List<WaferData>(lines.Length);
        var ci = CultureInfo.InvariantCulture;

        for (int i = 1; i < lines.Length; i++)  // skip header
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var p = line.Split(',');
            if (p.Length < 8) continue;

            rows.Add(new WaferData
            {
                Timestamp     = DateTime.Parse(p[0], ci, DateTimeStyles.RoundtripKind),
                Temperature   = float.Parse(p[1], ci),
                Pressure      = float.Parse(p[2], ci),
                EtchTime      = float.Parse(p[3], ci),
                ParticleCount = float.Parse(p[4], ci),
                FilmThickness = float.Parse(p[5], ci),
                Uniformity    = float.Parse(p[6], ci),
                IsDefective   = bool.Parse(p[7]),
            });
        }

        return rows;
    }

    public void Save(IEnumerable<WaferData> rows)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var r in rows)
        {
            sb.Append(r.Timestamp.ToString("o", ci)).Append(',')
              .Append(r.Temperature.ToString(ci)).Append(',')
              .Append(r.Pressure.ToString(ci)).Append(',')
              .Append(r.EtchTime.ToString(ci)).Append(',')
              .Append(r.ParticleCount.ToString(ci)).Append(',')
              .Append(r.FilmThickness.ToString(ci)).Append(',')
              .Append(r.Uniformity.ToString(ci)).Append(',')
              .Append(r.IsDefective ? "true" : "false")
              .AppendLine();
        }
        File.WriteAllText(Path, sb.ToString());
    }

    public IReadOnlyList<WaferData> EnsureSeeded()
    {
        if (Exists())
        {
            // Regenerate if the on-disk schema doesn't match (e.g. older format
            // without a Timestamp column).
            var firstLine = File.ReadLines(Path).FirstOrDefault();
            if (firstLine != Header)
            {
                Save(DataGenerator.Generate());
            }
        }
        else
        {
            Save(DataGenerator.Generate());
        }
        return Load();
    }
}
