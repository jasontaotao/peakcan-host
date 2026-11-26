using System;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Path;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.HIL.Core.Path;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

/// <summary>
/// 定义文件加载状态：离线报告需要区分“没配置 / 文件没了 / 解析失败 / 加载成功”，
/// 不能把四种情况都当成“内置默认”。
/// </summary>
public class UserJsonLoadStatusTests
{
    // 与 DidDatabaseTests 一致：测试根目录必须在 allowlist 内，
    // 否则 NormalizeRestricted 会抛 PathNormalizationException。
    private static readonly string TestRoot =
        System.IO.Path.Combine(AppContext.BaseDirectory, "TestUserDataStatus");

    private static PathOptions TestOptions => new(new List<string> { TestRoot });

    private static string TempJson(string content)
    {
        var path = System.IO.Path.Combine(TestRoot, $"uds-dids-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(TestRoot);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Not_configured_when_path_is_null()
    {
        var did = new DidDatabase(userJsonPath: null, NullLogger<DidDatabase>.Instance, TestOptions);
        var routine = new RoutineDatabase(userJsonPath: null, NullLogger<RoutineDatabase>.Instance, TestOptions);

        did.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.NotConfigured);
        routine.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.NotConfigured);
    }

    [Fact]
    public void Missing_when_file_absent()
    {
        var path = System.IO.Path.Combine(TestRoot, $"{Guid.NewGuid():N}-missing.json");
        var did = new DidDatabase(path, NullLogger<DidDatabase>.Instance, TestOptions);

        did.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.Missing);
        did.Find(0xF190).Should().NotBeNull("内置 DID 必须仍然可用");
    }

    [Fact]
    public void Failed_when_json_malformed()
    {
        var did = new DidDatabase(TempJson("{ not json"), NullLogger<DidDatabase>.Instance, TestOptions);
        var routine = new RoutineDatabase(TempJson("{ not json"), NullLogger<RoutineDatabase>.Instance, TestOptions);

        did.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.Failed);
        did.Find(0xF190).Should().NotBeNull();
        routine.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.Failed);
        routine.All.Should().BeEmpty();
    }

    [Fact]
    public void Clear_reports_that_json_definitions_no_longer_apply()
    {
        // OdxImportService 在 ODX 导入前调用 Clear()：用户 JSON 条目被移除，
        // 状态不得继续声称 Loaded，否则报告会同时写“ODX 已加载 + JSON 已加载”。
        var did = new DidDatabase(TempJson("{\"dids\":[]}"), NullLogger<DidDatabase>.Instance, TestOptions);
        var routine = new RoutineDatabase(TempJson("{\"routines\":[]}"), NullLogger<RoutineDatabase>.Instance, TestOptions);

        did.Clear();
        routine.Clear();

        did.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.NotConfigured);
        routine.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.NotConfigured);
    }

    [Fact]
    public void Loaded_when_json_valid()
    {
        var did = new DidDatabase(TempJson("{\"dids\":[]}"), NullLogger<DidDatabase>.Instance, TestOptions);
        var routine = new RoutineDatabase(TempJson("{\"routines\":[]}"), NullLogger<RoutineDatabase>.Instance, TestOptions);

        did.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.Loaded);
        routine.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.Loaded);
    }
}
