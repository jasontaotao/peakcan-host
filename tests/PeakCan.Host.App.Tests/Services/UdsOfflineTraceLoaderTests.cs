using System.IO;
using FluentAssertions;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public sealed class UdsOfflineTraceLoaderTests
{
    private static string TempPath(string extension)
        => Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");

    [Fact]
    public async Task Missing_file_throws_specific_error()
    {
        var loader = new UdsOfflineTraceLoader();
        var act = () => loader.LoadAsync(TempPath(".asc"));
        await act.Should().ThrowAsync<UdsOfflineLoadException>()
            .WithMessage("*不存在*");
    }

    [Fact]
    public async Task File_over_limit_is_rejected_explicitly()
    {
        var path = TempPath(".asc");
        await File.WriteAllBytesAsync(path, new byte[64]);
        var loader = new UdsOfflineTraceLoader(new ReplayOptions(MaxFileSizeBytes: 16));

        var act = () => loader.LoadAsync(path);

        await act.Should().ThrowAsync<UdsOfflineLoadException>()
            .WithMessage("*超过上限*16*"); // 消息里给出的是该实例实际生效的上限
    }

    [Fact]
    public async Task Asc_extension_dispatches_to_asc_parser_and_reports_format()
    {
        var path = TempPath(".asc");
        await File.WriteAllTextAsync(path,
            "date Mon Nov 26 10:00:00 2026\n" +
            " 0.000 1  7E0             Rx   d 3 02 10 01\n");

        var loader = new UdsOfflineTraceLoader();
        var result = await loader.LoadAsync(path);

        result.Format.Should().Be("ASC");
        result.Frames.Should().ContainSingle();
        result.Frames[0].Id.Should().Be(0x7E0u);
    }

    [Fact]
    public async Task Unsupported_extension_is_rejected()
    {
        var path = TempPath(".txt");
        await File.WriteAllTextAsync(path, "hello");
        var loader = new UdsOfflineTraceLoader();

        var act = () => loader.LoadAsync(path);

        await act.Should().ThrowAsync<UdsOfflineLoadException>()
            .WithMessage("*不支持*");
    }
}
