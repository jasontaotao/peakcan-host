using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Core.Xcp.Capability;

/// <summary>A2L 声明侧加载失败种类（<see cref="XcpA2lLoadResult.Failed"/> 的 Kind）。</summary>
public enum XcpA2lLoadFailureKind
{
    /// <summary>Asap2PackageApi.ParseFile 解析不出文档（Value = null）。</summary>
    ParseFailed,

    /// <summary>文档可解析，但没有任何模块携带 XCP IF_DATA。</summary>
    NoXcpIfData,
}

/// <summary>
/// A2L 声明侧加载结果（S3-T2 / spec D4 失败形状定案：显式 Result，不抛裸异常）。
/// <para>
/// 为什么用 Result 而不是异常：调用方是 App VM 层（T3 XcpConnectionPanelViewModel），
/// 解析失败 / 无 XCP IF_DATA 是用户可选错文件的常态路径，失败信息要进
/// ValidationNote 状态区展示，抛 InvalidDataException 穿 UI 是裸异常逃逸。
/// 波特率映射失败不受此约束——见 <see cref="XcpA2lLoader.ResolveBaudRate"/>
///（"按声明值走不猜"是硬停条件，不是可恢复状态，仍抛 NotSupportedException）。
/// </para>
/// </summary>
public abstract record XcpA2lLoadResult
{
    private XcpA2lLoadResult()
    {
    }

    /// <summary>加载成功：文档 + 第一个带 XCP IF_DATA 的模块 + 跨块检查 + 解析期合同，一次建齐。</summary>
    /// <param name="Document">整份 A2L 文档（Contracts 构建输入，CLI 事实清单也读它）。</param>
    /// <param name="IfData">第一个非空 IfDataXcp（XCP 声明侧来源，probe/A2L 对账共用）。</param>
    /// <param name="ValidationNotes">Asap2PackageApi.CollectCrossChecks 结果（与包 API 同源）。</param>
    /// <param name="Contracts">Asap2PackageApi.Contracts（解析期一次建好，收包线程只查不现建）。</param>
    public sealed record Loaded(
        A2lDocument Document,
        XcpIfData IfData,
        IReadOnlyList<ValidationNote> ValidationNotes,
        ContractSet Contracts) : XcpA2lLoadResult;

    /// <summary>加载失败：种类 + 面向人的消息（可进 VM 状态区）。</summary>
    public sealed record Failed(XcpA2lLoadFailureKind Kind, string Message) : XcpA2lLoadResult;
}

/// <summary>
/// A2L 声明侧加载服务（S3-T2 / spec D4 下沉）：把 xcp-probe 的
/// ParseDeclaration / ResolveDeclaredBaudRate 逻辑收拢为 Core 公开 API，
/// CLI probe 与 App VM（T3）同源消费，杜绝 App/CLI 双份解析路径漂移
///（超时策略在 S2 就吃过单源教训）。
/// <para>
/// 包 API（Asap2PackageApi）零变更：Load 一次完成 parse + CollectCrossChecks +
/// IF_DATA 提取 + ContractSet 构建，全链路只读包侧既有入口。
/// </para>
/// </summary>
public static class XcpA2lLoader
{
    /// <summary>
    /// 加载 A2L 声明侧：parse + 跨块检查 + 第一个带 XCP IF_DATA 的模块 +
    /// 解析期合同一次建齐。解析失败 / 无 XCP IF_DATA 返回
    /// <see cref="XcpA2lLoadResult.Failed"/>，不抛异常（失败形状见类型注释）。
    /// </summary>
    public static XcpA2lLoadResult Load(string a2lPath)
    {
        var parsed = Asap2PackageApi.ParseFile(a2lPath);
        if (parsed.Value is not { } document)
            return new XcpA2lLoadResult.Failed(
                XcpA2lLoadFailureKind.ParseFailed, $"A2L parse failed: {a2lPath}");

        var ifData = document.Modules.Select(m => m.IfDataXcp).FirstOrDefault(x => x is not null);
        if (ifData is null)
            return new XcpA2lLoadResult.Failed(
                XcpA2lLoadFailureKind.NoXcpIfData, $"A2L has no XCP IF_DATA: {a2lPath}");

        return new XcpA2lLoadResult.Loaded(
            document,
            ifData,
            Asap2PackageApi.CollectCrossChecks(document),
            Asap2PackageApi.Contracts(document));
    }

    /// <summary>
    /// A2L 声明的 XCP_ON_CAN.BAUDRATE → 经典 CAN 预设（真机连接路径用）。
    /// 按声明值走不猜：映射不出 = <see cref="NotSupportedException"/>（宁可不采不错采）；
    /// 预设表照抄 XcpProbeCommand.ResolveDeclaredBaudRate 原有 switch，禁止新增预设。
    /// </summary>
    public static BaudRate ResolveBaudRate(XcpIfData ifData)
    {
        if (ifData.OnCan.Count == 0)
            throw new NotSupportedException(
                "A2L has no XCP_ON_CAN block — refusing to guess baud rate.");

        var declaredBaudrate = ifData.OnCan[0].Baudrate;
        return declaredBaudrate switch
        {
            125000 => BaudRate.Can125kbps,
            250000 => BaudRate.Can250kbps,
            500000 => BaudRate.Can500kbps,
            1000000 => BaudRate.Can1Mbps,
            _ => throw new NotSupportedException(
                $"XCP_ON_CAN.BAUDRATE {declaredBaudrate} has no classic CAN preset — refusing to guess."),
        };
    }
}


