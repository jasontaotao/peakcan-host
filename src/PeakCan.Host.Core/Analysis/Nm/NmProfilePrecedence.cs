namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// profile 生效字段的唯一优先级实现：项目层（DBC/sidecar）优先，企标补空缺
/// （spec §6.1：项目 &gt; OEM &gt; base）。
/// <para>
/// 绑定阶段（<see cref="NmStandardProfileBinding.Bind"/>）只把企标原样挂到
/// <see cref="NmProfile.StandardLayout"/> / <see cref="NmProfile.StandardFeatures"/>，
/// 不写 <c>??</c> 合并；所有消费方必须经由本类读取，避免优先级规则出现第二份。
/// </para>
/// </summary>
internal static class NmProfilePrecedence
{
    /// <summary>NID 字节：项目层声明优先，否则用企标声明。</summary>
    internal static byte? EffectiveNidByte(NmProfile profile)
        => profile.Layout?.NidByte ?? profile.StandardLayout?.NidByte;

    /// <summary>CBV 字节：项目层声明优先，否则用企标声明。</summary>
    internal static byte? EffectiveCbvByte(NmProfile profile)
        => profile.Layout?.CbvByte ?? profile.StandardLayout?.CbvByte;

    /// <summary>用户数据起始字节：项目层声明优先，否则用企标声明。</summary>
    internal static byte? EffectiveUserDataOffset(NmProfile profile)
        => profile.Layout?.UserDataOffset ?? profile.StandardLayout?.UserDataOffset;

    /// <summary>用户数据长度：项目层声明优先，否则用企标声明。</summary>
    internal static int? EffectiveUserDataLength(NmProfile profile)
        => profile.Layout?.UserDataLength ?? profile.StandardLayout?.UserDataLength;

    /// <summary>PN 开关：项目层声明优先，否则用企标声明。</summary>
    internal static bool? EffectivePnEnabled(NmProfile profile)
        => profile.Features?.PnEnabled ?? profile.StandardFeatures?.PnEnabled;
}
