using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WaferDefectDemo.Services;
using WaferDefectModel.Models;
using WaferDefectModel.Services;

namespace WaferDefectDemo;

public partial class MainWindow : Window
{
    private readonly WaferCsvStore _store = new();
    private readonly DefectClassifier _classifier = new();
    private readonly DefectRateForecaster _forecaster = new();
    private readonly ObservableCollection<WaferData> _rows = new();

    private static readonly Brush PassBrush     = new SolidColorBrush(Color.FromRgb(0xD4, 0xF5, 0xD4));
    private static readonly Brush DefectBrush   = new SolidColorBrush(Color.FromRgb(0xF8, 0xC8, 0xC8));
    private static readonly Brush NeutralBrush  = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            foreach (var w in _store.EnsureSeeded())
                _rows.Add(w);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to load dataset: {ex.Message}",
                "Wafer Defect Predictor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        WaferGrid.ItemsSource = _rows;
        UpdateDatasetStatus();
        StatusText.Text = "Click Train Model to fit the LightGBM classifier on the current dataset.";
    }

    private void OnAddRowClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadInputs(out var row, out var error))
        {
            StatusText.Text = $"Add Row failed: {error}";
            return;
        }

        row.IsDefective = IsDefectiveBox.IsChecked == true;
        row.Timestamp = DateTime.Now;
        _rows.Add(row);

        // Adding new training data invalidates the previously trained model.
        PredictButton.IsEnabled = false;
        UpdateDatasetStatus();
        StatusText.Text = $"Row added. Re-train the model to incorporate it.";
    }

    private void OnSaveDatasetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _store.Save(_rows);
            StatusText.Text = $"Saved {_rows.Count} wafers to {_store.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void OnResetDatasetClick(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(this,
            "Replace the current dataset with a fresh seeded sample of 200 wafers?",
            "Reset dataset",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (ok != MessageBoxResult.Yes) return;

        // Fresh seed each reset so the user actually sees the data change.
        var fresh = DataGenerator.Generate(seed: Random.Shared.Next());
        _store.Save(fresh);
        _rows.Clear();
        foreach (var w in fresh) _rows.Add(w);

        PredictButton.IsEnabled = false;
        UpdateDatasetStatus();
        StatusText.Text = $"Dataset reset — {_rows.Count} fresh wafers generated.";
    }

    private void OnTrainClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count < 20)
        {
            StatusText.Text = "Need at least ~20 rows to train.";
            return;
        }

        try
        {
            var m = _classifier.Train(_rows);
            StatusText.Text =
                $"Trained · Accuracy {m.Accuracy:F3} · AUC {m.AreaUnderRocCurve:F3} · F1 {m.F1Score:F3}";
            PredictButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Train failed: {ex.Message}";
            PredictButton.IsEnabled = false;
        }
    }

    private void OnPredictClick(object sender, RoutedEventArgs e)
    {
        if (!_classifier.IsTrained)
        {
            StatusText.Text = "Train the model first.";
            return;
        }
        if (!TryReadInputs(out var input, out var error))
        {
            StatusText.Text = $"Predict failed: {error}";
            return;
        }

        var p = _classifier.Predict(input);
        ResultBox.Background = p.IsDefective ? DefectBrush : PassBrush;
        ResultText.Text =
            (p.IsDefective ? "DEFECTIVE" : "PASS") +
            $"   ·   P(defective) = {p.Probability:P1}";
    }

    private void OnSaveModelClick(object sender, RoutedEventArgs e)
    {
        if (!_classifier.IsTrained)
        {
            StatusText.Text = "Train the model before saving.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            FileName = "wafer-model.zip",
            Filter = "ML.NET model (*.zip)|*.zip",
            DefaultExt = ".zip",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            _classifier.Save(dlg.FileName);
            StatusText.Text = $"Model saved to {dlg.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save model failed: {ex.Message}";
        }
    }

    private void OnLoadModelClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ML.NET model (*.zip)|*.zip",
            DefaultExt = ".zip",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            _classifier.Load(dlg.FileName);
            PredictButton.IsEnabled = true;
            StatusText.Text = $"Model loaded from {dlg.FileName}. Predict is enabled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Load model failed: {ex.Message}";
            PredictButton.IsEnabled = false;
        }
    }

    private void OnForecastClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var points = _forecaster.Forecast(_rows, daysAhead: 14);
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("Date         Defect%   95% CI");
            foreach (var p in points)
            {
                sb.AppendLine(string.Format(ci,
                    "{0:yyyy-MM-dd}   {1,6:P1}   [{2:P1} – {3:P1}]",
                    p.Date, p.DefectRate, p.LowerBound, p.UpperBound));
            }
            ForecastText.Text = sb.ToString();
            StatusText.Text = $"Forecast complete · next {points.Count} days.";
        }
        catch (Exception ex)
        {
            ForecastText.Text = "No forecast yet.";
            StatusText.Text = $"Forecast failed: {ex.Message}";
        }
    }

    private bool TryReadInputs(out WaferData row, out string error)
    {
        row = new WaferData();
        var ci = CultureInfo.InvariantCulture;

        if (!float.TryParse(TemperatureBox.Text,   NumberStyles.Float, ci, out var t)) { error = "Temperature is not a number.";   return false; }
        if (!float.TryParse(PressureBox.Text,      NumberStyles.Float, ci, out var p)) { error = "Pressure is not a number.";      return false; }
        if (!float.TryParse(EtchTimeBox.Text,      NumberStyles.Float, ci, out var et)){ error = "EtchTime is not a number.";      return false; }
        if (!float.TryParse(ParticleCountBox.Text, NumberStyles.Float, ci, out var pc)){ error = "ParticleCount is not a number."; return false; }
        if (!float.TryParse(FilmThicknessBox.Text, NumberStyles.Float, ci, out var ft)){ error = "FilmThickness is not a number."; return false; }
        if (!float.TryParse(UniformityBox.Text,    NumberStyles.Float, ci, out var u)) { error = "Uniformity is not a number.";    return false; }

        row.Temperature   = t;
        row.Pressure      = p;
        row.EtchTime      = et;
        row.ParticleCount = pc;
        row.FilmThickness = ft;
        row.Uniformity    = u;
        error = "";
        return true;
    }

    private void UpdateDatasetStatus()
    {
        int total = _rows.Count;
        int defective = 0;
        foreach (var r in _rows) if (r.IsDefective) defective++;
        double pct = total == 0 ? 0 : 100.0 * defective / total;
        DatasetStatus.Text = $"{total} wafers  ·  {pct:F0}% defective  ·  source: {_store.Path}";
    }
}
