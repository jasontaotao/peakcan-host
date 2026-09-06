using System.Collections.Generic;
using PeakCan.Host.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Decouples the WPF App layer from the Infrastructure-layer HilRunnerService.
/// App project references Core but not Infrastructure 鈥?this interface is the bridge.
/// </summary>
public interface IHilRunnerService
{
    Task<TestSuiteResult> RunAsync(
        PeakCan.Host.Core.HIL.HilRunRequest request,
        IProgress<TestProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>鏈€杩戜竴娆?RunAsync 瀹為檯瑙ｆ瀽鐨?DBC 鏂囨。锛涙湭杩愯鎴栨棤 DBC 鏃朵负 null銆?/summary>
    DbcDocument? LastDbcDocument { get; }

    /// <summary>澶氶€氶亾杩愯鍚勯€氶亾鐨?DBC 鏂囨。瀛楀吀锛堟寜 ChannelId锛夛紱鍗曢€氶亾鎴栨湭杩愯鏃朵负 null銆?/summary>
    IReadOnlyDictionary<ChannelId, DbcDocument>? LastPerChannelDbcs { get; }

    /// <summary>鏈 run 瀹為檯浣跨敤鐨?case-log 鐩綍锛圕aptureCaseLogs 鎴愬姛鏃堕潪 null锛夈€?/summary>
    string? LastCaseLogDirectory { get; }
}
