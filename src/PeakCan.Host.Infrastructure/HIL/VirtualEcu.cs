using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Reactive ECU simulator: listens to ICanChannel.FrameReceived, matches request frames,
/// generates response frames via ISO-TP reassembly.
/// </summary>
public sealed class VirtualEcu : IDisposable
{
    public static int InstanceCount;

    private readonly ICanChannel _channel;
    private readonly IsoTpLayer _isoTp;
    private readonly List<UdsResponseRule> _rules;
    private readonly ILogger<VirtualEcu>? _logger;
    private readonly CanIdConfig _ecuCanIds;
    private int _disposed;

    public uint RequestId => _ecuCanIds.ResponseId; // ECU listens on HIL's send ID

    public VirtualEcu(ICanChannel channel, CanIdConfig ecuCanIds,
        IEnumerable<UdsResponseRule> rules, ILogger<VirtualEcu>? logger = null)
    {
        _channel = channel;
        _ecuCanIds = ecuCanIds;
        _rules = rules.ToList();
        _logger = logger;

        // ECU-side IsoTpLayer — CanIdConfig already swapped to ECU perspective by EcuScriptLoader
        // IsoTpLayer needs ILogger<IsoTpLayer>, VirtualEcu's logger is ILogger<VirtualEcu>, pass null
        _isoTp = new IsoTpLayer(_ecuCanIds, SendFrameAsync, logger: null);
        _isoTp.MessageReceived += OnUdsRequestReceived;
        _channel.FrameReceived += OnCanFrameReceived;
    }

    private void OnCanFrameReceived(CanFrame frame)
    {
        try { _isoTp.ProcessFrame(frame); }
        catch (ArgumentException ex)
        {
            // Frame filtered out by IsoTpLayer (wrong CAN ID) - normal
        }
    }

    private void OnUdsRequestReceived(byte[] request)
    {
        if (request.Length == 0) return;
        var sid = request[0];

        foreach (var rule in _rules)
        {
            if (rule.TryMatch(request, out var responseData))
            {
                _ = SendUdsResponseAsync(responseData, rule.ResponseDelayMs);
                return;
            }
        }

        // No matching rule -> NRC 0x11 (serviceNotSupported)
        // NRC format (ISO 14229-1 §11.3.2): [0x7F, originalSID, nrc]
        _ = SendUdsResponseAsync(new byte[] { 0x7F, sid, 0x11 }, 0);
    }

    private async Task SendUdsResponseAsync(byte[] data, int delayMs)
    {
        if (delayMs > 0)
            await Task.Delay(delayMs).ConfigureAwait(false);

        await _isoTp.SendMessageAsync(data).ConfigureAwait(false);
    }

    private Task SendFrameAsync(CanFrame frame)
        => _channel.WriteAsync(frame, CancellationToken.None).AsTask();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _channel.FrameReceived -= OnCanFrameReceived;
        _isoTp.MessageReceived -= OnUdsRequestReceived;
        _isoTp.Dispose();
    }
}
