using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// v1.6.6 PATCH Item 1 + v1.6.7 PATCH Item 1: verifies the
/// <see cref="DbcService"/> in-service caps for file size and
/// message count. Mirrors v1.6.5 PATCH's opt-in config schema;
/// both caps default to <see cref="DbcOptions.Unlimited"/>.
/// <para>
/// Cap rejections surface via the shared <see cref="Error"/>
/// envelope. v1.6.7 PATCH adds <c>ErrorCode.DbcFileTooLarge</c>
/// for the size-cap path (categorical code distinct from generic
/// <c>ParseFailure</c>). The message-count cap path retains
/// <see cref="ErrorCode.ParseFailure"/> (mid-parse reuse, matches
/// v1.6.6 design doc Decision 3 spirit). Both paths additionally
/// carry the disambiguating <c>Message</c> string
/// ("exceeds MaxFileSizeBytes N" / "exceeds MaxMessageCount N")
/// to identify which cap fired.
/// </para>
/// </summary>
public class DbcServiceLimitTests
{
    // Reused 1-arg ctor → unlimited caps (back-compat path).
    private static DbcService NewUnlimited() =>
        new(NullLogger<DbcService>.Instance, DbcOptions.Unlimited);

    // 2-arg ctor with explicit caps for size / count tests.
    private static DbcService NewWith(long maxFileSizeBytes, int maxMessageCount) =>
        new(NullLogger<DbcService>.Instance, new DbcOptions(maxFileSizeBytes, maxMessageCount));

    private static string WriteTempDbc(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"peakcan-dbc-limit-{Guid.NewGuid():N}.dbc");
        File.WriteAllText(path, content);
        return path;
    }

    private const string OneMessageDbc = """
            VERSION ""
            NS_ :
            BS_ :
            BU_: ECU1
            BO_ 256 M0: 8 ECU1
            """;

    private const string FiveMessageDbc = """
            VERSION ""
            NS_ :
            BS_ :
            BU_: ECU1
            BO_ 256 M0: 8 ECU1
            BO_ 257 M1: 8 ECU1
            BO_ 258 M2: 8 ECU1
            BO_ 259 M3: 8 ECU1
            BO_ 260 M4: 8 ECU1
            """;

    [Fact]
    public async Task LoadAsync_File_Above_MaxFileSize_Fires_LoadFailed_With_DbcFileTooLarge_Error_Code()
    {
        // Arrange — one-message DBC padded to > 512 bytes; cap at 512.
        var padded = string.Concat(OneMessageDbc, new string(' ', 1000));
        var path = WriteTempDbc(padded);
        try
        {
            var svc = NewWith(maxFileSizeBytes: 512, maxMessageCount: 0);
            Error? captured = null;
            DbcDocument? loadedDoc = null;
            svc.LoadFailed += e => captured = e;
            svc.DbcLoaded += doc => loadedDoc = doc;

            // Act
            await svc.LoadAsync(path);

            // Assert — size cap fires via ErrorCode.DbcFileTooLarge
            // (v1.6.7 PATCH Item 1 categorical error code). Disambiguating
            // Message identifies the cap value.
            captured.Should().NotBeNull("size cap should fire LoadFailed");
            captured!.Code.Should().Be(ErrorCode.DbcFileTooLarge);
            captured.Message.Should().Contain("exceeds MaxFileSizeBytes 512");
            loadedDoc.Should().BeNull("DbcLoaded must NOT fire when LoadFailed fires");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_File_Below_MaxFileSize_Fires_DbcLoaded()
    {
        // Arrange — small DBC, cap at 10 KB (well above file size).
        var path = WriteTempDbc(OneMessageDbc);
        try
        {
            var svc = NewWith(maxFileSizeBytes: 10_000, maxMessageCount: 0);
            DbcDocument? captured = null;
            svc.DbcLoaded += doc => captured = doc;

            // Act
            await svc.LoadAsync(path);

            // Assert
            captured.Should().NotBeNull();
            captured!.Messages.Should().HaveCount(1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_Both_Caps_Zero_Unlimited()
    {
        // Arrange — 5-message DBC, both caps 0.
        var path = WriteTempDbc(FiveMessageDbc);
        try
        {
            var svc = NewUnlimited();
            DbcDocument? captured = null;
            svc.DbcLoaded += doc => captured = doc;

            // Act
            await svc.LoadAsync(path);

            // Assert — unlimited path passes all 5.
            captured.Should().NotBeNull();
            captured!.Messages.Should().HaveCount(5);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_MessageCount_Exceeds_Cap_Fires_LoadFailed_With_ParseFailure()
    {
        // Arrange — 5-message DBC, cap at 3.
        var path = WriteTempDbc(FiveMessageDbc);
        try
        {
            var svc = NewWith(maxFileSizeBytes: 0, maxMessageCount: 3);
            Error? captured = null;
            DbcDocument? loadedDoc = null;
            svc.LoadFailed += e => captured = e;
            svc.DbcLoaded += doc => loadedDoc = doc;

            // Act
            await svc.LoadAsync(path);

            // Assert — message-count cap fires, DbcLoaded does NOT fire.
            captured.Should().NotBeNull();
            captured!.Code.Should().Be(ErrorCode.ParseFailure);
            captured.Message.Should().Contain("exceeds MaxMessageCount 3");
            loadedDoc.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_Low_Both_Caps_Rejects_Large_Real_Fixture()
    {
        // Arrange — use the E51_PT_CAN-BMS.dbc real fixture (77 KB, 256 messages).
        // BOTH caps below fixture size/count. Path.GetFullPath resolves the
        // `../../../../../tests/...` segments to a canonical absolute path
        // so PathNormalizer.Normalize (defense-in-depth in DbcService.LoadAsync
        // + ReadAllBytesAsync) doesn't reject the `..` segments.
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "tests", "PeakCan.Host.Core.Tests", "E51_PT_CAN-BMS.dbc"));
        if (!File.Exists(fixturePath))
        {
            // Skip if fixture not found (CI without fixture copy).
            return;
        }

        var svc = NewWith(maxFileSizeBytes: 10_000, maxMessageCount: 100);
        Error? captured = null;
        svc.LoadFailed += e => captured = e;

        // Act
        await svc.LoadAsync(fixturePath);

        // Assert — either size cap or message-count cap fires first.
        captured.Should().NotBeNull(
            "E51_PT_CAN-BMS.dbc (256 messages / ~77 KB) should reject when both caps are at 10 KB / 100 msgs");
        // v1.6.7 PATCH: size cap emits ErrorCode.DbcFileTooLarge; message-count
        // cap emits ErrorCode.ParseFailure. Either path is acceptable for this
        // combined-cap test (file trips both, whichever fires first wins).
        captured!.Code.Should().Match(c =>
            c == ErrorCode.DbcFileTooLarge || c == ErrorCode.ParseFailure);
        captured.Message.Should().Match(m =>
            m.Contains("MaxFileSizeBytes") || m.Contains("MaxMessageCount"));
    }

    // v1.6.7 PATCH Item 3: concurrent caller test characterizing the as-built
    // last-write-wins concurrency model for DbcService.LoadAsync. No locking;
    // see v1.6.7 design doc Decision 6.
    [Fact]
    public async Task LoadAsync_Concurrent_Calls_All_Converge_Without_Race_Or_Exception()
    {
        // Arrange — single DbcService with unlimited caps. Multiple concurrent
        // LoadAsync calls on different temp DBC files. Each LoadAsync call uses
        // an independent file so cap mechanics don't matter; this test only
        // verifies the concurrency surface (no exceptions, last-write-wins).
        var svc = NewUnlimited();
        var failCount = 0;
        var loadCount = 0;
        var failLock = new object();
        var loadLock = new object();
        svc.LoadFailed += _ => { lock (failLock) failCount++; };
        svc.DbcLoaded += _ => { lock (loadLock) loadCount++; };

        var paths = Enumerable.Range(0, 10)
            .Select(_ => WriteTempDbc(OneMessageDbc))
            .ToList();

        try
        {
            // Act — fire 10 concurrent LoadAsync calls (Task.WhenAll per
            // UdsClientConcurrentSecurityAccessTests precedent).
            await Task.WhenAll(paths.Select(p => svc.LoadAsync(p)));

            // Assert — for unlimited-cap path with valid 1-message DBCs,
            // every call should succeed: DbcLoaded fires 10 times, LoadFailed 0.
            // No exception should bubble to the caller.
            loadCount.Should().Be(paths.Count);
            failCount.Should().Be(0);
            svc.Current.Should().NotBeNull("last-write-wins leaves Current with one of the loaded docs");
        }
        finally
        {
            foreach (var p in paths) File.Delete(p);
        }
    }
}
