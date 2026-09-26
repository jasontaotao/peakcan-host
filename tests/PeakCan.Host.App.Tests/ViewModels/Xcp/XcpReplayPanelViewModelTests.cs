using A2lEditor.Core;
using A2lEditor.Core.Layout;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Replay;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S6-T3：回放面板 VM（spec D3/T2 补记口径）。渲染面归视图 code-behind，
/// 本测试钉：加载 → 通道清单/元数据/警示态 → 渲染请求事件。
/// </summary>
public sealed class XcpReplayPanelViewModelTests
{
    private static readonly MdfChannelSpec Rpm = new("EngineSpeed", "rpm");

    private const string ShaA = "AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111";
    private const string ShaB = "BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222";

    private static string TempPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s6vm_{Guid.NewGuid():N}.mf4");

    private static ContractSnapshot BuildSnapshot(string sha) => new(
        ContractSchemaVersion: 1,
        A2lSha256: sha,
        A2l: new A2lFingerprint(1, 0, 0, 0, 0),
        PackageVersion: "0.1.1",
        Contracts:
        [
            new SnapshottedContract(
                ObjectName: "EngineSpeed", Category: "Measurement", CharacteristicKind: null,
                Segments: [new ValueSegment(0x2000, 2, (ushort)0, 0)],
                TotalByteLength: 2, DataType: "UWORD", BigEndian: false, Unit: "rpm", Format: null,
                LowerLimit: 0.0, UpperLimit: 8000.0, ExtLowerLimit: null, ExtUpperLimit: null,
                IsWritable: false, AccessSource: "Declared",
                Conversion: new SnapshottedConversion(
                    "identical", null, null, null, null, null, null, null, null, null, null, null, null),
                Axis: null, Comment: null, Notes: [], SourceLine: 1),
        ]);

    private static async Task<string> WriteFileAsync(ContractSnapshot? snapshot)
    {
        var path = TempPath();
        var writer = Mdf4StreamWriter.Create(path, [Rpm], DateTimeOffset.UtcNow);
        await writer.WriteRecordAsync(0, 0.0, 1000.0);
        await writer.WriteRecordAsync(0, 0.1, 1200.0);
        if (snapshot is { } snap)
            await writer.WriteAttachmentAsync("application/json",
                "contractSchemaVersion=1; packageVersion=0.1.1",
                System.Text.Encoding.UTF8.GetBytes(ContractSnapshotCodec.Encode(snap)));
        await writer.FinalizeAsync();
        await writer.DisposeAsync();
        return path;
    }

    [Fact]
    public async Task Load_file_populates_channels_and_metadata_status()
    {
        var path = await WriteFileAsync(BuildSnapshot(ShaA));
        try
        {
            var vm = new XcpReplayPanelViewModel(currentA2lSha256Provider: () => ShaA);
            vm.LoadFile(path);

            var ch = Assert.Single(vm.Channels);
            Assert.Equal("EngineSpeed", ch.Name);
            Assert.Equal("rpm", ch.Unit);
            Assert.Equal(2, ch.SampleCount);
            Assert.Contains("快照", vm.MetadataSourceText);
            Assert.Equal(string.Empty, vm.WarningText);
            Assert.Equal(1, vm.RenderRequestCount);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Mismatched_sha_warns_and_drops_metadata()
    {
        var path = await WriteFileAsync(BuildSnapshot(ShaA));
        try
        {
            var vm = new XcpReplayPanelViewModel(currentA2lSha256Provider: () => ShaB);
            vm.LoadFile(path);

            Assert.NotEqual(string.Empty, vm.WarningText);
            Assert.Equal(string.Empty, Assert.Single(vm.Channels).Unit); // 元数据降级 → 单位空
            Assert.Contains("1200", Assert.Single(vm.Channels).LastValueText); // 曲线值仍可用
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task No_current_a2l_trusts_snapshot_metadata()
    {
        var path = await WriteFileAsync(BuildSnapshot(ShaA));
        try
        {
            var vm = new XcpReplayPanelViewModel(currentA2lSha256Provider: () => null);
            vm.LoadFile(path);

            Assert.Equal(string.Empty, vm.WarningText);
            Assert.Equal("rpm", Assert.Single(vm.Channels).Unit);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_file_surfaces_error_without_throw()
    {
        var bad = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s6vm_{Guid.NewGuid():N}.mf4");
        System.IO.File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });
        try
        {
            var vm = new XcpReplayPanelViewModel();
            vm.LoadFile(bad);

            Assert.Contains("失败", vm.StatusText);
            Assert.Empty(vm.Channels);
        }
        finally
        {
            System.IO.File.Delete(bad);
        }
    }

    [Fact]
    public async Task Selection_toggle_requests_render()
    {
        var path = await WriteFileAsync(BuildSnapshot(ShaA));
        try
        {
            var vm = new XcpReplayPanelViewModel();
            vm.LoadFile(path);
            var before = vm.RenderRequestCount;

            vm.Channels[0].IsSelected = true;
            Assert.Equal(before + 1, vm.RenderRequestCount);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
