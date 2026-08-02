using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>
/// ECU 脚本表单模型（文件视角 canIds）。加载经 EcuScriptLoader 后反交换 CanIds,
/// 保存经 ToJson 序列化文件视角——绝不把内存模型再喂 EcuScriptLoader.Parse（约束 #1）。
/// </summary>
public sealed partial class EditableEcuScript : EditableEcuNode
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _requestIdHex = "0x7E0";      // 文件视角
    [ObservableProperty] private string _responseIdHex = "0x7E8";     // 文件视角
    [ObservableProperty] private bool _isExtendedFrame;
    [ObservableProperty] private string _initialState = "default";

    public ObservableCollection<EditableEcuState> States { get; } = new();
    public ObservableCollection<EditableDidValue> DidValues { get; } = new();

    public event Action? Changed;

    public EditableEcuScript()
    {
        Notify = () => Changed?.Invoke();
        HookCollection(States);
        HookCollection(DidValues);
    }

    public static EditableEcuScript FromEcuScript(EcuScript script)
    {
        var e = new EditableEcuScript
        {
            Name = script.Name,
            RequestIdHex = Hex(script.CanIds.ResponseId, script.CanIds.IsExtendedFrame),   // 反交换
            ResponseIdHex = Hex(script.CanIds.RequestId, script.CanIds.IsExtendedFrame),
            IsExtendedFrame = script.CanIds.IsExtendedFrame,
            InitialState = script.InitialState,
        };
        foreach (var group in script.StateMachine.Transitions.GroupBy(t => t.FromState ?? "wildcard"))
            e.States.Add(EditableEcuState.FromTransitions(group.Key, group, e));
        if (script.DidValues is { } dv)
            foreach (var (k, v) in dv)
                e.DidValues.Add(EditableDidValue.From(k, v, e));
        return e;
    }

    internal static string Hex(uint value, bool extended)
        => extended ? $"0x{value:X8}" : $"0x{value:X3}";

    internal static string ToHex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    /// <summary>空格分隔 hex 串 → byte[]；空/空白 → null。</summary>
    internal static byte[]? ParseHexBytes(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            bytes[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    /// <summary>序列化文件视角 JSON（约束 #1/#2: 不经 EcuScriptLoader.Parse, response 走 $type）。</summary>
    public string ToJson()
    {
        var script = new
        {
            name = Name,
            initialState = InitialState,
            canIds = new
            {
                requestId = RequestIdHex,
                responseId = ResponseIdHex,
                isExtendedFrame = IsExtendedFrame,
            },
            didValues = DidValues.Count > 0
                ? DidValues.ToDictionary(d => d.KeyHex, d => ParseHexBytes(d.BytesHex))
                : null,
            states = States.Select(s => new
            {
                name = s.Name,
                transitions = s.Transitions.Select(t => t.ToTransitionObject()).ToList(),
            }),
        };
        return System.Text.Json.JsonSerializer.Serialize(script, PeakCan.Host.Core.HIL.Serialization.HILJsonOptions.Default);
    }
}
