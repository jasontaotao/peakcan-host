using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Ui;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Ui;

/// <summary>
/// 脚本「最近打开」列表。与会话 MRU 分开存是刻意的：会话菜单按会话语义打开文件，
/// 混进脚本路径会让菜单点出错误的行为。
/// </summary>
public sealed class RecentScriptsStoreTests : IDisposable
{
    private readonly string _dir;

    public RecentScriptsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"recentscripts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string NewPath() => Path.Combine(_dir, $"recent-{Guid.NewGuid():N}.json");

    private static RecentScriptsStore NewStore(string path)
        => new(NullLogger<RecentScriptsStore>.Instance, path);

    [Fact]
    public void Missing_File_Loads_Empty()
    {
        NewStore(NewPath()).Load().Should().BeEmpty();
    }

    [Fact]
    public void Added_Paths_Survive_A_New_Instance_Newest_First()
    {
        var path = NewPath();

        NewStore(path).Add(@"C:\scripts\a.js");
        NewStore(path).Add(@"C:\scripts\b.js");

        var loaded = NewStore(path).Load();
        loaded.Select(e => e.Path).Should().Equal(@"C:\scripts\b.js", @"C:\scripts\a.js");
    }

    [Fact]
    public void Re_Adding_An_Existing_Path_Moves_It_To_The_Front_Without_Duplicating()
    {
        var path = NewPath();
        var store = NewStore(path);
        store.Add(@"C:\scripts\a.js");
        store.Add(@"C:\scripts\b.js");

        var updated = store.Add(@"C:\SCRIPTS\A.JS");   // Windows 路径大小写不敏感

        // 注意：FluentAssertions 的 Equal(params string[]) 会把第三个参数当成期望值，
        // because 只能靠 HaveCount/ContainSingle 这类单值断言表达。
        updated.Select(e => e.Path).Should().Equal(@"C:\SCRIPTS\A.JS", @"C:\scripts\b.js");
        updated.Should().HaveCount(2, "重复项必须合并成一条并置顶");
    }

    [Fact]
    public void List_Is_Capped_And_Drops_The_Oldest()
    {
        var store = NewStore(NewPath());
        for (var i = 0; i < RecentScriptsStore.MaxEntries + 3; i++)
            store.Add($@"C:\scripts\s{i}.js");

        var loaded = store.Load();
        loaded.Should().HaveCount(RecentScriptsStore.MaxEntries);
        loaded[0].Path.Should().Be($@"C:\scripts\s{RecentScriptsStore.MaxEntries + 2}.js");
        loaded.Should().NotContain(e => e.Path.EndsWith("s0.js", StringComparison.Ordinal));
    }

    [Fact]
    public void Blank_Path_Is_Ignored()
    {
        var store = NewStore(NewPath());

        store.Add("   ").Should().BeEmpty();
        store.Load().Should().BeEmpty();
    }

    [Fact]
    public void Corrupt_File_Loads_Empty_Without_Throwing()
    {
        var path = NewPath();
        File.WriteAllText(path, "{ not json", new UTF8Encoding(true));

        NewStore(path).Load().Should().BeEmpty();
    }

    [Fact]
    public void Null_Elements_In_The_File_Are_Ignored()
    {
        // JSON 里的 null 元素反序列化成 null（非空标注运行时不生效）。这条路径跑在
        // 启动流程里，解引用崩掉就是"每次开机都崩且文件不变，只能手删 %APPDATA%"。
        var path = NewPath();
        File.WriteAllText(
            path,
            """{"version":"recent-scripts/v1","entries":[null,{"path":"C:\\a.js","openedAtUtcTicks":1}]}""",
            new UTF8Encoding(true));

        var loaded = NewStore(path).Load();

        loaded.Should().ContainSingle();
        loaded[0].Path.Should().Be(@"C:\a.js");
    }

    [Fact]
    public void Oversized_File_Is_Treated_As_Empty()
    {
        var path = NewPath();
        File.WriteAllBytes(path, new byte[RecentScriptsStore.MaxLoadFileBytes + 1]);

        NewStore(path).Load().Should().BeEmpty();
    }

    [Fact]
    public void Clear_Empties_The_List()
    {
        var path = NewPath();
        var store = NewStore(path);
        store.Add(@"C:\scripts\a.js");

        store.Clear().Should().BeTrue();

        store.Load().Should().BeEmpty();
    }
}
