using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.Core.Uds;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 通讯参数面板的**可编辑**部分：诊断 CAN ID 与时序参数的表单、校验、应用/重置。
/// <para>
/// <b>为什么全用 string 属性：</b>刷写面板那套 <c>StringFormat={}{0:X3}</c> + <c>uint</c>
/// 绑定有缺陷——<c>StringFormat</c> 只影响显示方向，ConvertBack 时 <c>uint</c> 的默认
/// TypeConverter 按**十进制**解析，用户输入 "7E0" 会绑定失败（必须输 "2016"）。
/// 所以这里用 string + 显式 <c>NumberStyles.HexNumber</c>。
/// </para>
/// <para>
/// <b>三个动作刻意分开</b>（不能混成一个「重置」）：
/// <list type="bullet">
/// <item><see cref="ApplyCommand"/> = 校验 + 仲裁 + 重配 + 落盘</item>
/// <item><see cref="ReloadCommand"/> = 丢弃未保存的编辑，回到已保存的配置</item>
/// <item><see cref="RestoreDefaultsCommand"/> = 清用户配置回内置默认（需再点应用）</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class TransportParamsViewModel
{
    private UdsTransportSettingsStore? _settingsStore;

    // —— 可编辑表单 ——
    [ObservableProperty] private string _editRequestId = "7E0";
    [ObservableProperty] private string _editResponseId = "7E8";
    [ObservableProperty] private string _editFunctionalId = "";
    [ObservableProperty] private bool _editIsExtended;
    [ObservableProperty] private string _editP2Ms = "50";
    [ObservableProperty] private string _editP2StarMs = "5000";
    [ObservableProperty] private string _editNBsMs = "1000";
    [ObservableProperty] private string _editNCrMs = "1000";

    /// <summary>应用结果提示（成功/失败原因）。空 = 未操作过。</summary>
    [ObservableProperty] private string _editStatus = "";

    /// <summary>有未保存的编辑时禁用「重新载入」以外的误导性提示。</summary>
    [ObservableProperty] private bool _hasUnsavedEdits;

    /// <summary>
    /// 接上持久化存储。DI 注册时调用；测试可注入临时路径的 store。
    /// </summary>
    public void AttachSettingsStore(UdsTransportSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _settingsStore = store;
        LoadFormFrom(store.Current);
    }

    /// <summary>把表单填成给定配置（不改存储）。</summary>
    private void LoadFormFrom(UdsTransportSettings settings)
    {
        EditRequestId = settings.RequestId.ToString("X", CultureInfo.InvariantCulture);
        EditResponseId = settings.ResponseId.ToString("X", CultureInfo.InvariantCulture);
        EditFunctionalId = settings.FunctionalId?.ToString("X", CultureInfo.InvariantCulture) ?? "";
        EditIsExtended = settings.IsExtendedFrame;
        EditP2Ms = settings.P2Ms.ToString(CultureInfo.InvariantCulture);
        EditP2StarMs = settings.P2StarMs.ToString(CultureInfo.InvariantCulture);
        EditNBsMs = settings.NBsMs.ToString(CultureInfo.InvariantCulture);
        EditNCrMs = settings.NCrMs.ToString(CultureInfo.InvariantCulture);
        HasUnsavedEdits = false;
        EditStatus = "";
    }

    /// <summary>丢弃未保存的编辑，回到已保存的配置。</summary>
    [RelayCommand]
    private void Reload()
    {
        if (_settingsStore is null) return;
        LoadFormFrom(_settingsStore.Current);
    }

    /// <summary>清用户配置回内置默认（只填表单，不落盘——需要再点应用）。</summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        LoadFormFrom(UdsTransportSettings.Default);
        HasUnsavedEdits = true;
        EditStatus = "已填入内置默认值，点「应用」后生效。";
    }

    /// <summary>
    /// 校验 + 仲裁 + 重配 + 落盘。
    /// <para>
    /// 顺序有意为之：**先校验**（不合法就不动任何状态）→ **再仲裁重配**（忙时拒绝，
    /// 不排队）→ **最后落盘**（重配成功才持久化；否则会出现「磁盘说新地址、运行时
    /// 还是旧地址」的分叉）。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void Apply()
    {
        if (_diagnosticTransport is null || _diagnosticClient is null || _settingsStore is null)
        {
            EditStatus = "当前不可编辑（面板未接上诊断栈）。";
            return;
        }

        if (!TryParseForm(out var settings, out var parseError))
        {
            EditStatus = parseError;
            return;
        }

        // 仲裁 + 重配：忙时拒绝，让用户稍后再试，而不是把 ID 换到一半。
        var config = new PeakCan.HIL.Core.Uds.IsoTp.CanIdConfig
        {
            RequestId = settings.RequestId,
            ResponseId = settings.ResponseId,
            FunctionalId = settings.FunctionalId,
            IsExtendedFrame = settings.IsExtendedFrame,
        };

        // 只有寻址真的变了才走重配。重配会清掉会话/安全/ISO-TP 状态——用户只改
        // 时序时不该被顺手踢回默认会话（ECU 其实还在编程会话里）。
        var addressingChanged =
            config.RequestId != _diagnosticTransport.Config.RequestId
            || config.ResponseId != _diagnosticTransport.Config.ResponseId
            || config.FunctionalId != _diagnosticTransport.Config.FunctionalId
            || config.IsExtendedFrame != _diagnosticTransport.Config.IsExtendedFrame;

        if (addressingChanged
            && !_diagnosticClient.TryApplyTransportConfig(config, out var applyError))
        {
            EditStatus = applyError ?? "应用失败。";
            return;
        }

        // ISO-TP 的 N_Bs / N_Cr 不在 CanIdConfig 里，单独设。
        _diagnosticTransport.FlowControlTimeout = TimeSpan.FromMilliseconds(settings.NBsMs);
        _diagnosticTransport.ReceiveTimeout = TimeSpan.FromMilliseconds(settings.NCrMs);

        // 时序基线：必须写进 UdsClient（它会同时更新真正生效的 _timer 与展示用的
        // Session）。只写 Session 会让界面显示新值、实际仍按旧值超时。
        _diagnosticClient.ApplyTimingBaseline(
            settings.P2Ms, settings.P2StarMs,
            hasUserOverride: settings != UdsTransportSettings.Default);

        if (!_settingsStore.Save(settings))
        {
            // 落盘失败：运行时已经改了，必须如实告诉用户「重启后会丢」。
            EditStatus = "已应用到本次运行，但保存到磁盘失败——重启后会恢复旧配置。";
            HasUnsavedEdits = false;
            return;
        }

        HasUnsavedEdits = false;
        EditStatus = "已应用并保存。";
        Poll();
    }

    /// <summary>把表单解析成配置；失败时给出**具体**原因，不要笼统的「格式错误」。</summary>
    private bool TryParseForm(out UdsTransportSettings settings, out string error)
    {
        settings = UdsTransportSettings.Default;
        error = "";

        if (!TryParseHex(EditRequestId, out var requestId))
        {
            error = "请求 ID 不是合法的十六进制值。";
            return false;
        }
        if (!TryParseHex(EditResponseId, out var responseId))
        {
            error = "响应 ID 不是合法的十六进制值。";
            return false;
        }

        uint? functionalId = null;
        if (!string.IsNullOrWhiteSpace(EditFunctionalId))
        {
            if (!TryParseHex(EditFunctionalId, out var fid))
            {
                error = "功能寻址 ID 不是合法的十六进制值（留空表示不使用功能寻址）。";
                return false;
            }
            functionalId = fid;
        }

        // 请求 ID 与响应 ID 相同是明确错误：同一 ID 收发会让自己的请求被当成响应。
        if (requestId == responseId)
        {
            error = "请求 ID 与响应 ID 不能相同。";
            return false;
        }

        if (!TryParsePositiveInt(EditP2Ms, out var p2))
        {
            error = "P2 必须是正整数（毫秒）。";
            return false;
        }
        if (!TryParsePositiveInt(EditP2StarMs, out var p2Star))
        {
            error = "P2* 必须是正整数（毫秒）。";
            return false;
        }
        if (!TryParsePositiveInt(EditNBsMs, out var nBs))
        {
            error = "N_Bs 必须是正整数（毫秒）。";
            return false;
        }
        if (!TryParsePositiveInt(EditNCrMs, out var nCr))
        {
            error = "N_Cr 必须是正整数（毫秒）。";
            return false;
        }

        // CAN ID 范围由 CanId 构造函数校验（标准帧 ≤0x7FF、扩展帧 ≤0x1FFFFFFF），
        // 这里只做形状检查，范围错误留给 TryApplyTransportConfig 报出更准确的文案。

        settings = new UdsTransportSettings(
            requestId, responseId, functionalId, EditIsExtended, p2, p2Star, nBs, nCr);
        return true;
    }

    /// <summary>十六进制解析：容忍 <c>0x</c> 前缀与空白。</summary>
    internal static bool TryParseHex(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim();
        return uint.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParsePositiveInt(string? text, out int value)
    {
        value = 0;
        return int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
               && value > 0;
    }

    /// <summary>任一编辑属性变化时标记「有未保存编辑」。</summary>
    partial void OnEditRequestIdChanged(string value) => MarkDirty();
    partial void OnEditResponseIdChanged(string value) => MarkDirty();
    partial void OnEditFunctionalIdChanged(string value) => MarkDirty();
    partial void OnEditIsExtendedChanged(bool value) => MarkDirty();
    partial void OnEditP2MsChanged(string value) => MarkDirty();
    partial void OnEditP2StarMsChanged(string value) => MarkDirty();
    partial void OnEditNBsMsChanged(string value) => MarkDirty();
    partial void OnEditNCrMsChanged(string value) => MarkDirty();

    private void MarkDirty() => HasUnsavedEdits = true;
}
