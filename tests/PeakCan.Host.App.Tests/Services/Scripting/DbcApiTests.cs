using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.Services.Scripting;

/// <summary>
/// v1.6.8 PATCH: verifies <see cref="DbcApi.Load"/> surfaces
/// <see cref="DbcService.LoadFailed"/> payload (ErrorCode + Message)
/// to ClearScript V8 callers, and distinguishes user-initiated
/// cancellation as <c>errorCode="Cancelled"</c>.
/// </summary>
public class DbcApiTests
{
    private static string WriteTempDbc(string content)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"peakcan-dbcapi-test-{Guid.NewGuid():N}.dbc");
        File.WriteAllText(path, content);
        return path;
    }

    // Reflection helper: DbcApi.Load returns Task<object> wrapping an
    // anonymous type. Avoid dynamic dispatch (RuntimeBinderException on
    // missing property) by reading the 4 known fields via reflection.
    private static (bool Success, int MessageCount, string? ErrorCode, string? Error)
        Unload(object result)
    {
        var t = result.GetType();
        return (
            (bool)t.GetProperty("success")!.GetValue(result)!,
            (int)t.GetProperty("messageCount")!.GetValue(result)!,
            (string?)t.GetProperty("errorCode")?.GetValue(result),
            (string?)t.GetProperty("error")?.GetValue(result));
    }

    private const string OneMessageDbc = """
            VERSION ""
            NS_ :
            BS_ :
            BU_: ECU1
            BO_ 256 M0: 8 ECU1
            """;

    [Fact]
    public async Task Load_Valid_Dbc_Returns_Success_True_With_MessageCount()
    {
        // Arrange
        var path = WriteTempDbc(OneMessageDbc);
        try
        {
            var dbcService = new DbcService(NullLogger<DbcService>.Instance);
            var api = new DbcApi(NullLogger<DbcApi>.Instance, dbcService);

            // Act
            var r = Unload(await api.Load(path));

            // Assert
            r.Success.Should().BeTrue();
            r.MessageCount.Should().Be(1);
            r.ErrorCode.Should().BeNull();
            r.Error.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Load_Nonexistent_Path_Returns_Success_False_With_IoError_Code()
    {
        // Arrange — DbcService swallows IO failures and fires LoadFailed
        // with ErrorCode.IoError. DbcApi.Load must surface that.
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        var api = new DbcApi(NullLogger<DbcApi>.Instance, dbcService);

        // Act
        var r = Unload(await api.Load("/this/path/does/not/exist/nope.dbc"));

        // Assert
        r.Success.Should().BeFalse();
        r.MessageCount.Should().Be(0);
        r.ErrorCode.Should().Be("IoError");
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Load_File_Exceeding_MaxFileSize_Returns_Success_False_With_DbcFileTooLarge_Code()
    {
        // Arrange — DbcService with size cap below the file's actual size.
        // v1.6.7 PATCH wires ErrorCode.DbcFileTooLarge; this test
        // regression-checks that the code reaches the script surface.
        var padded = string.Concat(OneMessageDbc, new string(' ', 1000));
        var path = WriteTempDbc(padded);
        try
        {
            var dbcService = new DbcService(
                NullLogger<DbcService>.Instance,
                new DbcOptions(MaxFileSizeBytes: 512, MaxMessageCount: 0));
            var api = new DbcApi(NullLogger<DbcApi>.Instance, dbcService);

            // Act
            var r = Unload(await api.Load(path));

            // Assert
            r.Success.Should().BeFalse();
            r.MessageCount.Should().Be(0);
            r.ErrorCode.Should().Be("DbcFileTooLarge");
            r.Error.Should().Contain("exceeds MaxFileSizeBytes 512");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Load_Empty_Path_Returns_Success_False_With_EmptyPath_Code()
    {
        // The empty-path branch in Load returns a synthetic errorCode
        // without going through LoadFailed. Exercises the early-return
        // path with the new errorCode field.
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        var api = new DbcApi(NullLogger<DbcApi>.Instance, dbcService);

        var r = Unload(await api.Load(""));

        r.Success.Should().BeFalse();
        r.MessageCount.Should().Be(0);
        r.ErrorCode.Should().Be("EmptyPath");
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Load_After_Failure_Clears_Stale_Error_On_Next_Success()
    {
        // Arrange — first load fails (nonexistent path → IoError in
        // _lastLoadError). Second load succeeds (valid DBC). D4:
        // OnDbcLoaded handler clears _lastLoadError, so the second
        // load's return must show errorCode=null.
        var path = WriteTempDbc(OneMessageDbc);
        try
        {
            var dbcService = new DbcService(NullLogger<DbcService>.Instance);
            var api = new DbcApi(NullLogger<DbcApi>.Instance, dbcService);

            // Act — first: failure
            var r1 = Unload(await api.Load("/this/path/does/not/exist/nope.dbc"));
            // Act — second: success
            var r2 = Unload(await api.Load(path));

            // Assert
            r1.Success.Should().BeFalse();
            r1.ErrorCode.Should().Be("IoError");
            r2.Success.Should().BeTrue();
            r2.MessageCount.Should().Be(1);
            r2.ErrorCode.Should().BeNull("OnDbcLoaded must clear _lastLoadError on success (D4)");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Load_After_FakeDbcService_Silent_Cancel_Returns_Cancelled_Code()
    {
        // Arrange — FakeDbcService.LoadAsync returns Task.CompletedTask
        // (no DbcLoaded, no LoadFailed fire). Mirrors the silent-cancel
        // branch in DbcService.cs:162-165 catch (OperationCanceledException).
        var fakeSvc = new FakeDbcService();
        var api = new DbcApi(NullLogger<DbcApi>.Instance, fakeSvc);

        // Act
        var r = Unload(await api.Load("/some/path"));

        // Assert
        r.Success.Should().BeFalse();
        r.MessageCount.Should().Be(0);
        r.ErrorCode.Should().Be("Cancelled");
        r.Error.Should().Be("Load was cancelled");
    }

    /// <summary>
    /// Test double for <see cref="DbcService"/>: overrides
    /// <see cref="DbcService.LoadAsync"/> to silently complete without
    /// firing <see cref="DbcService.DbcLoaded"/> or
    /// <see cref="DbcService.LoadFailed"/>. Mirrors the silent-cancel
    /// branch in <c>DbcService.cs:162-165</c>
    /// (<c>catch (OperationCanceledException) { }</c>) so Test 6 can
    /// exercise the "Cancelled" code path in
    /// <see cref="DbcApi.Load"/> without a real cancellation token.
    /// <para>
    /// Same pattern as
    /// <c>AppShellViewModelTests.cs:54-59</c> — each test file owns its
    /// own private nested <c>FakeDbcService</c> per established convention.
    /// </para>
    /// </summary>
    private sealed class FakeDbcService : DbcService
    {
        public FakeDbcService()
            : base(NullLogger<DbcService>.Instance)
        {
        }

        public override Task LoadAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
