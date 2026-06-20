using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;

namespace PeakCan.Host.Core.Tests;

/// <summary>
/// Integration smoke test for a real-world Vector-generated DBC file
/// (BMS, ~77 KB, 256 messages, 820 signals, 10 VAL_TABLE_ entries, 105
/// BA_DEF_ attributes). Guards against regressions when the NS_ block
/// is filled with the full Vector "new symbol" list (NS_DESC_, CM_,
/// BA_DEF_, BA_, VAL_, CAT_DEF_, CAT_, FILTER, BA_DEF_DEF_,
/// EV_DATA_, ENVVAR_DATA_, SGTYPE_, SGTYPE_VAL_, BA_DEF_SGTYPE_,
/// BA_SGTYPE_, SIG_TYPE_REF_, VAL_TABLE_, SIG_GROUP_, SIG_VALTYPE_,
/// SIGTYPE_VALTYPE_, BO_TX_BU_, BA_DEF_REL_, BA_REL_, BA_DEF_DEF_REL_,
/// BU_SG_REL_, BU_EV_REL_) — a pattern not exercised by the
/// synthetic unit tests in <see cref="DbcParserTests"/>.
/// </summary>
public class E51PTCANBMSDbcFixtureTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "E51_PT_CAN-BMS.dbc");

    [Fact]
    public void Loads_E51_PT_CAN_BMS_Dbc_Successfully()
    {
        File.Exists(FixturePath)
            .Should().BeTrue($"fixture must be copied next to the test binary (looked in {FixturePath})");

        var text = File.ReadAllText(FixturePath);
        var r = DbcParser.Parse(text);

        if (!r.IsSuccess)
        {
            throw new Xunit.Sdk.XunitException(
                $"Parse failed: code={r.Error!.Code} message={r.Error.Message}");
        }

        // Tripwire: parse must succeed. The previous regression — NS_ block
        // terminating at VAL_/VAL_TABLE_ and leaking into ParseValForSignal
        // — surfaced as a hard "VAL_: unknown message 'CAT_DEF_'" parse
        // failure on this exact file. Asserting only lower bounds keeps the
        // test stable when the fixture is re-saved (Vector CANdb++ may
        // reorder or re-emit metadata blocks without touching the message
        // catalog in any meaningful way).
        r.Value!.Messages.Should().NotBeEmpty();
        r.Value.ValueTables.Should().NotBeEmpty();
        r.Value.Nodes.Should().NotBeEmpty();
        r.Value.Messages.Sum(m => m.Signals.Count).Should().BeGreaterThan(0,
            "no signals parsed — SG_ block parsing likely broken");
    }
}
