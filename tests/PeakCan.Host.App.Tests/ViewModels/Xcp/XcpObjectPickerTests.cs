using System;
using System.Collections.ObjectModel;
using System.Linq;
using A2lEditor.Core;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using FluentAssertions;
using NSubstitute;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels.Xcp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S3-T9 红测：XcpObjectPickerViewModel（spec §3 批量挑变量 + D2 关注集回填）。
/// <para>
/// 口径：树按对象类别分组（来自 ContractSet 条目 Category）；搜索按名称子串
/// 大小写不敏感过滤；确认输出 (name, category) 列表并与
/// <see cref="ITraceSessionService.XcpWatchedObjects"/> 对账去重（已关注对象
/// 默认勾选、二次确认不产生重复行）。VM 层全部可测，窗口壳只消费。
/// </para>
/// </summary>
public class XcpObjectPickerTests
{
    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static A2lDocument Doc()
    {
        var cmIdentical = new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%d", "-",
            new IdenticalConversion(), new LineRange(1, 1));

        A2lMeasurement Meas(string name, ulong addr) =>
            new(name, "d", A2lDataType.UBYTE, "CM_ID", "0", "0", "0", "65535", addr, new LineRange(1, 1));

        var bigTable = new A2lCharacteristic("BigTable", "d", "VAL_BLK", "RL_U8", 0x1010,
            "0", "100", null, "CM_ID", new LineRange(1, 1), MatrixDim: new uint[] { 8, 1, 1 });

        var module = new A2lModule("M", "m",
            new A2lMeasurement[] { Meas("Rpm", 0x1000), Meas("Volt", 0x1001), Meas("Freq", 0x1002) },
            new[] { bigTable }, Array.Empty<A2lAxisPts>(),
            new[] { cmIdentical }, Array.Empty<A2lRecordLayout>(), Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1));
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static A2lDocument EmptyDoc()
    {
        var module = new A2lModule("M", "m",
            Array.Empty<A2lMeasurement>(), Array.Empty<A2lCharacteristic>(), Array.Empty<A2lAxisPts>(),
            Array.Empty<A2lCompuMethod>(), Array.Empty<A2lRecordLayout>(), Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1));
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static ContractSet Contracts() => new(Doc());

    private static ITraceSessionService FakeSession(params XcpWatchRow[] watched)
    {
        var session = Substitute.For<ITraceSessionService>();
        session.XcpWatchedObjects.Returns(new ObservableCollection<XcpWatchRow>(watched));
        return session;
    }

    private static XcpPickerNode? Leaf(XcpObjectPickerViewModel vm, string name) =>
        vm.Roots.SelectMany(r => r.Children).FirstOrDefault(n => n.Name == name);

    // ------------------------------------------------------------------
    // (a) 树按 Category 分组（来自 ContractSet 条目类别）
    // ------------------------------------------------------------------

    [Fact]
    public void BuildTree_GroupsContractsByCategory()
    {
        var vm = new XcpObjectPickerViewModel(Contracts());

        var roots = vm.Roots.ToList();
        roots.Select(r => r.Name).Should().Equal("MEASUREMENT", "CHARACTERISTIC");
        roots.Should().OnlyContain(r => r.IsCategory && !r.IsLeaf);

        var measurement = roots.Single(r => r.Name == "MEASUREMENT");
        measurement.Children.Select(n => n.Name).Should().Equal("Rpm", "Volt", "Freq");
        measurement.Children.Should().OnlyContain(n => n.Category == "MEASUREMENT" && n.IsLeaf);

        var characteristic = roots.Single(r => r.Name == "CHARACTERISTIC");
        characteristic.Children.Select(n => n.Name).Should().Equal("BigTable");
        characteristic.Children.Single().Category.Should().Be("CHARACTERISTIC");
    }

    // ------------------------------------------------------------------
    // (b) 搜索框过滤（名称子串，大小写不敏感）
    // ------------------------------------------------------------------

    [Fact]
    public void SearchText_FiltersByNameCaseInsensitive()
    {
        var vm = new XcpObjectPickerViewModel(Contracts());

        vm.SearchText = "RPM";

        Leaf(vm, "Rpm")!.IsVisible.Should().BeTrue();
        Leaf(vm, "Volt")!.IsVisible.Should().BeFalse();
        Leaf(vm, "Freq")!.IsVisible.Should().BeFalse();
        // 父组有可见子项则保留，便于树结构可导航。
        vm.Roots.Single(r => r.Name == "MEASUREMENT").IsVisible.Should().BeTrue();
        vm.Roots.Single(r => r.Name == "CHARACTERISTIC").IsVisible.Should().BeFalse();

        vm.SearchText = "";

        vm.Roots.SelectMany(r => r.Children).Should().OnlyContain(n => n.IsVisible);
    }

    [Fact]
    public void SearchText_NoMatch_LeavesAllNodesHidden()
    {
        var vm = new XcpObjectPickerViewModel(Contracts());

        vm.SearchText = "zzz-no-match";

        vm.Roots.Should().OnlyContain(r => !r.IsVisible);
        vm.Roots.SelectMany(r => r.Children).Should().OnlyContain(n => !n.IsVisible);
    }

    // ------------------------------------------------------------------
    // (c) 多选回填：确认 → 输出选中 (name, category) 列表
    // ------------------------------------------------------------------

    [Fact]
    public void Confirm_ReturnsSelectedNameCategoryRows()
    {
        var vm = new XcpObjectPickerViewModel(Contracts());
        Leaf(vm, "Rpm")!.IsSelected = true;
        Leaf(vm, "BigTable")!.IsSelected = true;

        var rows = vm.Confirm();

        rows.Should().HaveCount(2);
        rows.Should().Contain(new XcpWatchRow("Rpm", "MEASUREMENT"));
        rows.Should().Contain(new XcpWatchRow("BigTable", "CHARACTERISTIC"));
    }

    [Fact]
    public void Confirm_NoSelection_ReturnsEmpty()
    {
        var vm = new XcpObjectPickerViewModel(Contracts());

        vm.Confirm().Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // (d) 去重：已关注对象默认勾选 / 再次添加不产生重复行
    // ------------------------------------------------------------------

    [Fact]
    public void WatchedObjects_PreselectNodes()
    {
        var session = FakeSession(new XcpWatchRow("Rpm", "MEASUREMENT"));

        var vm = new XcpObjectPickerViewModel(Contracts(), session);

        Leaf(vm, "Rpm")!.IsSelected.Should().BeTrue();
        Leaf(vm, "Volt")!.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void Confirm_AppendsOnlyMissingRowsToWatchedObjects()
    {
        var session = FakeSession(new XcpWatchRow("Rpm", "MEASUREMENT"));
        var vm = new XcpObjectPickerViewModel(Contracts(), session);
        Leaf(vm, "Rpm")!.IsSelected = true;
        Leaf(vm, "Volt")!.IsSelected = true;

        vm.Confirm();

        var watched = session.XcpWatchedObjects;
        watched.Should().HaveCount(2);
        watched.Count(w => w.Name == "Rpm").Should().Be(1);
        watched.Select(w => w.Name).Should().Equal("Rpm", "Volt");
    }

    [Fact]
    public void Confirm_Twice_ProducesNoDuplicateRows()
    {
        var session = FakeSession();
        var vm = new XcpObjectPickerViewModel(Contracts(), session);
        Leaf(vm, "Rpm")!.IsSelected = true;

        vm.Confirm();
        vm.Confirm();

        session.XcpWatchedObjects.Count(w => w.Name == "Rpm").Should().Be(1);
        session.XcpWatchedObjects.Should().HaveCount(1);
    }

    [Fact]
    public void WatchedObjects_DedupesOnNameAndCategoryPair()
    {
        // 同名不同类别的对象是两个不同 A2L 条目——按 (name, category) 对账，
        // 不做仅按名称的粗暴去重。
        var session = FakeSession(new XcpWatchRow("Rpm", "CHARACTERISTIC"));

        var vm = new XcpObjectPickerViewModel(Contracts(), session);

        // 本 fixture 的 CHARACTERISTIC 组没有 Rpm（无处勾选）；
        // MEASUREMENT 类的 Rpm 是另一个对象，不得被误标已关注。
        Leaf(vm, "Rpm")!.IsSelected.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // (e) 空合同集 / 未加载 A2L → 空树不炸
    // ------------------------------------------------------------------

    [Fact]
    public void NullContracts_EmptyTreeAndConfirmDoesNotThrow()
    {
        var vm = new XcpObjectPickerViewModel(null, FakeSession());

        vm.Roots.Should().BeEmpty();
        vm.Confirm().Should().BeEmpty();
    }

    [Fact]
    public void EmptyContractSet_EmptyTree()
    {
        var vm = new XcpObjectPickerViewModel(new ContractSet(EmptyDoc()));

        vm.Roots.Should().BeEmpty();
        vm.Confirm().Should().BeEmpty();
    }

    [Fact]
    public void Ctor_WithoutTraceSession_DoesNotThrow()
    {
        var act = () => new XcpObjectPickerViewModel(Contracts());

        act.Should().NotThrow();
        act().Roots.Should().HaveCount(2);
    }
}
