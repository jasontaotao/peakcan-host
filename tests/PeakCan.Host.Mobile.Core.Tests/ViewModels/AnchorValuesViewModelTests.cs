using FluentAssertions;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;
using PeakCan.Host.Mobile.Core.Tests.Fakes;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class AnchorValuesViewModelTests
{
    private static DbcCatalog CreateCatalog() => DbcCatalog.Parse("""
        VERSION ""

        NS_ :

        BS_:

        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
         SG_ EngineTemp : 16|8@1+ (1,-40) [-40|215] "degC" Vector__XXX

        BO_ 512 OilSystem: 8 ECM
         SG_ OilPressure : 0|8@1+ (10,0) [0|2550] "kPa" Vector__XXX
        """, "engine.dbc").Catalog!;

    private static CachedFrame Frame(long index, double ts, uint canId, byte[]? data = null) =>
        new(index, ts, canId, false, 8, data ?? [0, 0, 0, 0, 0, 0, 0, 0]);

    private static AnchorValuesViewModel CreateVm(ITraceCacheStore cache, long traceId, DbcCatalog? dbc) =>
        new(cache, traceId, dbc, new FakeUiDispatcher());

    [Fact]
    public async Task LoadAsync_WithDbc_ExpandsEverySignalSortedByMessage()
    {
        // Arrange: 0x100 两帧（取最新 t=3.0）+ 0x200 一帧；DBC 对两个 ID 均有定义
        await using var store = new TraceCacheStore(":memory:");
        var traceId = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(traceId,
        [
            Frame(0, 1.0, 0x100, [0x00, 0x04, 0x28, 0, 0, 0, 0, 0]),
            Frame(1, 3.0, 0x100, [0x00, 0x04, 0x28, 0, 0, 0, 0, 0]),
            Frame(2, 2.0, 0x200, [0x0A, 0, 0, 0, 0, 0, 0, 0]),
        ]);
        var vm = CreateVm(store, traceId, CreateCatalog());

        await vm.LoadAsync(3.0);

        vm.IsEmpty.Should().BeFalse();
        vm.Rows.Should().HaveCount(3);
        vm.Rows.Select(r => r.MessageName).Should().Equal(["EngineData", "EngineData", "OilSystem"]); // 按消息名排序
        vm.Rows[0].SignalName.Should().Be("EngineSpeed"); // 同消息内保持 DBC 定义顺序
        vm.Rows[0].ValueText.Should().Be("256");          // 0x0400 * 0.25
        vm.Rows[0].Unit.Should().Be("rpm");
        vm.Rows[1].SignalName.Should().Be("EngineTemp");  // bits 16-23 = 0x28, 40 - 40
        vm.Rows[1].ValueText.Should().Be("0");
        vm.Rows[1].Unit.Should().Be("degC");
        vm.Rows[2].SignalName.Should().Be("OilPressure"); // 0x0A * 10
        vm.Rows[2].ValueText.Should().Be("100");
        vm.Rows[2].Unit.Should().Be("kPa");
    }

    [Fact]
    public async Task LoadAsync_WithoutDbc_FallsBackToRawHexRows()
    {
        // Arrange: 无 DBC → 每 ID 最新帧一行，raw hex
        await using var store = new TraceCacheStore(":memory:");
        var traceId = await store.GetOrCreateTraceAsync("a.asc", 100);
        var frame = Frame(0, 2.5, 0x100, [0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0]);
        await store.AppendFramesAsync(traceId, [frame]);
        var vm = CreateVm(store, traceId, null);

        await vm.LoadAsync(3.0);

        vm.IsEmpty.Should().BeFalse();
        var row = vm.Rows.Should().ContainSingle().Subject;
        row.SignalName.Should().BeEmpty();
        row.Unit.Should().BeEmpty();
        row.MessageName.Should().Be(FrameRow.FromCached(frame).IdText);
        // 面板与表格必须同一份 hex 格式化——逐字节一致
        row.ValueText.Should().Be(FrameRow.FromCached(frame).DataText);
    }

    [Fact]
    public async Task LoadAsync_NoCachedFrames_SetsIsEmpty()
    {
        // Arrange: ts 早于任何帧（未缓存区域）
        await using var store = new TraceCacheStore(":memory:");
        var traceId = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(traceId, [Frame(0, 1.0, 0x100)]);
        var vm = CreateVm(store, traceId, CreateCatalog());

        await vm.LoadAsync(0.5);

        vm.IsEmpty.Should().BeTrue();
        vm.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_UnknownIdInDbc_RawFallbackForThatFrameOnly()
    {
        // Arrange: 0x100 有 DBC 定义；0x300 无 → 仅 0x300 行走 raw
        await using var store = new TraceCacheStore(":memory:");
        var traceId = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(traceId,
        [
            Frame(0, 1.0, 0x100, [0x00, 0x04, 0, 0, 0, 0, 0, 0]),
            Frame(1, 2.0, 0x300, [0x01, 0x02, 0, 0, 0, 0, 0, 0]),
        ]);
        var vm = CreateVm(store, traceId, CreateCatalog());

        await vm.LoadAsync(3.0);

        // 排序：按 MessageName 升序 → "300" < "EngineData"；0x100 定义了两个信号 → 2 行 + 1 raw 行
        vm.Rows.Should().HaveCount(3);
        vm.Rows[0].MessageName.Should().Be("300");        // 0x300 无定义 → raw（X3）
        vm.Rows[0].SignalName.Should().BeEmpty();
        vm.Rows[0].ValueText.Should().Be("01 02 00 00 00 00 00 00");
        vm.Rows[1].MessageName.Should().Be("EngineData"); // 0x100 有定义 → 信号行
        vm.Rows[1].SignalName.Should().Be("EngineSpeed");
        vm.Rows[2].SignalName.Should().Be("EngineTemp");
    }

    [Fact]
    public async Task LoadAsync_PublishesChangesOnUiDispatcher()
    {
        // Arrange: fake dispatcher Post 同步执行
        await using var store = new TraceCacheStore(":memory:");
        var traceId = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.AppendFramesAsync(traceId, [Frame(0, 1.0, 0x100)]);
        var vm = CreateVm(store, traceId, CreateCatalog());
        var rowsChanged = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnchorValuesViewModel.Rows)) rowsChanged++;
        };

        await vm.LoadAsync(3.0);

        // Post 在 fake dispatcher 上立即执行；IsEmpty 从 false→false 不重复通知（CommunityToolkit 语义）
        rowsChanged.Should().Be(1);
    }
}