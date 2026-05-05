using WaferDefectModel.Models;

namespace WaferDefectDemo.Services;

public static class DataGenerator
{
    public static IReadOnlyList<WaferData> Generate(int n = 1000, int seed = 42, int days = 180)
    {
        var rng = new Random(seed);
        var rows = new List<WaferData>(n);
        var startDate = DateTime.Today.AddDays(-days);

        for (int i = 0; i < n; i++)
        {
            // Spread evenly across the time window.
            double dayFraction = (double)i / n;
            int dayIndex = (int)(dayFraction * days);
            var timestamp = startDate.AddDays(dayIndex)
                                     .AddHours(rng.NextDouble() * 24);

            // Slow drift over time: temperature creeps up, particles increase
            // — simulating fab degradation between maintenance cycles.
            double t = dayIndex / (double)days;
            float driftT = (float)(t * 8.0);   // up to +8 °C
            float driftP = (float)(t * 20.0);  // up to +20 particles

            float temperature   = (float)(350 + rng.NextDouble() * 100) + driftT;
            float pressure      = (float)(50  + rng.NextDouble() * 100);
            float etchTime      = (float)(30  + rng.NextDouble() * 60);
            float particleCount = (float)(rng.NextDouble() * 80) + driftP;
            float filmThickness = (float)(90  + rng.NextDouble() * 20);
            float uniformity    = (float)(90  + rng.NextDouble() * 10);

            double drift =
                  Math.Abs(temperature   - 400f) / 50.0
                + Math.Abs(pressure      - 100f) / 50.0
                + Math.Abs(etchTime      -  60f) / 30.0
                + Math.Max(0, particleCount - 15) / 65.0
                + Math.Abs(filmThickness - 100f) / 10.0
                + Math.Max(0, 98 - uniformity)   /  8.0;

            double prob = 1.0 / (1.0 + Math.Exp(-(drift * 1.4 - 1.6)));
            bool isDefective = rng.NextDouble() < prob;

            rows.Add(new WaferData
            {
                Timestamp     = timestamp,
                Temperature   = temperature,
                Pressure      = pressure,
                EtchTime      = etchTime,
                ParticleCount = particleCount,
                FilmThickness = filmThickness,
                Uniformity    = uniformity,
                IsDefective   = isDefective,
            });
        }

        return rows;
    }
}
