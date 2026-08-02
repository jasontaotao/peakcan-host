using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

public enum EcuResponseMode { Static, Dynamic }

/// <summary>
/// 可编辑转移。hex 字段用空格分隔串编辑（"FF 00"）; ServiceId/SubFunction 用 "0x22" 式 hex。
/// 响应二选一: Static=固定字节, Dynamic=生成器名（约束 #2, 序列化走 EcuResponse $type）。
/// </summary>
public sealed partial class EditableEcuTransition : EditableEcuNode
{
    [ObservableProperty] private string _serviceIdHex = "0x22";
    [ObservableProperty] private string? _subFunctionHex;
    [ObservableProperty] private string _dataMaskHex = "";
    [ObservableProperty] private string _dataPatternHex = "";
    [ObservableProperty] private EcuResponseMode _responseMode = EcuResponseMode.Static;
    [ObservableProperty] private string _staticDataHex = "";
    [ObservableProperty] private string _generatorName = "";
    [ObservableProperty] private string? _toState;
    [ObservableProperty] private int _responseDelayMs;

    public static EditableEcuTransition FromTransition(EcuStateTransition t, Action? notify)
    {
        var e = new EditableEcuTransition
        {
            Notify = notify,
            ServiceIdHex = $"0x{t.ServiceId:X2}",
            SubFunctionHex = t.SubFunction.HasValue ? $"0x{t.SubFunction.Value:X2}" : null,
            DataMaskHex = t.DataMask is { Length: > 0 } m ? EditableEcuScript.ToHex(m) : "",
            DataPatternHex = t.DataPattern is { Length: > 0 } p ? EditableEcuScript.ToHex(p) : "",
            ToState = t.ToState,
            ResponseDelayMs = t.ResponseDelayMs,
        };
        switch (t.Response)
        {
            case StaticResponse s:
                e.ResponseMode = EcuResponseMode.Static;
                e.StaticDataHex = EditableEcuScript.ToHex(s.Data);
                break;
            case DynamicResponse d:
                e.ResponseMode = EcuResponseMode.Dynamic;
                e.GeneratorName = d.GeneratorName;
                break;
        }
        return e;
    }
}
