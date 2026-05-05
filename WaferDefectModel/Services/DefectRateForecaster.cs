using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;
using WaferDefectModel.Models;

namespace WaferDefectModel.Services;

public record ForecastPoint(DateTime Date, float DefectRate, float LowerBound, float UpperBound);

public class DefectRateForecaster
{
    private class DailyRate
    {
        public DateTime Date { get; set; }
        public float DefectRate { get; set; }
    }

    private class RateForecast
    {
        public float[] Forecast { get; set; } = Array.Empty<float>();
        public float[] LowerBound { get; set; } = Array.Empty<float>();
        public float[] UpperBound { get; set; } = Array.Empty<float>();
    }

    public IReadOnlyList<ForecastPoint> Forecast(IEnumerable<WaferData> rows, int daysAhead = 14)
    {
        var daily = rows
            .GroupBy(r => r.Timestamp.Date)
            .Select(g => new DailyRate
            {
                Date = g.Key,
                DefectRate = (float)(g.Count(r => r.IsDefective) / (double)g.Count()),
            })
            .OrderBy(d => d.Date)
            .ToList();

        if (daily.Count < 30)
            throw new InvalidOperationException(
                $"Need at least 30 distinct days of data to forecast; got {daily.Count}.");

        var ml = new MLContext(seed: 1);
        var data = ml.Data.LoadFromEnumerable(daily);

        // Heuristics: short window relative to series, but at least a week.
        int windowSize = Math.Max(7, daily.Count / 12);
        int seriesLength = Math.Min(daily.Count, Math.Max(windowSize * 4, 60));

        var pipeline = ml.Forecasting.ForecastBySsa(
            outputColumnName: nameof(RateForecast.Forecast),
            inputColumnName: nameof(DailyRate.DefectRate),
            windowSize: windowSize,
            seriesLength: seriesLength,
            trainSize: daily.Count,
            horizon: daysAhead,
            confidenceLevel: 0.95f,
            confidenceLowerBoundColumn: nameof(RateForecast.LowerBound),
            confidenceUpperBoundColumn: nameof(RateForecast.UpperBound));

        var model = pipeline.Fit(data);
        var engine = model.CreateTimeSeriesEngine<DailyRate, RateForecast>(ml);
        var forecast = engine.Predict();

        var lastDate = daily[^1].Date;
        var result = new List<ForecastPoint>(daysAhead);
        for (int i = 0; i < daysAhead; i++)
        {
            result.Add(new ForecastPoint(
                lastDate.AddDays(i + 1),
                Math.Clamp(forecast.Forecast[i],   0f, 1f),
                Math.Clamp(forecast.LowerBound[i], 0f, 1f),
                Math.Clamp(forecast.UpperBound[i], 0f, 1f)));
        }
        return result;
    }
}
