using System;
using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Ui;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Ui;

/// <summary>
/// 脚本草稿存储。文件契约与 <see cref="LayoutStateStoreTests"/> 同源，
/// 但写入侧也卡上限——只卡读取会让超大草稿"写得进、读不回"。
/// </summary>
public sealed class ScriptDraftStoreTests : IDisposable
{
    private readonly string _dir;

    public ScriptDraftStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"scriptdraft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string NewPath() => Path.Combine(_dir, $"draft-{Guid.NewGuid():N}.json");

    private static ScriptDraftStore NewStore(string path)
        => new(NullLogger<ScriptDraftStore>.Instance, path);

    [Fact]
    public void Save_Then_Load_RoundTrips_Every_Field()
    {
        var path = NewPath();
        var ticks = new DateTime(2026, 10, 30, 12, 34, 56, DateTimeKind.Utc).Ticks;

        NewStore(path).Save(new ScriptDraftDto("log('hi');", @"C:\scripts\a.js", ticks))
            .Should().BeTrue();

        var loaded = NewStore(path).Load();

        loaded.Should().NotBeNull();
        // 三个字段分别断言：record 的位置参数写反时（Text/Path 互换）整体比较也可能"通过"，
        // 但恢复流程会拿脚本文本当文件名去读。
        loaded!.Text.Should().Be("log('hi');");
        loaded.Path.Should().Be(@"C:\scripts\a.js");
        loaded.SavedAtUtcTicks.Should().Be(ticks);
    }

    [Fact]
    public void Save_Keeps_Chinese_Text_Intact()
    {
        var path = NewPath();
        NewStore(path).Save(new ScriptDraftDto("// 中文注释\nlog('你好');", null, 1))
            .Should().BeTrue();

        NewStore(path).Load()!.Text.Should().Contain("中文注释").And.Contain("你好");
    }

    [Fact]
    public void Missing_File_Loads_Null()
    {
        NewStore(NewPath()).Load().Should().BeNull();
    }

    [Fact]
    public void Corrupt_File_Loads_Null_Without_Throwing()
    {
        var path = NewPath();
        File.WriteAllText(path, "{ this is not json", new UTF8Encoding(true));

        NewStore(path).Load().Should().BeNull();
    }

    [Fact]
    public void Draft_Without_Text_Is_Not_Restored()
    {
        var path = NewPath();
        NewStore(path).Save(new ScriptDraftDto("", @"C:\a.js", 1)).Should().BeTrue();

        NewStore(path).Load().Should().BeNull("空草稿恢复出来只会覆盖用户现有内容");
    }

    [Fact]
    public void Text_Over_The_Character_Cap_Is_Rejected_On_Save()
    {
        var path = NewPath();
        var huge = new string('x', ScriptDraftStore.MaxDraftChars + 1);

        NewStore(path).Save(new ScriptDraftDto(huge, null, 1))
            .Should().BeFalse("超限必须让调用方知道，不能写进去再被读取侧丢掉");

        NewStore(path).Load().Should().BeNull();
    }

    [Fact]
    public void Oversized_File_Is_Treated_As_Absent()
    {
        var path = NewPath();
        File.WriteAllBytes(path, new byte[ScriptDraftStore.MaxLoadFileBytes + 1]);

        NewStore(path).Load().Should().BeNull();
    }

    [Fact]
    public void Clear_Removes_The_Draft_And_Is_Idempotent()
    {
        var path = NewPath();
        var store = NewStore(path);
        store.Save(new ScriptDraftDto("x", null, 1)).Should().BeTrue();

        store.Clear().Should().BeTrue();
        store.Load().Should().BeNull();
        store.Clear().Should().BeTrue("文件已经不存在时再清一次也应当成功");
    }
}
