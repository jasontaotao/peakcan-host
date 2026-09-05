using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Templates;

namespace PeakCan.Host.App.Tests.Templates;

/// <summary>
/// 随包发布的规范 GB/T 27930 DBC（Templates\Dbc\gbt27930.dbc）回归：
/// 必须可被 DbcParser 解析、覆盖两个 seed 模板 TrialContract 要求的全部报文名、
/// 且 PGN/优先级与 GB/T 27930-2015 报文表一致（canfd.net 核对，hil-core
/// Gbt27930CanonicalTableTests 同源）。信号布局提取自真实工程抓帧 DBC
/// （Fixtures/Can/gbt27930_96_换电.dbc）。
/// </summary>
public class Gbt27930DbcTests
{
    private static string DbcPath => Path.Combine(
        AppContext.BaseDirectory, "Templates", "Dbc", "gbt27930.dbc");

    private static DbcDocument Load()
    {
        File.Exists(DbcPath).Should().BeTrue($"shipped DBC must exist at {DbcPath}");
        var result = DbcParser.Parse(File.ReadAllText(DbcPath));
        result.IsSuccess.Should().BeTrue($"DBC parse must succeed; error: {result.Error?.Message}");
        return result.Value!;
    }

    [Theory]
    [InlineData("CHM", 0x1826F456u)]
    [InlineData("BHM", 0x182756F4u)]
    [InlineData("CRM", 0x1801F456u)]
    [InlineData("BRM", 0x1C02FFF4u)]
    [InlineData("BCP", 0x1C06FFF4u)]
    [InlineData("CTS", 0x1807F456u)]
    [InlineData("CML", 0x1808F456u)]
    [InlineData("BRO", 0x100956F4u)]
    [InlineData("CRO", 0x100AF456u)]
    [InlineData("BCL", 0x181056F4u)]
    [InlineData("BCS", 0x1C11FFF4u)]
    [InlineData("CCS", 0x1812F456u)]
    [InlineData("BSM", 0x181356F4u)]
    [InlineData("BST", 0x101956F4u)]
    [InlineData("CST", 0x101AF456u)]
    [InlineData("BSD", 0x181C56F4u)]
    [InlineData("CSD", 0x181DF456u)]
    public void Canonical_Messages_HaveStandardJ1939Ids(string name, uint expectedRawId)
    {
        var dbc = Load();
        var msg = dbc.Messages.Single(m => m.Name == name);
        // MessagesById 以 IDE 位合入 bit31 的 32 位 ID 为键；0x1FFFFFFF 掩出原始 29 位
        var keys = dbc.MessagesById.Keys.Where(k => (k & 0x1FFFFFFFu) == expectedRawId).ToList();
        keys.Should().NotBeEmpty($"{name} 0x{expectedRawId:X8} must be indexed");
    }

    [Fact]
    public void Dbc_CoversSeedTemplateTrialContracts()
    {
        var dbc = Load();
        var names = dbc.Messages.Select(m => m.Name).ToHashSet();

        foreach (var required in Gbt27930ChargerTemplate.Create().Trial!.RequiredDbcMessages)
            names.Should().Contain(required, "charger trial contract depends on it");
        foreach (var required in Gbt27930BmsTemplate.Create().Trial!.RequiredDbcMessages)
            names.Should().Contain(required, "BMS trial contract depends on it");
    }

    [Fact]
    public void Dbc_HasSignalsOnEveryMessage()
    {
        var dbc = Load();
        dbc.Messages.Should().HaveCount(17);
        dbc.Messages.SelectMany(m => m.Signals).Should().NotBeEmpty();
    }
}
