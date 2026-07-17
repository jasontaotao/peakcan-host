using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    // Flow: CSV export of DBC messages.
    // Method moved verbatim from DbcViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - ExportCsv -> _allMessages (main field)
    //
    // [RelayCommand] attribute MUST travel with ExportCsv method.
    // Microsoft.Win32.SaveFileDialog is WPF-specific; this partial stays
    // in the App layer (not Core).

    /// <summary>
    /// Export DBC messages to a CSV file.
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        if (_allMessages.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "dbc-messages.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ID,Name,DLC,Sender,Signals");
        foreach (var m in _allMessages)
        {
            sb.AppendLine(string.Join(',',
                m.Id,
                m.Name,
                m.Dlc,
                m.Sender,
                m.SignalCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
    }
}
