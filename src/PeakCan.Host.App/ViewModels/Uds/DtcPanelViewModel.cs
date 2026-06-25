using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using PeakCan.Host.Core.Uds;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Panel VM for the DTCs tab. DTCs collection is wholesale-replaced on
/// each ReadDtcsCommand (DtcRow is plain class, no INotifyPropertyChanged).
/// </summary>
public sealed partial class DtcPanelViewModel : ObservableObject, IUdsPanel
{
    private readonly UdsClient _udsClient;
    private ObservableCollection<UdsLogLine>? _log;

    public ObservableCollection<DtcRow> Dtcs { get; } = new();

    public DtcPanelViewModel(UdsClient udsClient)
    {
        ArgumentNullException.ThrowIfNull(udsClient);
        _udsClient = udsClient;
    }

    public void AttachLog(ObservableCollection<UdsLogLine> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    [RelayCommand]
    private async Task ReadDtcsAsync()
    {
        try
        {
            AppendLog("Info", "Reading DTCs (reportByStatusMask=0xFF)...");
            var data = await _udsClient.ReadDtcInformationAsync(0x02, 0xFF).ConfigureAwait(false);

            Dtcs.Clear();
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                var code   = (uint)((data[i] << 16) | (data[i + 1] << 8) | data[i + 2]);
                var status = data[i + 3];
                Dtcs.Add(new DtcRow { Code = code, Status = status, Description = GetDtcDescription(code) });
            }

            AppendLog("Info", $"Found {Dtcs.Count} DTCs");
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Read DTCs failed: NRC 0x{(byte)ex.ResponseCode:X2}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Read DTCs error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ClearDtcsAsync()
    {
        try
        {
            AppendLog("Info", "Clearing all DTCs...");
            await _udsClient.ClearDiagnosticInformationAsync().ConfigureAwait(false);
            Dtcs.Clear();
            AppendLog("Info", "All DTCs cleared");
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Clear DTCs failed: NRC 0x{(byte)ex.ResponseCode:X2}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Clear DTCs error: {ex.Message}");
        }
    }

    private static string GetDtcDescription(uint dtc) => dtc switch
    {
        <= 0x00FFFF => "Powertrain",
        <= 0x01FFFF => "Chassis",
        <= 0x02FFFF => "Body",
        <= 0x03FFFF => "Network",
        _           => "Unknown"
    };

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine($"{DateTime.Now:HH:mm:ss.fff}", level, message));
}
