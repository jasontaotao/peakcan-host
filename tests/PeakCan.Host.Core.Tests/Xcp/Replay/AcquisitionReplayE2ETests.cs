using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using Microsoft.Extensions.Time.Testing;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Scheduling;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Replay;

/// <summary>
/// S6-T7 模拟从机端到端（spec 验收判据）：采集（XcpAcquisitionSession 轮转 + DTO 注入）
/// → 落盘（XcpMdfRecordSink 真实 MF4 文件）→ 回放读取（Mdf4StreamReader）
/// 数据一致：回放样本值 == 注入值（M0 UBYTE IDENTICAL）。全程零 DOWNLOAD 断言续用。
/// </summary>
public sealed class AcquisitionReplayE2ETests
{
    [Fact]
    public async Task Acquisition_Records_To_Mf4_And_Replay_Reads_Same_Value()
    {
        var clock = new FakeTimeProvider();
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"s6e2e_{Guid.NewGuid():N}.mf4");
        try
        {
            await using var sink = new XcpMdfRecordSink(new XcpMdfRecordSinkOptions
            {
                Directory = System.IO.Path.GetTempPath(),
                FilePrefix = "s6e2e",
                Channels = [new MdfChannelSpec("M0", "")],
            });
            await sink.StartAsync(System.DateTimeOffset.Parse("2026-09-26T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture), [new MdfChannelSpec("M0", "")]);

            var spyTransport = new XcpVirtualSlave();
            var spy = new XcpTransportSpy(spyTransport);
            using var session = new XcpAcquisitionSession(spy, MasterOptions(),
                new XcpAcquisitionSessionOptions
                {
                    Sink = sink,
                    Polling = new PollingSchedulerOptions { Period = TimeSpan.FromMilliseconds(1) },
                }, clock);
            var map = session.Plan(Contracts(SmallDoc()), Placeholders(SmallDoc()));
            var rotation = await session.ConfigureRotationAsync(map);
            Assert.Equal(RotationTableState.Running, rotation.TableState);

            // 采集：DTO 注入 42（PID 0x00 → M0，IDENTICAL 换算 → 物理值 42）。
            spyTransport.InjectDto(0x00, 0x2A);

            // StopAsync 先排空队列再 finalize：之后 WrittenCount 终值可断言。
            await sink.StopAsync();
            Assert.Equal(1, sink.WrittenCount);
            Assert.True(File.Exists(sink.FilePath));
            File.Move(sink.FilePath!, path);

            // 回放：读回 MDF，样本值一致（D3 errata 口径：落盘即解码物理值，回放直读）。
            var data = Mdf4StreamReader.Read(path);
            var channel = Assert.Single(data.Channels);
            Assert.Equal("M0", channel.Name);

            // 两条记录：1 条换表空窗失效行（S4 gap 语义：Invalid + NaN）+ 1 条真实样本。
            Assert.Equal(2, channel.Samples.Count);
            var invalid = Assert.Single(channel.Samples, s => s.Invalid);
            Assert.True(double.IsNaN(invalid.Value));
            var valid = Assert.Single(channel.Samples, s => !s.Invalid);
            Assert.Equal(42d, valid.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- 夹具（ReceiveE2ETests SmallDoc 同款：单模块 + 单 DAQ 表 + 单段映射）----

    private static XcpMasterOptions MasterOptions() => new(XcpVirtualSlave.DefaultMasterCanId);

    private static ContractSet Contracts(A2lDocument doc) => Asap2PackageApi.Contracts(doc);

    private static AcquisitionPlan Placeholders(A2lDocument doc) => AcquisitionPlan.Build(Contracts(doc));

    private static A2lDocument SmallDoc()
    {
        var module = new A2lModule("M", "m",
            new[] { new A2lMeasurement("M0", "d", A2lDataType.UBYTE, "CM_ID", "0", "0", "0", "65535", 0x1000, new LineRange(1, 1)) },
            Array.Empty<A2lCharacteristic>(), Array.Empty<A2lAxisPts>(),
            new[] { new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%d", "-", new IdenticalConversion(), new LineRange(1, 1)) },
            Array.Empty<A2lRecordLayout>(), Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1), MemorySegments: Segments(),
            IfDataXcp: IfData(new XcpDaqList(0, "DAQ", 15, 100, 0, 0, "")));
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static A2lMemorySegment[] Segments() => new[]
    {
        new A2lMemorySegment("CAL", "cal", "DATA", "FLASH", "INTERN", 0x1000, 0x1000,
            Array.Empty<string>(), new LineRange(1, 1),
            IfDataXcp: new XcpIfData(XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
                Array.Empty<XcpOnCan>(),
                new[]
                {
                    new XcpSegment(0, 1, 0, 0, 0,
                        new[] { new XcpAddressMapping(0x1000, 0x9000, 0x1000, Array.Empty<XcpMissingField>(), "") },
                        1, Array.Empty<XcpMissingField>(), ""),
                },
                Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "")),
    };

    private static XcpIfData IfData(XcpDaqList daqList) => new(
        XcpIfDataScope.ModuleLevel, null,
        new XcpDaq(Dynamic: false, MaxDaq: 1, MaxEventChannel: 1, MinDaq: null,
            OptimisationType: "STATIC", AddressExtension: "", IdentificationFieldType: "",
            GranularityOdtEntrySizeDaq: "", MaxOdtEntrySizeDaq: 4, OverloadIndication: false,
            new[] { daqList }, Array.Empty<XcpEventChannel>(),
            Array.Empty<XcpMissingField>(), ""),
        null, null,
        Array.Empty<XcpOnCan>(), Array.Empty<XcpSegment>(),
        Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "");
}
