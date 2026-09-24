using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.Host.App.Services.Xcp;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S3-T5 红测：XcpCardPanelViewModel（spec §1 + D3 + S1 §5.5 卡片格）。
/// <para>
/// 口径：卡片值只来自 <see cref="XcpDaqSample"/>（S2 T16 解码产物），
/// 单位/格式/限值只读 <see cref="ValueContract"/> 解析期字段（spec §1 禁现算量程，
/// 无 A2L 解读调用——Type/引用断言钉住）；停更计时用注入
/// <see cref="TimeProvider"/>（FakeTimeProvider），VM 不持有 Dispatcher，
/// 20 Hz flush 由视图层定时器驱动 <see cref="XcpCardPanelViewModel.Flush"/>。
/// </para>
/// </summary>
public class XcpCardPanelViewModelTests
{
    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static A2lDocument Doc()
    {
        var measurements = new[]
        {
            new A2lMeasurement("Rpm", "rpm fixture", A2lDataType.UBYTE, "CM_RPM",
                "0", "0", "0", "100", 0x1000, new LineRange(1, 1),
                Format: "%6.2", ExtLowerLimit: -10, ExtUpperLimit: 120),
            new A2lMeasurement("Volt", "volt fixture", A2lDataType.UBYTE, "CM_VOLT",
                "0", "0", "0", "50", 0x1001, new LineRange(2, 2),
                Format: "%.3f"),
            new A2lMeasurement("Freq", "freq fixture", A2lDataType.UBYTE, "CM_HZ",
                "0", "0", "0", "1000", 0x1002, new LineRange(3, 3)),
        };
        var compuMethods = new[]
        {
            new A2lCompuMethod("CM_RPM", "speed", "IDENTICAL", "%.2f", "rpm",
                new IdenticalConversion(), new LineRange(10, 10)),
            new A2lCompuMethod("CM_VOLT", "voltage", "IDENTICAL", "%.3f", "V",
                new IdenticalConversion(), new LineRange(11, 11)),
            new A2lCompuMethod("CM_HZ", "frequency", "IDENTICAL", "%.2f", "Hz",
                new IdenticalConversion(), new LineRange(12, 12)),
        };
        var module = new A2lModule("M", "m", measurements,
            Array.Empty<A2lCharacteristic>(), Array.Empty<A2lAxisPts>(), compuMethods,
            Array.Empty<A2lRecordLayout>(), Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1));
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static ValueContract Contract(string name)
    {
        var contracts = new ContractSet(Doc());
        contracts.TryGet(name, out var contract).Should().BeTrue();
        return contract!;
    }

    private static PlannedDaqEntry Entry(string objectName) =>
        new(Pid: 0, OdtIndex: 0, EntryIndex: 0, ObjectName: objectName,
            SegmentIndex: 0, ByteLength: 1, OffsetInOdt: 0,
            LogicalAddress: 0x1000, PhysicalAddress: null);

    private static XcpDaqSample Sample(string objectName, double value, DateTimeOffset at) =>
        new(Entry(objectName), value, at);

    private static XcpCardPanelViewModel NewVm(
        out XcpCardPanelSink sink,
        out FakeTimeProvider time,
        TimeSpan? stalePeriod = null)
    {
        sink = new XcpCardPanelSink();
        time = new FakeTimeProvider();
        return new XcpCardPanelViewModel(sink, time, stalePeriod);
    }

    // ------------------------------------------------------------------
    // (a) Flush 批量更新卡片值（sink.Drain 结果映射到对应卡片）
    // ------------------------------------------------------------------

    [Fact]
    public void Flush_MapsDrainedSamplesToWatchCards()
    {
        var vm = NewVm(out var sink, out var time);
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));
        vm.AddWatch("Volt", "MEASUREMENT", Contract("Volt"));

        var rpm = vm.Cards.Single(c => c.Name == "Rpm");
        var volt = vm.Cards.Single(c => c.Name == "Volt");

        // 首个样本到达前：无值占位（S1 §5.5 卡片格）。
        rpm.DisplayValue.Should().Be("—");

        sink.OnValues(Sample("Rpm", 1234.567, time.GetUtcNow()));
        sink.OnValues(Sample("Volt", 3.14159, time.GetUtcNow()));

        vm.Flush();

        // Format/Unit 均读 ValueContract（%6.2 → 两位小数；%.3f → 三位小数）。
        rpm.DisplayValue.Should().Be("1234.57");
        rpm.Unit.Should().Be("rpm");
        volt.DisplayValue.Should().Be("3.142");
        volt.Unit.Should().Be("V");
    }

    [Fact]
    public void Flush_IgnoresSamplesOutsideWatchSet()
    {
        // 显示集与采集集分离（spec §1）：采集全量，卡片只渲染关注集条目。
        var vm = NewVm(out var sink, out var time);
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));

        sink.OnValues(Sample("Freq", 42, time.GetUtcNow()));
        vm.Flush();

        vm.Cards.Should().ContainSingle(c => c.Name == "Rpm");
        vm.Cards.Single().DisplayValue.Should().Be("—");
    }

    // ------------------------------------------------------------------
    // (b) 越限/越扩展限状态机（含临界值等值不算越限）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(50, XcpCardLimitState.Normal)]
    [InlineData(100, XcpCardLimitState.Normal)]     // 临界值等值不算越限
    [InlineData(110, XcpCardLimitState.OutOfLimit)]
    [InlineData(120, XcpCardLimitState.OutOfLimit)] // == 扩展上限，但仍越常规上限
    [InlineData(121, XcpCardLimitState.OutOfExtendedLimit)]
    [InlineData(0, XcpCardLimitState.Normal)]       // 临界值等值不算越限（下沿）
    [InlineData(-5, XcpCardLimitState.OutOfLimit)]  // 常规下限与扩展下限之间
    [InlineData(-10, XcpCardLimitState.OutOfLimit)] // == 扩展下限，但仍越常规下限
    [InlineData(-11, XcpCardLimitState.OutOfExtendedLimit)]
    public void LimitState_FollowsValueContractParsedLimits(
        double value, XcpCardLimitState expected)
    {
        var vm = NewVm(out var sink, out var time);
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));

        sink.OnValues(Sample("Rpm", value, time.GetUtcNow()));
        vm.Flush();

        vm.Cards.Single().LimitState.Should().Be(expected);
    }

    [Fact]
    public void LimitState_WithoutExtendedLimits_UsesNormalLimitsOnly()
    {
        var vm = NewVm(out var sink, out var time);
        vm.AddWatch("Volt", "MEASUREMENT", Contract("Volt"));

        sink.OnValues(Sample("Volt", 50, time.GetUtcNow()));
        vm.Flush();
        vm.Cards.Single().LimitState.Should().Be(XcpCardLimitState.Normal);

        sink.OnValues(Sample("Volt", 50.5, time.GetUtcNow()));
        vm.Flush();
        vm.Cards.Single().LimitState.Should().Be(XcpCardLimitState.OutOfLimit);
    }

    // ------------------------------------------------------------------
    // (c) 停更 ≥3 周期 IsStale + StaleDuration 文本（时间推进后刷新恢复）
    // ------------------------------------------------------------------

    [Fact]
    public void Staleness_TurnsGreyAfterThreeCycles_AndRecoversOnNextSample()
    {
        // 周期时长参数化，默认 10 ms（对齐从机节拍）；阈值 = 3 × 周期。
        var vm = NewVm(out var sink, out var time);
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));
        var card = vm.Cards.Single();

        sink.OnValues(Sample("Rpm", 1, time.GetUtcNow()));
        vm.Flush();
        card.IsStale.Should().BeFalse();

        // 1、2 个周期：不标灰。
        time.Advance(TimeSpan.FromMilliseconds(10));
        vm.Flush();
        card.IsStale.Should().BeFalse();

        time.Advance(TimeSpan.FromMilliseconds(10));
        vm.Flush();
        card.IsStale.Should().BeFalse();

        // 第 3 个周期（≥ 3 × 10 ms）：标灰 + 停更时长人读文本。
        time.Advance(TimeSpan.FromMilliseconds(10));
        vm.Flush();
        card.IsStale.Should().BeTrue();
        card.StaleDuration.Should().Contain("30").And.Contain("ms");

        // 新样本到达后 flush：恢复非灰。
        sink.OnValues(Sample("Rpm", 2, time.GetUtcNow()));
        vm.Flush();
        card.IsStale.Should().BeFalse();
        card.StaleDuration.Should().Be("刚刚更新");
        card.DisplayValue.Should().Be("2.00");
    }

    [Fact]
    public void Staleness_PeriodIsParameterizable()
    {
        var vm = NewVm(out var sink, out var time,
            stalePeriod: TimeSpan.FromMilliseconds(25));
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));
        var card = vm.Cards.Single();

        sink.OnValues(Sample("Rpm", 1, time.GetUtcNow()));
        vm.Flush();

        // 50 ms = 2 × 25 ms：仍不算停更。
        time.Advance(TimeSpan.FromMilliseconds(50));
        vm.Flush();
        card.IsStale.Should().BeFalse();

        // 75 ms = 3 × 25 ms：停更。
        time.Advance(TimeSpan.FromMilliseconds(25));
        vm.Flush();
        card.IsStale.Should().BeTrue();
        card.StaleDuration.Should().Contain("75");
    }

    [Fact]
    public void DefaultStalePeriod_AlignsWithSlaveTick()
    {
        XcpCardPanelViewModel.DefaultStalePeriodMilliseconds.Should().Be(10);
    }

    // ------------------------------------------------------------------
    // (d) 卡片字段只来自 ValueContract + XcpDaqSample（无 A2L 解读调用）
    // ------------------------------------------------------------------

    [Fact]
    public void Cards_ConsumeTheExactParsedContractInstance_NoReinterpretation()
    {
        var vm = NewVm(out var sink, out var time);
        var contract = Contract("Rpm");
        vm.AddWatch("Rpm", "MEASUREMENT", contract);

        var card = vm.Cards.Single();

        // 引用断言：卡片持有解析期合同实例本身，不重建、不重解读（spec §1）。
        card.Contract.Should().BeSameAs(contract);
        card.Unit.Should().Be(contract.Unit);
        card.Name.Should().Be(contract.ObjectName);
        card.Category.Should().Be("MEASUREMENT");
    }

    [Fact]
    public void CardSurface_HasNoA2lParserModelDependency()
    {
        // Type 断言：VM 与卡片的声明面（字段/属性/方法/构造参数/事件）不得出现
        // A2lEditor 解析模型类型（A2lDocument / A2lEditor.Core.Model.*）——
        // 卡片字段唯一来源是 ValueContract（解析期产物）+ XcpDaqSample。
        var offenders = DeclaredSurfaceTypes(typeof(XcpCardPanelViewModel))
            .Concat(DeclaredSurfaceTypes(typeof(XcpCardViewModel)))
            .Where(t => t == typeof(A2lDocument)
                || (t.Namespace?.StartsWith("A2lEditor.Core.Model", StringComparison.Ordinal) ?? false))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty();
    }

    private static IEnumerable<Type> DeclaredSurfaceTypes(Type type)
    {
        var seen = new HashSet<Type>();

        void Add(Type? t)
        {
            if (t is null || !seen.Add(t))
                return;
            if (t.IsByRef || t.IsPointer || t.IsArray)
            {
                Add(t.GetElementType());
                return;
            }
            if (t.IsGenericType)
                foreach (var arg in t.GetGenericArguments())
                    Add(arg);
        }

        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            foreach (var p in ctor.GetParameters())
                Add(p.ParameterType);
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            Add(f.FieldType);
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            Add(p.PropertyType);
            foreach (var i in p.GetIndexParameters())
                Add(i.ParameterType);
        }
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Add(m.ReturnType);
            foreach (var p in m.GetParameters())
                Add(p.ParameterType);
        }
        foreach (var e in type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Add(e.EventHandlerType);

        return seen;
    }

    // ------------------------------------------------------------------
    // (e) sink 丢帧计数（DropOldest）进 VM 可见状态（不静默）
    // ------------------------------------------------------------------

    [Fact]
    public void Flush_PublishesSinkDropOldestCount_Visibly()
    {
        var vm = NewVm(out var sink, out var time);
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));

        for (var i = 0; i < XcpCardPanelSink.DefaultCapacity + 50; i++)
            sink.OnValues(Sample("Rpm", i, time.GetUtcNow()));

        vm.Flush();

        sink.DroppedCount.Should().BeGreaterThan(0);
        vm.DroppedCount.Should().Be(sink.DroppedCount);
    }

    [Fact]
    public void Flush_CountsGapEntriesIntoVisibleAttributionChannel()
    {
        var vm = NewVm(out var sink, out var time);
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));

        XcpAcquisitionGap? observed = null;
        vm.GapObserved += gap => observed = gap;

        sink.OnGap(new XcpAcquisitionGap(
            XcpAcquisitionGapKind.AcquisitionInterrupted, "stream interrupted"));
        vm.Flush();

        vm.GapCount.Should().Be(1);
        vm.LastGapDetail.Should().Be("stream interrupted");
        observed.Should().NotBeNull();
        observed!.Kind.Should().Be(XcpAcquisitionGapKind.AcquisitionInterrupted);
    }

    [Fact]
    public void GapObserved_PreservesFullRecordFields_ParityContract()
    {
        // T5 评审 LOW：接力契约钉死——事件透传完整 record（Kind/Detail/Cause/
        // ExpectedMaxDuration/ReceiveKind 五字段逐字段相等），防止未来改窄成 string。
        var vm = NewVm(out var sink, out _);
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));

        XcpAcquisitionGap? observed = null;
        vm.GapObserved += gap => observed = gap;
        var gap = new XcpAcquisitionGap(
            XcpAcquisitionGapKind.PlanGapOpened, "rotation window",
            ExpectedMaxDuration: TimeSpan.FromMilliseconds(120));
        sink.OnGap(gap);
        vm.Flush();

        observed.Should().Be(gap);
    }

    [Fact]
    public void Update_RaisesPropertyChanged_ForLastUpdate()
    {
        // T5 评审 MEDIUM：LastUpdate 是公开绑定属性，更新必须通知（原漏报）。
        var vm = NewVm(out var sink, out var time);
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));

        var changes = new List<string?>();
        vm.Cards[0].PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        sink.OnValues(Sample("Rpm", 1, time.GetUtcNow()));
        vm.Flush();

        changes.Should().Contain(nameof(XcpCardViewModel.LastUpdate));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveStalePeriod()
    {
        // T5 评审 LOW：与 sink capacity 校验对称——0/负周期 = 全卡片永久停更。
        var act = () => new XcpCardPanelViewModel(stalePeriod: TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ------------------------------------------------------------------
    // (f) 关注集动态增删（AddWatch/RemoveWatch，去重）
    // ------------------------------------------------------------------

    [Fact]
    public void AddWatch_DeduplicatesByObjectName()
    {
        var vm = NewVm(out var sink, out var time);
        var contract = Contract("Rpm");

        vm.AddWatch("Rpm", "MEASUREMENT", contract);
        vm.AddWatch("Rpm", "MEASUREMENT", contract);

        vm.Cards.Should().ContainSingle(c => c.Name == "Rpm");
    }

    [Fact]
    public void RemoveWatch_DropsCard_AndIgnoresSubsequentSamples()
    {
        var vm = NewVm(out var sink, out var time);
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));
        vm.RemoveWatch("Rpm").Should().BeTrue();

        vm.Cards.Should().BeEmpty();

        // 关注集移除后样本继续到达（采集集仍全量）：不得异常、不得复活卡片。
        sink.OnValues(Sample("Rpm", 7, time.GetUtcNow()));
        var flush = () => vm.Flush();
        flush.Should().NotThrow();
        vm.Cards.Should().BeEmpty();

        // 重新关注：卡片可再次出现。
        vm.AddWatch("Rpm", "MEASUREMENT", Contract("Rpm"));
        vm.Flush();
        vm.Cards.Should().ContainSingle(c => c.Name == "Rpm");

        vm.RemoveWatch("Nope").Should().BeFalse();
    }
}
