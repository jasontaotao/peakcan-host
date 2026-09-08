using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.Serialization;
using PeakCan.HIL.Core.HIL.Expressions;
using PeakCan.Host.Core.HIL.Analysis;


namespace PeakCan.Host.App.Services.HilPreflight;

public enum PreflightSeverity
{
    Warning,
    Critical,
}

public sealed record PreflightIssue(string Code, string Message, PreflightSeverity Severity);

public sealed record PreflightResult(IReadOnlyList<PreflightIssue> Issues, bool HasCritical);

public sealed record HilPreflightRequest(
    string SuitePath,
    string? DbcPath,
    string? TracePath,
    string? EcuScriptPath,
    string? MatrixPath,
    string? CaseLogDirectory,
    HilMode Mode);

/// <summary>Run 前静态预检：文件、JSON 结构、套件校验器、通道声明与环境引用。</summary>
public sealed class SuitePreflightService(ILogger<SuitePreflightService> logger)
{
    private readonly ILogger<SuitePreflightService> _logger = logger;

    public async Task<PreflightResult> RunAsync(HilPreflightRequest request, CancellationToken ct = default)
    {
        var issues = new List<PreflightIssue>();
        issues.AddRange(CheckFiles(request));
        if (issues.All(i => i.Severity != PreflightSeverity.Critical))
            issues.AddRange(await ValidateSuiteAsync(request, ct));

        return new PreflightResult(issues, issues.Any(i => i.Severity == PreflightSeverity.Critical));
    }

    private static IEnumerable<PreflightIssue> CheckFiles(HilPreflightRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SuitePath))
            yield return Critical("SUITE_MISSING", "Suite 文件未选择。");
        else if (!File.Exists(request.SuitePath))
            yield return Critical("SUITE_NOT_FOUND", $"Suite 文件不存在: {request.SuitePath}");

        if (string.IsNullOrWhiteSpace(request.DbcPath))
            yield return Critical("DBC_MISSING", "DBC 文件未选择。");
        else if (!File.Exists(request.DbcPath))
            yield return Critical("DBC_NOT_FOUND", $"DBC 文件不存在: {request.DbcPath}");

        var missingPath = request.Mode switch
        {
            HilMode.TraceReplay => request.TracePath,
            HilMode.VirtualEcu => request.EcuScriptPath,
            HilMode.Matrix => request.MatrixPath,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(missingPath))
            yield return Critical("MODE_PATH_MISSING", $"{request.Mode} 所需的输入文件未选择。");
        else if (!File.Exists(missingPath))
            yield return Critical("MODE_PATH_NOT_FOUND",
                $"{request.Mode} 文件不存在: {missingPath}");

        if (!string.IsNullOrWhiteSpace(request.CaseLogDirectory))
        {
            var invalid = false;
            try { Directory.CreateDirectory(request.CaseLogDirectory); }
            catch { invalid = true; }
            if (invalid)
                yield return Critical("CASE_LOG_DIR_INVALID",
                    $"Case log 目录不可创建: {request.CaseLogDirectory}");
        }
    }

    private async Task<IReadOnlyList<PreflightIssue>> ValidateSuiteAsync(HilPreflightRequest request, CancellationToken ct)
    {
        var issues = new List<PreflightIssue>();
        TestSuite? suite;
        try
        {
            var json = await File.ReadAllTextAsync(request.SuitePath, ct);
            suite = JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default);
            if (suite is null)
                issues.Add(Critical("SUITE_EMPTY", "Suite 文件内容为空。"));
        }
        catch (JsonException ex)
        {
            issues.Add(Critical("SUITE_JSON_INVALID",
                $"Suite JSON 无效（行 {ex.LineNumber + 1}，列 {ex.BytePositionInLine + 1}）: {ex.Message}"));
            return issues;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HIL preflight failed to read suite");
            issues.Add(Critical("SUITE_READ_FAILED", $"Suite 文件读取失败: {ex.Message}"));
            return issues;
        }

        if (suite is null) return issues;

        issues.AddRange(ValidateChannelDeclarations(suite));
        issues.AddRange(ValidateEnvironmentReferences(suite));
        if (issues.Any(i => i.Severity == PreflightSeverity.Critical)) return issues;

        var registry = new StepValidatorRegistry(new ExpressionEvaluator());
        issues.AddRange(registry.Validate(suite).Select(i => new PreflightIssue(
            $"STEP_{i.RuleId}",
            $"{i.RuleName}: {i.Message}",
            i.Severity == ValidationSeverity.Critical ? PreflightSeverity.Critical : PreflightSeverity.Warning)));

        return issues;
    }

    private static IEnumerable<PreflightIssue> ValidateChannelDeclarations(TestSuite suite)
    {
        if (suite.Channels is not { Count: > 0 } channels) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Name))
            {
                yield return Critical("CHANNEL_NAME_INVALID", "通道声明缺少 name。");
                continue;
            }
            if (!seen.Add(channel.Name))
                yield return Critical("CHANNEL_DUPLICATE", $"通道声明重复: {channel.Name}");
        }
    }

    private static IEnumerable<PreflightIssue> ValidateEnvironmentReferences(TestSuite suite)
    {
        if (suite.Environment is not { Count: > 0 } nodes) yield break;
        var declared = suite.Channels?.Select(c => c.Name).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Channel))
            {
                if ((suite.Channels?.Count ?? 0) > 0)
                    yield return Critical("ENV_CHANNEL_MISSING",
                        $"环境节点 '{node.Name}' 未绑定通道。");
                continue;
            }
            if (!declared.Contains(node.Channel))
                yield return Critical("ENV_CHANNEL_DANGLING",
                    $"环境节点 '{node.Name}' 引用未声明通道 '{node.Channel}'。");
        }
    }

    private static PreflightIssue Critical(string code, string message) =>
        new(code, message, PreflightSeverity.Critical);
}
