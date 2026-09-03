using System.Runtime.InteropServices;
using System.Text;
using PeakCan.Tools.PromptCacheProbe;

// ============================================================================
// prompt-cache-probe: 验证 DeepSeek 上下文自动缓存是否在"稳定前缀"上命中。
//
// 背景: 产品侧 ChatFlow 每轮全量重发 system + 全部历史, 客户端没有任何
// cache 标记。命中与否完全取决于服务端策略 (DeepSeek 上下文硬盘缓存自动
// 按前缀匹配, 命中的 prompt 按 1/10 计费)。但 NuGet 包 PeakCan.HIL.Core
// 的 ChatCompletionUsageDto 只保留 prompt/completion tokens, 把
// prompt_cache_hit_tokens / prompt_cache_miss_tokens 静默丢弃且 ChatUpdate
// 不透出 usage —— 产品代码无法观测命中。本工具直连 API 重新解析 usage,
// 验证旧消息前缀实际能摊掉多少成本。
//
// 用法:
//   prompt-cache-probe                    # 从 Windows 凭据管理器读 key
//   prompt-cache-probe --key sk-xxx       # 显式传 key (不读凭据管理器)
//   prompt-cache-probe --api-base https://api.deepseek.com/v1 --model deepseek-chat
// ============================================================================

var probeArgs = Args.Parse(args);

// --- 解析 key: --key 参数 > 环境变量 > Windows 凭据管理器 ---
string? apiKey = probeArgs.Key;
string? keySource = null;
string? credError = null;
string? foundCredentialKey = null;
if (string.IsNullOrEmpty(apiKey))
    apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    // 依次尝试候选凭据名: 设置面板保存用 "PeakCan/{provider}/{alias}",
    // WindowsCredentialManagerStore 读时强制加 "peakcan-host:" 前缀;
    // 老版本用 "peakcan-host:deepseek-api-key"。哪个命中用哪个。
    foreach (var candidate in probeArgs.CredentialKeys)
    {
        var candidateValue = CredentialReader.TryRead(candidate, out _);
        if (!string.IsNullOrEmpty(candidateValue))
        {
            apiKey = candidateValue;
            foundCredentialKey = candidate;
            break;
        }
    }
    keySource = foundCredentialKey is null ? null : "Windows 凭据管理器";
    credError = foundCredentialKey is null
        ? $"候选凭据 {string.Join(", ", probeArgs.CredentialKeys)} 均不存在"
        : null;
}
else if (probeArgs.Key is null)
{
    keySource = "环境变量 DEEPSEEK_API_KEY";
}
else
{
    keySource = "--key 参数";
}
if (string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine($"无法获取 API Key。用 --key 显式传入, 或设置环境变量 DEEPSEEK_API_KEY。{credError}");
    return 1;
}

Console.WriteLine($"== Prompt Cache Probe ==");
Console.WriteLine($"  API base : {probeArgs.ApiBase}");
Console.WriteLine($"  model    : {probeArgs.Model}");
Console.WriteLine($"  rounds   : {probeArgs.Rounds}");
Console.WriteLine($"  key 来源 : {keySource} ({(foundCredentialKey ?? "n/a")})");
Console.WriteLine();

// --- 构造固定 system prompt + 递增历史 ---
// 用与产品 BuildSystemMessage 相同骨架的"冻结快照" (固定时间戳/固定状态),
// 保证前缀在轮间字节级稳定 —— 这正是缓存命中的前提。
var systemPrompt = BuildFixedSystemPrompt();
var turns = Enumerable.Range(1, probeArgs.Rounds)
    .Select(i => new ProbeTurn(
        User: $"这是第 {i} 条用户消息，请帮我分析当前的信号数据。",
        Assistant: $"这是第 {i} 条助手回复，我注意到信号发生了变化。"))
    .ToArray();

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
var runner = new ProbeRunner(http, probeArgs.ApiBase, probeArgs.Model, apiKey, systemPrompt);

var pricing = new LlmPricing(HitPerMillion: probeArgs.HitPricePerM, MissPerMillion: probeArgs.MissPricePerM);
var rounds = await runner.SendGrowingAsync(turns, CancellationToken.None);

// --- 输出对比表 ---
// 对齐列是块对齐理论值: DeepSeek 命中按 64-token 完整块统计, 尾部余数
// (<64t) 即使与历史字节一致也记为 miss。hit 恒等于/接近该值时, 说明
// 新增轮次内容不足 1 整块, 命中数"看起来没涨"——并非缓存封顶 (见
// CacheBlockAlignment 说明与实测)。
Console.WriteLine($"{"Round",-6}{"msgs",-6}{"prompt",-9}{"hit",-9}{"miss",-9}{"对齐值",-9}{"hit%",-7}{"成本(无缓存)",-12}{"成本(有缓存)",-12}{"节省"}");
Console.WriteLine(new string('-', 90));
decimal totalWithout = 0, totalWith = 0;
foreach (var r in rounds)
{
    var without = CostCalculator.CostWithoutCache(r.Usage, pricing);
    var with = CostCalculator.CostWithCache(r.Usage, pricing);
    totalWithout += without;
    totalWith += with;
    var align = CacheBlockAlignment.MaxAlignableTokens(r.Usage.PromptTokens);
    var aligned = r.Usage.PromptCacheHitTokens >= align ? "✓" : "";
    Console.WriteLine(
        $"{r.Round,-6}{r.MessageCount,-6}{r.Usage.PromptTokens,-9}{r.Usage.PromptCacheHitTokens,-9}" +
        $"{r.Usage.PromptCacheMissTokens,-9}{align,-9}{r.Usage.HitRatio,6:P1} {aligned} " +
        $"{without,10:C4}  {with,10:C4}  {CostCalculator.Savings(r.Usage, pricing),10:C4}");
}
Console.WriteLine(new string('-', 90));
var totalSaved = totalWithout - totalWith;
var savedPct = totalWithout == 0 ? 0 : totalSaved / totalWithout;
Console.WriteLine($"合计     : 无缓存 {totalWithout:C4} | 有缓存 {totalWith:C4} | 节省 {totalSaved:C4} ({savedPct:P1})");
Console.WriteLine();
Console.WriteLine($"缓存命中占比(末轮): {rounds[^1].Usage.HitRatio:P1} — 说明稳定前缀的服务端自动缓存命中情况。");
Console.WriteLine("注: 对齐值 = prompt 向下对齐到 64-token 块; hit 达标(✓)说明未命中部分仅限尾部余块");
Console.WriteLine("    (第 1 轮因前缀未缓存可能不达标; 第 2 轮起未命中仅余块 → ✓ 正常), 新增轮次内容");
Console.WriteLine("    不足 1 整块时 hit 数字不变属正常, 非缓存封顶。");
return 0;

/// <summary>模拟产品 BuildSystemMessage 的冻结快照 —— 固定状态、字节级稳定,
/// 验证服务端自动缓存在理想前缀下的命中率。</summary>
static string BuildFixedSystemPrompt()
{
    var sb = new StringBuilder();
    sb.AppendLine("你是一个汽车 CAN 总线故障诊断专家。");
    sb.AppendLine();
    sb.AppendLine("当前 trace 状态:");
    sb.AppendLine("- 绿锚: 158340.5101");
    sb.AppendLine("- 蓝锚: 158340.5200");
    sb.AppendLine("- watch list: 12 条信号");
    sb.AppendLine("- DBC: pure_electric_v4.6.dbc");
    sb.AppendLine("- DBC 节点: VCU, MCU, BMS, OBC, TBOX");
    sb.AppendLine("- 当前播放时间戳: 158340.5101");
    sb.AppendLine("- chart 视口范围: 158340.5000 ~ 158340.5200");
    sb.AppendLine();
    sb.AppendLine("时间格式约定:");
    sb.AppendLine("- 图表 X 轴与工具返回的 *_label 字段统一使用秒数（保留4位小数）");
    sb.AppendLine();
    sb.AppendLine("可用工具（19 个）：");
    sb.AppendLine("发现类: search_signals, get_signal_overview, anomaly_scan");
    sb.AppendLine("查询类: get_dbc_signal, get_dbc_message, find_related_signals");
    sb.AppendLine("操作类: propose_to_watch_list, remove_from_watch_list, seek_to");
    sb.AppendLine("分析类: search_signal_trace, get_anchor_info, analyze_timing_sequence");
    sb.AppendLine("上下文类: get_trace_info, get_dbc_info");
    sb.AppendLine("组织类: create_group, add_to_group, remove_from_group, set_group_notes, set_signal_alias");
    sb.AppendLine();
    sb.AppendLine("分析原则:");
    sb.AppendLine("1. 信息不足时问用户，不编造");
    sb.AppendLine("2. 引用数据时给出具体数值");
    sb.AppendLine("3. 发现关联信号时反问用户要不要加 watch list，给明确选择");
    sb.AppendLine("4. propose_to_watch_list 后可同轮调 get_anchor_info 读新值");
    sb.AppendLine("5. 第一轮可直接调 get_anchor_info 读已有 watch list 数据");
    sb.AppendLine("6. 不确定时说不确定");
    return sb.ToString();
}

/// <summary>命令行参数。</summary>
internal sealed record Args(
    string? Key,
    string ApiBase,
    string Model,
    int Rounds,
    decimal HitPricePerM,
    decimal MissPricePerM,
    IReadOnlyList<string> CredentialKeys)
{
    /// <summary>默认候选凭据名, 覆盖产品两条保存路径:
    /// 设置面板保存的 "PeakCan/DeepSeek/default" (无前缀) 与
    /// WindowsCredentialManagerStore 读取时强制加的 "peakcan-host:" 前缀,
    /// 以及 DI 注入小写 "deepseek" 和旧版 "peakcan-host:deepseek-api-key"。</summary>
    private static readonly string[] DefaultCredentialKeys =
    {
        "PeakCan/DeepSeek/default",
        "peakcan-host:PeakCan/DeepSeek/default",
        "PeakCan/deepseek/default",
        "peakcan-host:PeakCan/deepseek/default",
        "peakcan-host:deepseek-api-key",
    };

    public static Args Parse(string[] raw)
    {
        string? key = null, apiBase = "https://api.deepseek.com/v1", model = "deepseek-chat";
        var credentialKeys = new List<string>(DefaultCredentialKeys);
        int rounds = 3;
        decimal hitPrice = 0.2m, missPrice = 2.0m;
        for (int i = 0; i < raw.Length; i++)
        {
            switch (raw[i])
            {
                case "--key" when i + 1 < raw.Length: key = raw[++i]; break;
                case "--api-base" when i + 1 < raw.Length: apiBase = raw[++i]; break;
                case "--model" when i + 1 < raw.Length: model = raw[++i]; break;
                case "--rounds" when i + 1 < raw.Length:
                    if (int.TryParse(raw[++i], out var r)) rounds = r;
                    break;
                case "--hit-price" when i + 1 < raw.Length:
                    if (decimal.TryParse(raw[++i], out var h)) hitPrice = h;
                    break;
                case "--miss-price" when i + 1 < raw.Length:
                    if (decimal.TryParse(raw[++i], out var m)) missPrice = m;
                    break;
                case "--credential-key" when i + 1 < raw.Length:
                    credentialKeys.Insert(0, raw[++i]);
                    break;
            }
        }
        return new Args(key, apiBase, model, rounds, hitPrice, missPrice, credentialKeys);
    }
}

/// <summary>从 Windows 凭据管理器读取 API Key (与产品 WindowsCredentialManagerStore
/// 相同的 CredRead 前缀约定: 保存时 key 会加 "peakcan-host:" 前缀)。</summary>
internal static class CredentialReader
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int ERROR_NOT_FOUND = 1168;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    public static string? TryRead(string fullKey, out string error)
    {
        if (!CredRead(fullKey, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var err = Marshal.GetLastWin32Error();
            error = err == ERROR_NOT_FOUND
                ? $"凭据 '{fullKey}' 不存在 (Windows 凭据管理器)"
                : $"读取凭据失败 (HRESULT 0x{err:X8})";
            return null;
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var len = (int)cred.CredentialBlobSize / 2;
            error = "";
            return Marshal.PtrToStringUni(cred.CredentialBlob, len);
        }
        finally
        {
            CredFree(credPtr);
        }
    }
}
