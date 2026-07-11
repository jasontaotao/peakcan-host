using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SendViewModel
{
    // Flow B: Cyclic (v1.2.11 PATCH Item 3 + earlier).
    // Methods moved verbatim from SendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - StartCyclic/StopCyclic -> _cyclic (state, main)
    //   - StartCyclic uses BuildFlags + ParseHex helpers (stays in main)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    // v1.2.11 PATCH Item 3: cyclic-send commands exposed to SendView.xaml.

    [RelayCommand]
    private void StartCyclic()
    {
        if (!uint.TryParse(IdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            Status = $"Invalid ID: {IdText}";
            return;
        }
        if (!int.TryParse(CyclicIntervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            || ms < 1 || ms > 60_000)
        {
            Status = $"Invalid interval: {CyclicIntervalText} (must be 1..60000 ms)";
            return;
        }
        if (IsRtr && IsFd)
        {
            Status = "RTR is not valid for CAN FD (classic CAN only)";
            return;
        }
        var bytes = ParseHex(DataText);
        var canId = new CanId(raw, IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var frame = new CanFrame(canId, bytes, BuildFlags(), ChannelId.None, default);
        _cyclic.Start(frame, TimeSpan.FromMilliseconds(ms));
        IsCyclicRunning = _cyclic.IsRunning;
        Status = $"Cyclic started: every {ms} ms";
    }

    [RelayCommand]
    private void StopCyclic()
    {
        _cyclic.Stop();
        IsCyclicRunning = _cyclic.IsRunning;
        Status = $"Cyclic stopped ({CyclicSuccessCount} ok / {CyclicFailureCount} fail)";
    }
}