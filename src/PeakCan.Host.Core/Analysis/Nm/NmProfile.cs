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
    bool MarkerAuthoritative = false);

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
    NmFeatures? Features = null);

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
