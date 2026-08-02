using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>一个 DID 值: 键（"0xF190"）+ 字节（空格分隔 hex）。</summary>
public sealed partial class EditableDidValue : EditableEcuNode
{
    [ObservableProperty] private string _keyHex = "";
    [ObservableProperty] private string _bytesHex = "";

    public static EditableDidValue From(ushort key, byte[] bytes, EditableEcuScript owner)
        => new() { Notify = owner.Notify, KeyHex = $"0x{key:X4}", BytesHex = EditableEcuScript.ToHex(bytes) };
}
