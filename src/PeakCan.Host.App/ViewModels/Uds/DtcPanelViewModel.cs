using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Panel VM for the DTCs tab. DTCs are read live from the ECU via
/// <see cref="UdsClient.ReadDtcInformationAsync"/>. When an ODX file
/// has been loaded, the imported <see cref="DtcDatabase"/> enriches
/// the description field on each row (canonical ODX text wins over
/// the hard-coded ISO 14294 category mapping).
/// </summary>
public sealed partial class DtcPanelViewModel : ObservableObject, IUdsPanel
{
    private readonly UdsClient _udsClient;
    private readonly DtcDatabase _dtcDatabase;
    private ObservableCollection<UdsLogLine>? _log;

    public ObservableCollection<DtcRow> Dtcs { get; } = new();

    public DtcPanelViewModel(UdsClient udsClient, DtcDatabase dtcDatabase)
    {
        ArgumentNullException.ThrowIfNull(udsClient);
        ArgumentNullException.ThrowIfNull(dtcDatabase);
        _udsClient = udsClient;
        _dtcDatabase = dtcDatabase;
    }

    /// <summary>
    /// Backward-compat ctor for tests + non-DI callers. Constructs an
    /// empty <see cref="DtcDatabase"/> internally.
    /// </summary>
    public DtcPanelViewModel(UdsClient udsClient)
        : this(udsClient, new DtcDatabase()) { }

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
                Dtcs.Add(new DtcRow
                {
                    Code = code,
                    Status = status,
                    Description = LookupDescription(code),
                });
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

    /// <summary>
    /// Refresh DTC panel from the ODX-imported <see cref="DtcDatabase"/>.
    /// Called by <c>UdsViewModel</c> after <see cref="OdxImportViewModel"/>
    /// completes a successful load. ECU-side DTCs are independent —
    /// this only re-populates from the local database; if the database
    /// is empty, the panel becomes empty.
    /// </summary>
    public void RefreshFromDatabase()
    {
        Dtcs.Clear();
        foreach (var d in _dtcDatabase.All)
        {
            Dtcs.Add(new DtcRow
            {
                Code = d.Code,
                Status = d.StatusMask,
                Description = d.Description,
            });
        }
    }

    /// <summary>
    /// Look up ODX description first; fall back to ISO 14294 category
    /// mapping if no database entry exists.
    /// </summary>
    private string LookupDescription(uint code)
    {
        var def = _dtcDatabase.FindByCode(code);
        if (def.HasValue && !string.IsNullOrEmpty(def.Value.Description))
            return def.Value.Description;
        return code switch
        {
            <= 0x00FFFF => "Powertrain",
            <= 0x01FFFF => "Chassis",
            <= 0x02FFFF => "Body",
            <= 0x03FFFF => "Network",
            _           => "Unknown",
        };
    }

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine($"{DateTime.Now:HH:mm:ss.fff}", level, message));
}
