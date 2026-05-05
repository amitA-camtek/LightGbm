using System.Globalization;
using IlluminationLifetimeModel.Models;

namespace IlluminationLifetimeModel.Services;

public static class IniSnapshotParser
{
    public static IllumCalibSnapshot Parse(string iniPath, string machineId)
    {
        var sections = ReadIniSections(iniPath);

        var snap = new IllumCalibSnapshot
        {
            MachineId = machineId,
            SourceFile = Path.GetFileName(iniPath),
            DaysSincePrevCalibration = 0f,
        };

        var maxLimits = sections.GetValueOrDefault("SystemHighIntensityMaxLimit");

        ReadChannel(sections, maxLimits, 0, (a0, a1, a2, mx, mn, mg) =>
            { snap.Cf0A0 = a0; snap.Cf0A1 = a1; snap.Cf0A2 = a2; snap.Cf0Max = mx; snap.Cf0Min = mn; snap.Cf0Margin = mg; });
        ReadChannel(sections, maxLimits, 1, (a0, a1, a2, mx, mn, mg) =>
            { snap.Cf1A0 = a0; snap.Cf1A1 = a1; snap.Cf1A2 = a2; snap.Cf1Max = mx; snap.Cf1Min = mn; snap.Cf1Margin = mg; });
        ReadChannel(sections, maxLimits, 2, (a0, a1, a2, mx, mn, mg) =>
            { snap.Cf2A0 = a0; snap.Cf2A1 = a1; snap.Cf2A2 = a2; snap.Cf2Max = mx; snap.Cf2Min = mn; snap.Cf2Margin = mg; });
        ReadChannel(sections, maxLimits, 3, (a0, a1, a2, mx, mn, mg) =>
            { snap.Cf3A0 = a0; snap.Cf3A1 = a1; snap.Cf3A2 = a2; snap.Cf3Max = mx; snap.Cf3Min = mn; snap.Cf3Margin = mg; });
        ReadChannel(sections, maxLimits, 4, (a0, a1, a2, mx, mn, mg) =>
            { snap.Cf4A0 = a0; snap.Cf4A1 = a1; snap.Cf4A2 = a2; snap.Cf4Max = mx; snap.Cf4Min = mn; snap.Cf4Margin = mg; });
        ReadChannel(sections, maxLimits, 5, (a0, a1, a2, mx, mn, mg) =>
            { snap.Cf5A0 = a0; snap.Cf5A1 = a1; snap.Cf5A2 = a2; snap.Cf5Max = mx; snap.Cf5Min = mn; snap.Cf5Margin = mg; });

        snap.RunDate = ResolveRunDate(sections);
        return snap;
    }

    private static void ReadChannel(
        Dictionary<string, Dictionary<string, string>> sections,
        Dictionary<string, string>? maxLimits,
        int channel,
        Action<float, float, float, float, float, float> assign)
    {
        var section = sections.GetValueOrDefault($"ColorFilter_{channel}")
                      ?? throw new InvalidDataException($"Missing [ColorFilter_{channel}] section.");

        var a0 = ReadFloat(section, "BilinearVoltageTransform_A0");
        var a1 = ReadFloat(section, "BilinearVoltageTransform_A1");
        var a2 = ReadFloat(section, "BilinearVoltageTransform_A2");
        var max = ReadFloat(section, "MaxLightIntensity");
        var min = ReadFloat(section, "MinLightIntensity");

        var limit = maxLimits is not null
            ? ReadFloatOrDefault(maxLimits, $"ColorFilter_{channel}", float.NaN)
            : float.NaN;
        var margin = float.IsNaN(limit) ? 0f : max - limit;

        assign(a0, a1, a2, max, min, margin);
    }

    private static DateTime ResolveRunDate(Dictionary<string, Dictionary<string, string>> sections)
    {
        DateTime? latest = null;
        for (int c = 0; c < 6; c++)
        {
            var section = sections.GetValueOrDefault($"ColorFilter_{c}");
            if (section is null) continue;
            if (!section.TryGetValue("RunDate", out var raw)) continue;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) continue;
            if (latest is null || parsed > latest) latest = parsed;
        }
        return latest ?? DateTime.MinValue;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIniSections(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (!result.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[name] = current;
                }
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0 || current is null) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            current[key] = value;
        }

        return result;
    }

    private static float ReadFloat(Dictionary<string, string> section, string key)
    {
        if (!section.TryGetValue(key, out var raw))
            throw new InvalidDataException($"Missing key '{key}'.");
        return float.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static float ReadFloatOrDefault(Dictionary<string, string> section, string key, float fallback)
    {
        if (!section.TryGetValue(key, out var raw)) return fallback;
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
