using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Security.Keystore;
using Xunit;

namespace PeakCan.Host.Cli.Tests;

/// <summary>
/// SecOc key management CLI command tests. Logic tests use InMemoryKeyStore via
/// the factory seam; one integration test exercises the real DPAPI store.
/// </summary>
[SupportedOSPlatform("windows")]
public class SecOcKeyCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryKeyStore _store;
    private readonly StringWriter _stdout;
    private readonly StringWriter _stderr;

    public SecOcKeyCommandTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("secoc-key-cli-").FullName;
        _store = new InMemoryKeyStore();
        _stdout = new StringWriter();
        _stderr = new StringWriter();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _stdout.Dispose();
        _stderr.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteKeyFile(string name, string hex)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, hex, Encoding.ASCII);
        return path;
    }

    private string KeyFile(params string[] hexLines) => WriteKeyFile("k.hex", string.Join('\n', hexLines));

    private int Run(string command, string? keyId = null, string? keyFile = null,
        string? storeDir = null, string? entropy = null)
        => SecOcKeyCommand.Run(new CliArgs(
            DbcPath: "", SuitePath: "",
            SecOcKeyCommand: command,
            SecOcKeyId: keyId,
            SecOcKeyPath: keyFile,
            SecOcStoreDir: storeDir,
            SecOcEntropy: entropy), (_, _) => _store, _stdout, _stderr);

    private const string ValidKeyHex = "00112233445566778899aabbccddeeff";

    // ---- import ----

    [Fact]
    public void Import_WritesKeyIntoStore()
    {
        var exit = Run("import", keyId: "demo", keyFile: KeyFile(ValidKeyHex));

        exit.Should().Be(0);
        _store.Contains("demo").Should().BeTrue();
        _store.GetKey("demo").Should().Equal(Convert.FromHexString(ValidKeyHex));
    }

    [Fact]
    public void Import_ToleratesWhitespaceInKeyFile()
    {
        var exit = Run("import", keyId: "demo", keyFile: KeyFile("00112233", "445566778899aabbccddeeff"));

        exit.Should().Be(0);
        _store.GetKey("demo").Should().HaveCount(16);
    }

    [Fact]
    public void Import_RejectsWrongKeyLength()
    {
        var exit = Run("import", keyId: "demo", keyFile: KeyFile("00112233"));

        exit.Should().Be(2);
        _store.Contains("demo").Should().BeFalse();
    }

    [Fact]
    public void Import_RejectsBadHex()
    {
        var exit = Run("import", keyId: "demo", keyFile: KeyFile("zz112233445566778899aabbccddeeff"));

        exit.Should().Be(2);
        _store.Contains("demo").Should().BeFalse();
    }

    [Fact]
    public void Import_MissingKeyFile_Fails()
    {
        var exit = Run("import", keyId: "demo", keyFile: Path.Combine(_tempDir, "nope.hex"));

        exit.Should().Be(2);
    }

    [Fact]
    public void Import_MissingKeyFileArgument_Fails()
    {
        var exit = Run("import", keyId: "demo");

        exit.Should().Be(2);
    }

    [Fact]
    public void Import_MissingKeyId_Fails()
    {
        var exit = Run("import", keyFile: KeyFile(ValidKeyHex));

        exit.Should().Be(2);
    }

    // ---- list ----

    [Fact]
    public void List_EmptyStore_ExitsZero()
    {
        var exit = Run("list");

        exit.Should().Be(0);
        _stdout.ToString().Should().Contain("(empty)");
    }

    [Fact]
    public void List_PrintsKeyIds()
    {
        _store.SetKey("alpha", new byte[16]);
        _store.SetKey("beta", new byte[16]);

        var exit = Run("list");

        exit.Should().Be(0);
        var outText = _stdout.ToString();
        outText.Should().Contain("alpha");
        outText.Should().Contain("beta");
    }

    // ---- remove ----

    [Fact]
    public void Remove_ExistingKey_Succeeds()
    {
        _store.SetKey("demo", new byte[16]);

        var exit = Run("remove", keyId: "demo");

        exit.Should().Be(0);
        _store.Contains("demo").Should().BeFalse();
    }

    [Fact]
    public void Remove_MissingKey_FailsWithOne()
    {
        var exit = Run("remove", keyId: "ghost");

        exit.Should().Be(1);
    }

    [Fact]
    public void Run_UnknownCommand_FailsWithTwo()
    {
        var exit = Run("rotate");

        exit.Should().Be(2);
    }

    // ---- DPAPI integration (real store, Windows CurrentUser scope) ----

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void DpapiStore_ImportThenSecondInstanceReadsKey()
    {
        var storeDir = Path.Combine(_tempDir, "dpapi-store");
        var keyFile = KeyFile(ValidKeyHex);

        var exitImport = SecOcKeyCommand.Run(new CliArgs(
            DbcPath: "", SuitePath: "",
            SecOcKeyCommand: "import", SecOcKeyId: "dpapi-demo", SecOcKeyPath: keyFile,
            SecOcStoreDir: storeDir, SecOcEntropy: "e2e"),
            SecOcKeyCommand.DefaultStoreFactory, _stdout, _stderr);
        var exitList = SecOcKeyCommand.Run(new CliArgs(
            DbcPath: "", SuitePath: "",
            SecOcKeyCommand: "list", SecOcStoreDir: storeDir, SecOcEntropy: "e2e"),
            SecOcKeyCommand.DefaultStoreFactory, _stdout, _stderr);

        exitImport.Should().Be(0);
        exitList.Should().Be(0);
        var reload = new DpapiKeyStore(storeDir, "e2e");
        reload.GetKey("dpapi-demo").Should().Equal(Convert.FromHexString(ValidKeyHex));
    }
}
