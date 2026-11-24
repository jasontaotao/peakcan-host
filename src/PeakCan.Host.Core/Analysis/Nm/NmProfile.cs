namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>NM profile 的字段来源。位标志，可同时来自多个来源。</summary>
[Flags]
public enum NmProfileSource
{
    None = 0,
    Dbc = 1,
    Sidecar = 2,
    Session = 4,
    Overrides = 8,
}

/// <summary>CAN ID 与节点站地址的映射方式。</summary>
public enum NmIdMappingMode
{
    Auto,
    BasePlusNode,
    LowByteSourceAddress,
    SharedId,
    Explicit,
}

/// <summary>M1 只需要下列能力；OemRules 在 M2 才会可用。</summary>
public enum NmCapability
{
    IdentifyMessages,
    Timing,
    Activity,
    RawContent,
    SignalDecode,
    NidCheck,
    CbvDecode,
    PnCheck,
    UserData,
    OemRules,
}

public sealed record NmTimings(
    int? CycleMs = null,
    int? TimeoutMs = null,
    int? RepeatMessageMs = null,
    int? WaitBusSleepMs = null,
    int? ImmediateCycleMs = null,
    int? ImmediateCount = null);

public sealed record NmLayout(
    byte? NidByte = null,
    byte? CbvByte = null,
    byte? UserDataOffset = null,
    int? UserDataLength = null);

public sealed record NmFeatures(
    bool? ActiveWakeupBitEnabled = null,
    bool? RepeatMsgIndEnabled = null,
    bool? PnEnabled = null);

public sealed record NmNodeDef(
    string Name,
    byte? StationAddress = null,
    byte? NodeIdentifier = null,
    bool IsNmNode = false);

/// <summary>DBC 内两个来源给出不同值；不静默择一。</summary>
public sealed record NmConfigConflict(string Field, string[] Values, string[] Sources);

public sealed record NmProvenance(
    string? ProfilePath = null,
    string? ProfileHash = null,
    string ResolvedFrom = "dbc");

/// <summary>一条报文（M1 的 NM 识别结果 + 周期/超时基线）。</summary>
public sealed record NmMessageDef(
    string CanIdHex,
    bool IsExtended,
    string? Name,
    string? Sender,
    byte Dlc,
    bool IsNm,
    string[] Reasons,
    int? CycleMs = null,
    int? TimeoutMs = null,
    bool MarkerAuthoritative = false)
{
    /// <summary>
    /// 面向用户的报文标识：有名字时是「名字 (CAN ID)」，没名字时退化成纯 CAN ID。
    /// <para>
    /// <b>为什么两样都要</b>：排查现场时人脑子里想的是「BodyCan 的 NM 报文」，
    /// 不是「0x18FFFA27」；但只显示名字又会丢信息——DBC 可能没给名字
    /// （<see cref="Name"/> 为 null），不同网段也可能重名，而 ID 是稳定的唯一锚点。
    /// </para>
    /// <para>
    /// <b>为什么是计算属性而不是构造参数</b>：它不携带新事实，只是展示格式；
    /// 加进 record 主构造器会改变 JSON 形状与 canonical hash 的输入面。
    /// </para>
    /// </summary>
    public string DisplayName => NmDisplayName.Of(CanIdHex, Name);
}

public sealed record NmProfile(
    NmProfileSource Source,
    IReadOnlyList<NmMessageDef> Messages,
    NmTimings Timings,
    IReadOnlyList<NmNodeDef> Nodes,
    IReadOnlyList<NmConfigConflict> Conflicts,
    IReadOnlyList<string> MissingFields,
    NmProvenance Provenance,
    NmIdMappingMode? IdMapping = null,
    NmLayout? Layout = null,
    NmFeatures? Features = null,
    // 企标 profile 的 timing 段（展平后）；null 表示未选企标，走 M1 默认值。
    NmStandardTimings? StandardTimings = null,
    // 展平后的企标位角色；null 表示未选企标（M1 行为）。
    IReadOnlyList<NmBitRole>? StandardBitRoles = null,
    // 展平后的企标规则；null 表示未选企标（不跑规则引擎）。
    IReadOnlyList<NmRuleDef>? StandardRules = null,
    // 企标声明的布局/开关；只在项目层缺该字段时经 NmProfilePrecedence 补位。
    // 绑定阶段不合并进 Layout/Features（spec §6.1：项目 > OEM > base，只实现一次）。
    NmStandardLayout? StandardLayout = null,
    NmStandardFeatures? StandardFeatures = null,
    // 展平链的加载期提示（spec §5.3：旧键 phase 改名）。进报告 Assumptions，
    // 也参与缓存 key——它依赖 YAML 键拼写而非语义，不哈希就会命中旧报告。
    IReadOnlyList<string>? StandardLoadAssumptions = null,
    // 展平链声明的「已知未被检查的条款/半边」（spec §9.5）。
    // null = 未声明；进 NmClauseCoverage 把 Covered 降为 Partial，也参与缓存 key。
    IReadOnlyList<NmUncoveredClauseDef>? StandardUncoveredClauses = null);

/// <summary>用户在补全对话框里填的内容；序列化为 <c>*.nmprofile.json</c>。</summary>
public sealed record NmUserSupplement(
    IReadOnlyList<string>? NmMessageSelectors = null,
    NmIdMappingMode? IdMapping = null,
    byte? NidByte = null,
    byte? CbvByte = null,
    byte? UserDataOffset = null,
    int? UserDataLength = null,
    NmTimings? Timings = null,
    NmFeatures? Features = null,
    IReadOnlyList<NmNodeDef>? Nodes = null,
    string? DbcPath = null,
    string? DbcHash = null,
    // sidecar 结构版本：将来字段变更时用于迁移。
    int Version = 1,
    // 创建时间：Save 时填充，已有文件保留原值（spec §6.6.6）。
    DateTimeOffset? CreatedAt = null,
    // 生成该 sidecar 时的 profile 结构版本。
    int ProfileVersion = 1);
