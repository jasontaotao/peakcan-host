using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// ViewModel for the UDS diagnostic tab. Provides session management,
/// DID read/write, routine control, and DTC operations.
/// </summary>
public sealed partial class UdsViewModel : ObservableObject
{
    private readonly ILogger<UdsViewModel> _logger;
    private readonly UdsClient _udsClient;

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private string _sessionText = "Default";

    [ObservableProperty]
    private string _securityText = "Not Authenticated";

    [ObservableProperty]
    private ushort _didAddress = 0xF190; // VIN

    [ObservableProperty]
    private string _didValue = "";

    [ObservableProperty]
    private ushort _routineId = 0xFF00;

    [ObservableProperty]
    private string _routineResult = "";

    /// <summary>DTC list from ReadDTCInformation.</summary>
    public ObservableCollection<DtcEntry> DtcList { get; } = new();

    /// <summary>UDS log entries.</summary>
    public ObservableCollection<string> LogEntries { get; } = new();

    public UdsViewModel(ILogger<UdsViewModel> logger, UdsClient udsClient)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(udsClient);

        _logger = logger;
        _udsClient = udsClient;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            StatusText = "Connecting...";
            await _udsClient.DiagnosticSessionControlAsync(0x02); // Extended
            SessionText = "Extended";
            StatusText = "Connected";
            Log("Connected to ECU (Extended Session)");
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            Log($"Connect failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ReadDidAsync()
    {
        try
        {
            Log($"Reading DID 0x{DidAddress:X4}...");
            var data = await _udsClient.ReadDataByIdentifierAsync(DidAddress);
            DidValue = BitConverter.ToString(data).Replace("-", " ");
            Log($"DID 0x{DidAddress:X4} = {DidValue}");
        }
        catch (UdsNegativeResponseException ex)
        {
            Log($"Read DID failed: {ex.ResponseCode}");
        }
        catch (Exception ex)
        {
            Log($"Read DID error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task WriteDidAsync()
    {
        try
        {
            var data = ParseHexString(DidValue);
            Log($"Writing DID 0x{DidAddress:X4}...");
            await _udsClient.WriteDataByIdentifierAsync(DidAddress, data);
            Log($"DID 0x{DidAddress:X4} written successfully");
        }
        catch (UdsNegativeResponseException ex)
        {
            Log($"Write DID failed: {ex.ResponseCode}");
        }
        catch (Exception ex)
        {
            Log($"Write DID error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SecurityAccessAsync()
    {
        try
        {
            Log("Requesting security access...");
            var seed = await _udsClient.SecurityAccessAsync(0x01);
            Log($"Received seed: {BitConverter.ToString(seed)}");

            // TODO: Implement actual key calculation
            var key = new byte[seed.Length]; // Placeholder
            await _udsClient.SecurityAccessAsync(0x01, key);
            SecurityText = "Authenticated (Level 1)";
            Log("Security access granted");
        }
        catch (UdsNegativeResponseException ex)
        {
            Log($"Security access failed: {ex.ResponseCode}");
        }
        catch (Exception ex)
        {
            Log($"Security access error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ReadDtcAsync()
    {
        try
        {
            Log("Reading DTCs...");
            var data = await _udsClient.ReadDtcInformationAsync(0x02, 0xFF);

            DtcList.Clear();
            // Parse DTCs: 3 bytes per DTC + 1 byte status
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                var dtc = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
                var status = data[i + 3];
                DtcList.Add(new DtcEntry
                {
                    Code = $"0x{dtc:X6}",
                    Status = $"0x{status:X2}",
                    Description = GetDtcDescription(dtc)
                });
            }

            Log($"Found {DtcList.Count} DTCs");
        }
        catch (Exception ex)
        {
            Log($"Read DTCs failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ClearDtcAsync()
    {
        try
        {
            Log("Clearing all DTCs...");
            await _udsClient.ClearDiagnosticInformationAsync();
            DtcList.Clear();
            Log("All DTCs cleared");
        }
        catch (Exception ex)
        {
            Log($"Clear DTCs failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RunRoutineAsync()
    {
        try
        {
            Log($"Running routine 0x{RoutineId:X4}...");
            var result = await _udsClient.RoutineControlAsync(0x01, RoutineId);
            RoutineResult = BitConverter.ToString(result).Replace("-", " ");
            Log($"Routine result: {RoutineResult}");
        }
        catch (Exception ex)
        {
            Log($"Routine failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task TesterPresentAsync()
    {
        try
        {
            await _udsClient.TesterPresentAsync();
            Log("TesterPresent OK");
        }
        catch (Exception ex)
        {
            Log($"TesterPresent failed: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogEntries.Add(entry);

        // Keep last 100 entries
        while (LogEntries.Count > 100)
            LogEntries.RemoveAt(0);
    }

    private static byte[] ParseHexString(string hex)
    {
        hex = hex.Replace(" ", "").Replace("-", "");
        if (hex.Length % 2 != 0)
            hex = "0" + hex;

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    private static string GetDtcDescription(int dtc)
    {
        // Simplified DTC description lookup
        return dtc switch
        {
            >= 0x000000 and <= 0x00FFFF => "Powertrain",
            >= 0x010000 and <= 0x01FFFF => "Chassis",
            >= 0x020000 and <= 0x02FFFF => "Body",
            >= 0x030000 and <= 0x03FFFF => "Network",
            _ => "Unknown"
        };
    }
}

/// <summary>DTC entry for display.</summary>
public sealed class DtcEntry
{
    public string Code { get; init; } = "";
    public string Status { get; init; } = "";
    public string Description { get; init; } = "";
}
