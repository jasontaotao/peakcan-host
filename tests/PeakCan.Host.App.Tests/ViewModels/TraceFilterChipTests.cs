using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// P0-3：过滤 chip 条。每个 chip 删除后必须改写底层字段、走解析管线，
/// 并让 EntriesView 反映新过滤结果（不是只删 UI 标签）。
/// </summary>
public class TraceFilterChipTests
{
    private static CanFrame Frame(uint id) => new(
        new CanId(id, FrameFormat.Standard),
        Array.Empty<byte>(),
        FrameFlags.None,
        ChannelId.None,
        Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public void Id_Tokens_Become_Chips_And_Removal_Updates_Text_And_View()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });
        vm.IdListText = "0x100, 0x200";

        vm.FilterChips.Where(c => c.Kind == FilterChipKinds.Id).Should().HaveCount(2);

        var chip = vm.FilterChips.Single(c => c.Label == "ID 0x100");
        chip.RemoveCommand.Execute(null);

        vm.IdListText.Should().Be("0x200");
        vm.EntriesView.Count.Should().Be(1);
        vm.FilterChips.Where(c => c.Kind == FilterChipKinds.Id).Should().HaveCount(1);
    }

    [Fact]
    public void Removing_Middle_Token_Then_First_Keeps_Remaining_Order()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200), Frame(0x300) });
        vm.IdListText = "0x100, 0x200, 0x300";

        vm.FilterChips.Single(c => c.Label == "ID 0x200").RemoveCommand.Execute(null);
        vm.IdListText.Should().Be("0x100, 0x300");

        vm.FilterChips.Single(c => c.Label == "ID 0x100").RemoveCommand.Execute(null);
        vm.IdListText.Should().Be("0x300");
        vm.EntriesView.Count.Should().Be(1);
    }

    [Fact]
    public void Pgn_And_Dbc_Chips_Remove_Their_Fields()
    {
        var vm = new TraceViewModel();
        vm.PgnText = "0xEA00";
        vm.DbcMessageName = "EEC1";

        vm.FilterChips.Single(c => c.Kind == FilterChipKinds.Pgn).RemoveCommand.Execute(null);
        vm.PgnText.Should().BeEmpty();

        vm.FilterChips.Single(c => c.Kind == FilterChipKinds.Dbc).RemoveCommand.Execute(null);
        vm.DbcMessageName.Should().BeEmpty();
    }

    [Fact]
    public void Toggle_And_Field_Chips_Are_Removable()
    {
        var vm = new TraceViewModel();
        vm.ShowErrorsOnly = true;
        vm.ExcludeMatch = true;
        vm.ChannelFilter = new ChannelId(0x51);

        vm.FilterChips.Should().Contain(c => c.Kind == FilterChipKinds.ErrorsOnly);
        vm.FilterChips.Should().Contain(c => c.Kind == FilterChipKinds.ExcludeMode);
        vm.FilterChips.Should().Contain(c => c.Kind == FilterChipKinds.Channel);

        vm.FilterChips.Single(c => c.Kind == FilterChipKinds.ErrorsOnly).RemoveCommand.Execute(null);
        vm.FilterChips.Single(c => c.Kind == FilterChipKinds.ExcludeMode).RemoveCommand.Execute(null);
        vm.FilterChips.Single(c => c.Kind == FilterChipKinds.Channel).RemoveCommand.Execute(null);

        vm.ShowErrorsOnly.Should().BeFalse();
        vm.ExcludeMatch.Should().BeFalse();
        vm.ChannelFilter.Should().BeNull();
        vm.FilterChips.Should().BeEmpty();
    }

    [Fact]
    public void Payload_Chip_Removal_Clears_All_Three_Fields()
    {
        var vm = new TraceViewModel();
        vm.PayloadOffsetText = "0";
        vm.PayloadMaskHex = "FF";
        vm.PayloadValueHex = "2A";

        var chip = vm.FilterChips.Single(c => c.Kind == FilterChipKinds.Payload);
        chip.RemoveCommand.Execute(null);

        vm.PayloadOffsetText.Should().BeEmpty();
        vm.PayloadMaskHex.Should().BeEmpty();
        vm.PayloadValueHex.Should().BeEmpty();
        vm.FilterChips.Should().NotContain(c => c.Kind == FilterChipKinds.Payload);
    }

    [Fact]
    public void Excluded_Id_Chip_Removal_Restores_Row()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x123), Frame(0x456) });
        vm.ExcludeThisIdCommand.Execute(vm.Entries[0]);

        var chip = vm.FilterChips.Single(c => c.Kind == FilterChipKinds.ExcludedId);
        chip.Label.Should().Be("排除 0x123");

        chip.RemoveCommand.Execute(null);

        vm.EntriesView.Count.Should().Be(2);
        vm.FilterChips.Should().NotContain(c => c.Kind == FilterChipKinds.ExcludedId);
    }

    [Fact]
    public void Invalid_Token_Still_Appears_As_Chip()
    {
        var vm = new TraceViewModel();
        vm.IdListText = "nothex";

        vm.FilterErrorText.Should().NotBeNull();
        vm.FilterChips.Should().Contain(c =>
            c.Kind == FilterChipKinds.Id && c.Label == "ID nothex");
    }

    [Fact]
    public void No_Filters_Means_No_Chips()
    {
        var vm = new TraceViewModel();
        vm.FilterChips.Should().BeEmpty();
    }
}
