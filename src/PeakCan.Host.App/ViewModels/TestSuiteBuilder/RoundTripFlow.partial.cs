using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Serialization;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

public sealed partial class TestSuiteBuilderViewModel
{
    public void LoadFromText(string json)
    {
        try
        {
            var suite = JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default)
                ?? throw new InvalidDataException("suite.json is empty");
            Cases.Clear();
            foreach (var c in suite.Cases) Cases.Add(EditableTestCase.FromCase(c));
            SuiteName = suite.Name;
            GlobalCaseFixtureKeys = suite.GlobalCaseFixtureKeys ?? Array.Empty<string>();
            SuiteFixtureKeys = suite.SuiteFixtureKeys ?? Array.Empty<string>();
            FailurePolicy = suite.Config?.FailurePolicy ?? FailurePolicy.ContinueAll;
            ContinueAfterSetupFailure = suite.Config?.ContinueAfterSetupFailure ?? true;
            TimeoutMs = suite.TimeoutMs;
            SelectedCase = Cases.FirstOrDefault();
            SelectedStep = null;
            Status = $"Loaded {Cases.Count} case(s) from {_suitePath ?? "(text)"}";
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "Load failed.";
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = _fileDialog.ShowOpenDialog("Test Suite JSON|*.json|All Files|*.*");
        if (path is null) return;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            _suitePath = path;
            LoadFromText(json);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "Open failed.";
        }
    }

    [RelayCommand]
    private void Save() => SaveCore(_suitePath);

    [RelayCommand]
    private void SaveAs()
    {
        var dir = _suitePath is null ? null : Path.GetDirectoryName(_suitePath);
        var chosen = _fileDialog.ShowSaveDialog("Test Suite JSON|*.json", ".json", dir);
        if (chosen is null) return;
        SaveCore(chosen);
    }

    private void SaveCore(string? path)
    {
        if (string.IsNullOrEmpty(path)) { SaveAs(); return; }
        try
        {
            var json = JsonSerializer.Serialize(ToSuite(), HILJsonOptions.Default);
            File.WriteAllText(path, json);
            _suitePath = path;
            Status = $"Saved {path}";
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "Save failed.";
        }
    }
}
