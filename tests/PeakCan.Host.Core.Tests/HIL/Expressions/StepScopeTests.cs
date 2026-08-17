using FluentAssertions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Expressions;
using PeakCan.HIL.Core.HIL.Uds;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Expressions;

public class StepScopeTests
{
    // ===== HostSignalValueResolver =====

    [Fact]
    public void HostSignalValueResolver_ReturnsDouble_WhenSignalFound()
    {
        var ctx = Substitute.For<IAssertionContext>();
        ctx.GetSignalValue("BMS.EngineRPM", Arg.Any<int>()).Returns(3000.0);

        var resolver = new HostSignalValueResolver(ctx);
        var found = resolver.TryGetSignal("BMS.EngineRPM", out var value);

        found.Should().BeTrue();
        value.Kind.Should().Be(ExpressionValue.ValueKind.Double);
        value.AsDouble.Should().Be(3000.0);
    }

    [Fact]
    public void HostSignalValueResolver_ReturnsUndefined_WhenSignalNotFound()
    {
        var ctx = Substitute.For<IAssertionContext>();
        ctx.GetSignalValue("BMS.EngineRPM", Arg.Any<int>()).Returns((double?)null);

        var resolver = new HostSignalValueResolver(ctx);
        var found = resolver.TryGetSignal("BMS.EngineRPM", out var value);

        found.Should().BeFalse();
        value.Kind.Should().Be(ExpressionValue.ValueKind.Undefined);
    }

    // ===== HostDidValueResolver =====

    [Fact]
    public void HostDidValueResolver_ReturnsBytes_WhenDidFound()
    {
        var store = Substitute.For<IStepVariableStore>();
        var dict = new Dictionary<string, object> { ["did_0xF190"] = new byte[] { 0x01, 0x02 } };
        store.Variables.Returns(dict);

        var resolver = new HostDidValueResolver(store);
        var found = resolver.TryGetDid(0xF190, out var value);

        found.Should().BeTrue();
        value.Kind.Should().Be(ExpressionValue.ValueKind.Bytes);
        value.AsBytes.Should().BeEquivalentTo(new byte[] { 0x01, 0x02 });
    }

    [Fact]
    public void HostDidValueResolver_ReturnsUndefined_WhenDidNotFound()
    {
        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>());

        var resolver = new HostDidValueResolver(store);
        var found = resolver.TryGetDid(0xF190, out var value);

        found.Should().BeFalse();
        value.Kind.Should().Be(ExpressionValue.ValueKind.Undefined);
    }

    [Fact]
    public void HostDidValueResolver_ConvertsObjectTypes_Correctly()
    {
        var store = Substitute.For<IStepVariableStore>();
        var dict = new Dictionary<string, object>
        {
            ["did_0x0001"] = 42,           // int → FromLong
            ["did_0x0002"] = 3.14,          // double → FromDouble
            ["did_0x0003"] = true,          // bool → FromBool
            ["did_0x0004"] = "hello",       // string → FromString
            ["did_0x0005"] = new byte[] { 0xAA }, // byte[] → FromBytes
            ["did_0x0006"] = null!,          // null → Undefined
            ["did_0x0007"] = new object(),   // 未知 → Undefined
        };
        store.Variables.Returns(dict);

        var resolver = new HostDidValueResolver(store);

        var (found1, val1) = ResolveDid(resolver, 0x0001);
        found1.Should().BeTrue(); val1.Kind.Should().Be(ExpressionValue.ValueKind.Long); val1.AsLong.Should().Be(42);

        var (found2, val2) = ResolveDid(resolver, 0x0002);
        found2.Should().BeTrue(); val2.Kind.Should().Be(ExpressionValue.ValueKind.Double); val2.AsDouble.Should().Be(3.14);

        var (found3, val3) = ResolveDid(resolver, 0x0003);
        found3.Should().BeTrue(); val3.Kind.Should().Be(ExpressionValue.ValueKind.Bool); val3.AsBool.Should().BeTrue();

        var (found4, val4) = ResolveDid(resolver, 0x0004);
        found4.Should().BeTrue(); val4.Kind.Should().Be(ExpressionValue.ValueKind.String); val4.AsString.Should().Be("hello");

        var (found5, val5) = ResolveDid(resolver, 0x0005);
        found5.Should().BeTrue(); val5.Kind.Should().Be(ExpressionValue.ValueKind.Bytes); val5.AsBytes.Should().BeEquivalentTo(new byte[] { 0xAA });

        // null → Undefined, but found is true (key exists)
        var (found6, val6) = ResolveDid(resolver, 0x0006);
        found6.Should().BeTrue(); val6.Kind.Should().Be(ExpressionValue.ValueKind.Undefined);

        // 未知 type → Undefined
        var (found7, val7) = ResolveDid(resolver, 0x0007);
        found7.Should().BeTrue(); val7.Kind.Should().Be(ExpressionValue.ValueKind.Undefined);
    }

    private static (bool found, ExpressionValue value) ResolveDid(HostDidValueResolver resolver, ushort did)
    {
        var f = resolver.TryGetDid(did, out var v);
        return (f, v);
    }

    // ===== FrameStatisticsFunctionRegistry =====

    [Fact]
    public void FrameStats_ElapsedMs_ReturnsNowMinusCaseStart()
    {
        var stats = Substitute.For<IFrameStatistics>();
        stats.Now.Returns(5000L);

        var registry = new FrameStatisticsFunctionRegistry(stats, caseStart: 1000);
        var invoked = registry.TryInvoke("elapsedMs", [], out var result);

        invoked.Should().BeTrue();
        result.Kind.Should().Be(ExpressionValue.ValueKind.Long);
        result.AsLong.Should().Be(4000);
    }

    [Fact]
    public void FrameStats_FrameCount_ReturnsCountSinceCaseStart()
    {
        var stats = Substitute.For<IFrameStatistics>();
        stats.Now.Returns(5000L);
        stats.CountSince(new CanId(0x123, FrameFormat.Standard), 1000, 5000).Returns(5);

        var registry = new FrameStatisticsFunctionRegistry(stats, caseStart: 1000);
        var invoked = registry.TryInvoke("frameCount", [ExpressionValue.FromLong(0x123)], out var result);

        invoked.Should().BeTrue();
        result.Kind.Should().Be(ExpressionValue.ValueKind.Long);
        result.AsLong.Should().Be(5);
    }

    [Fact]
    public void FrameStats_FrameCount_WithWindow_ReturnsCountInWindow()
    {
        var stats = Substitute.For<IFrameStatistics>();
        stats.Now.Returns(5000L);
        // windowMs = 2000, so CountSince(id, 5000-2000=3000, 5000)
        stats.CountSince(new CanId(0x123, FrameFormat.Standard), 3000, 5000).Returns(3);

        var registry = new FrameStatisticsFunctionRegistry(stats, caseStart: 1000);
        var invoked = registry.TryInvoke("frameCount", [ExpressionValue.FromLong(0x123), ExpressionValue.FromLong(2000)], out var result);

        invoked.Should().BeTrue();
        result.Kind.Should().Be(ExpressionValue.ValueKind.Long);
        result.AsLong.Should().Be(3);
    }

    [Fact]
    public void FrameStats_FrameSeen_ReturnsTrue_WhenCountGreaterThanZero()
    {
        var stats = Substitute.For<IFrameStatistics>();
        stats.Now.Returns(5000L);
        stats.CountSince(new CanId(0x123, FrameFormat.Standard), 1000, 5000).Returns(3);

        var registry = new FrameStatisticsFunctionRegistry(stats, caseStart: 1000);
        var invoked = registry.TryInvoke("frameSeen", [ExpressionValue.FromLong(0x123)], out var result);

        invoked.Should().BeTrue();
        result.Kind.Should().Be(ExpressionValue.ValueKind.Bool);
        result.AsBool.Should().BeTrue();
    }

    [Fact]
    public void FrameStats_FrameSeen_ReturnsFalse_WhenCountIsZero()
    {
        var stats = Substitute.For<IFrameStatistics>();
        stats.Now.Returns(5000L);
        stats.CountSince(new CanId(0x123, FrameFormat.Standard), 1000, 5000).Returns(0);

        var registry = new FrameStatisticsFunctionRegistry(stats, caseStart: 1000);
        var invoked = registry.TryInvoke("frameSeen", [ExpressionValue.FromLong(0x123)], out var result);

        invoked.Should().BeTrue();
        result.Kind.Should().Be(ExpressionValue.ValueKind.Bool);
        result.AsBool.Should().BeFalse();
    }

    [Fact]
    public void FrameStats_UnknownFunction_ReturnsFalse()
    {
        var stats = Substitute.For<IFrameStatistics>();
        var registry = new FrameStatisticsFunctionRegistry(stats, caseStart: 0);

        var invoked = registry.TryInvoke("nonexistent", [], out var result);

        invoked.Should().BeFalse();
        result.Kind.Should().Be(ExpressionValue.ValueKind.Undefined);
    }

    [Fact]
    public void FrameStats_FrameCount_WithDoubleArg_ConvertsCorrectly()
    {
        var stats = Substitute.For<IFrameStatistics>();
        stats.Now.Returns(5000L);
        stats.CountSince(new CanId(0x456, FrameFormat.Standard), 1000, 5000).Returns(7);

        var registry = new FrameStatisticsFunctionRegistry(stats, caseStart: 1000);
        // 以 double 形式传入 CanId
        var invoked = registry.TryInvoke("frameCount", [ExpressionValue.FromDouble(0x456)], out var result);

        invoked.Should().BeTrue();
        result.AsLong.Should().Be(7);
    }

    // ===== StepScopeFactory =====

    [Fact]
    public void StepScopeFactory_CreatesScope_WithAllResolvers()
    {
        var ctx = Substitute.For<IAssertionContext>();
        ctx.GetSignalValue("BMS.EngineRPM", Arg.Any<int>()).Returns(3000.0);

        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>
        {
            ["did_0xF190"] = new byte[] { 0x01 },
            ["myVar"] = 42.0,
        });

        var stats = Substitute.For<IFrameStatistics>();
        stats.Now.Returns(2000L);

        var scope = StepScopeFactory.Create(ctx, store, stats, caseStart: 1000);

        scope.Should().NotBeNull();
        scope.FunctionRegistry.Should().NotBeNull();
        scope.SignalResolver.Should().NotBeNull();
        scope.DidResolver.Should().NotBeNull();
        scope.Variables.Should().NotBeNull();
    }

    [Fact]
    public void StepScopeFactory_Variables_IncludesConvertedEntries()
    {
        var ctx = Substitute.For<IAssertionContext>();
        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>
        {
            ["myVar"] = 42.0,
        });

        var stats = Substitute.For<IFrameStatistics>();
        var scope = StepScopeFactory.Create(ctx, store, stats, caseStart: 0);

        var resolved = scope.Resolve("myVar");
        resolved.Kind.Should().Be(ExpressionValue.ValueKind.Double);
        resolved.AsDouble.Should().Be(42.0);
    }

    [Fact]
    public void StepScopeFactory_SuiteParams_AreConverted()
    {
        var ctx = Substitute.For<IAssertionContext>();
        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>());

        var stats = Substitute.For<IFrameStatistics>();
        var suiteParams = new Dictionary<string, ParameterValue>
        {
            ["speed"] = new(ParameterKind.Number, 100.0),
            ["enable"] = new(ParameterKind.Bool, true),
            ["name"] = new(ParameterKind.String, "test"),
            ["data"] = new(ParameterKind.HexBytes, new byte[] { 0xAA, 0xBB }),
        };

        var scope = StepScopeFactory.Create(ctx, store, stats, caseStart: 0, suiteParams: suiteParams);

        var speed = scope.Resolve("speed");
        speed.Kind.Should().Be(ExpressionValue.ValueKind.Double);
        speed.AsDouble.Should().Be(100.0);

        var enable = scope.Resolve("enable");
        enable.Kind.Should().Be(ExpressionValue.ValueKind.Bool);
        enable.AsBool.Should().BeTrue();

        var name = scope.Resolve("name");
        name.Kind.Should().Be(ExpressionValue.ValueKind.String);
        name.AsString.Should().Be("test");

        var data = scope.Resolve("data");
        data.Kind.Should().Be(ExpressionValue.ValueKind.Bytes);
        data.AsBytes.Should().BeEquivalentTo(new byte[] { 0xAA, 0xBB });
    }

    [Fact]
    public void StepScopeFactory_CaseParams_TakePriorityOverSuiteParams()
    {
        var ctx = Substitute.For<IAssertionContext>();
        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>());

        var stats = Substitute.For<IFrameStatistics>();
        var suiteParams = new Dictionary<string, ParameterValue>
        {
            ["param"] = new(ParameterKind.Number, 1.0),
        };
        var caseParams = new Dictionary<string, ParameterValue>
        {
            ["param"] = new(ParameterKind.Number, 2.0),
        };

        var scope = StepScopeFactory.Create(ctx, store, stats, caseStart: 0,
            suiteParams: suiteParams, caseParams: caseParams);

        var resolved = scope.Resolve("param");
        resolved.AsDouble.Should().Be(2.0);
    }

    [Fact]
    public void StepScopeFactory_SignalResolution_ThroughResolver()
    {
        var ctx = Substitute.For<IAssertionContext>();
        ctx.GetSignalValue("BMS.EngineRPM", Arg.Any<int>()).Returns(3000.0);

        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>());

        var stats = Substitute.For<IFrameStatistics>();

        var scope = StepScopeFactory.Create(ctx, store, stats, caseStart: 0);

        // signal.BMS.EngineRPM 格式 — 需通过 Resolve 或 SignalResolver
        // StepScope.Resolve 本身不处理 signal.X.Y 格式，只做变量名查找
        // SignalResolver 由 evaluator 调用，但我们可以直接测试 resolver
        scope.SignalResolver!.TryGetSignal("BMS.EngineRPM", out var value).Should().BeTrue();
        value.AsDouble.Should().Be(3000.0);
    }

    [Fact]
    public void StepScopeFactory_DidResolution_ThroughResolver()
    {
        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>
        {
            ["did_0xF190"] = new byte[] { 0x01, 0x02 },
        });

        var ctx = Substitute.For<IAssertionContext>();
        var stats = Substitute.For<IFrameStatistics>();
        var scope = StepScopeFactory.Create(ctx, store, stats, caseStart: 0);

        // did.0xF190 格式 — 由 evaluator 解析前缀后调用 DidResolver
        scope.DidResolver!.TryGetDid(0xF190, out var value).Should().BeTrue();
        value.AsBytes.Should().BeEquivalentTo(new byte[] { 0x01, 0x02 });
    }

    [Fact]
    public void StepScopeFactory_FrameStatsFunction_ThroughRegistry()
    {
        var ctx = Substitute.For<IAssertionContext>();
        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>());

        var stats = Substitute.For<IFrameStatistics>();
        stats.Now.Returns(5000L);

        var scope = StepScopeFactory.Create(ctx, store, stats, caseStart: 1000);

        var invoked = scope.FunctionRegistry!.TryInvoke("elapsedMs", [], out var result);
        invoked.Should().BeTrue();
        result.AsLong.Should().Be(4000);
    }

    [Fact]
    public void StepScopeFactory_LoopIndexVar_IsPassedThrough()
    {
        var ctx = Substitute.For<IAssertionContext>();
        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>());

        var stats = Substitute.For<IFrameStatistics>();
        var loopIndexVar = new Dictionary<string, ExpressionValue>
        {
            ["i"] = ExpressionValue.FromLong(3),
        };

        var scope = StepScopeFactory.Create(ctx, store, stats, caseStart: 0,
            loopIndexVar: loopIndexVar);

        var resolved = scope.Resolve("i");
        resolved.AsLong.Should().Be(3);
    }

    [Fact]
    public void StepScopeFactory_Resolve_ReturnsUndefined_ForMissingVariable()
    {
        var ctx = Substitute.For<IAssertionContext>();
        var store = Substitute.For<IStepVariableStore>();
        store.Variables.Returns(new Dictionary<string, object>());

        var stats = Substitute.For<IFrameStatistics>();

        var scope = StepScopeFactory.Create(ctx, store, stats, caseStart: 0);

        var resolved = scope.Resolve("nonexistent");
        resolved.Kind.Should().Be(ExpressionValue.ValueKind.Undefined);
    }
}