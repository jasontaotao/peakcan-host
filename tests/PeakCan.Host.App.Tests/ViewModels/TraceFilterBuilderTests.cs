using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// P1 过滤条件构建器：把「字段 → 值」结构化地写进既有过滤字段。
/// 参照 <see cref="TraceRowContextTests"/> 的 MTA 直驱模式——不依赖 WPF Application，
/// 断言落到 EntriesView 的最终过滤结果与 chip 条，而不只是属性写入。
/// </summary>
public class TraceFilterBuilderTests
{
    private static CanFrame Frame(uint id, bool isError = false)
        => new(new CanId(id, FrameFormat.Standard),
               new byte[] { 0x01, 0x02 },
               isError ? FrameFlags.ErrFrame : FrameFlags.None,
               ChannelId.None,
               Timestamp.FromMicroseconds(1_000_000UL));

    private static void SelectField(TraceViewModel vm, string key)
        => vm.SelectedBuilderField = vm.FilterBuilderFields.Single(f => f.Key == key);

    [Fact]
    public void Add_Id_Writes_AllowList_And_Applies_Filter()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });

        SelectField(vm, FilterBuilderField.Id);
        vm.BuilderValue = "0x100";
        vm.AddFilterConditionCommand.Execute(null);

        vm.IdListText.Should().Be("0x100");
        vm.EntriesView.Cast<TraceEntry>().Select(e => e.Id.Raw).Should().BeEquivalentTo(new uint[] { 0x100 });
        vm.FilterChips.Should().Contain(c => c.Label.Contains("0x100"));
        vm.IsBuilderOpen.Should().BeFalse("添加成功后弹层关闭");
        vm.BuilderValue.Should().BeEmpty();
    }

    [Fact]
    public void Add_Id_Appends_To_Existing_List_And_Skips_Duplicates()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200), Frame(0x300) });
        vm.IdListText = "0x100";

        SelectField(vm, FilterBuilderField.Id);
        vm.BuilderValue = "0x100, 0x200";   // 0x100 已存在
        vm.AddFilterConditionCommand.Execute(null);

        vm.IdListText.Should().Be("0x100, 0x200", "追加时去重，且不打断用户已有顺序");
        vm.EntriesView.Count.Should().Be(2);
    }

    [Fact]
    public void Add_Invalid_Id_Reports_Error_And_Writes_Nothing()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100) });

        SelectField(vm, FilterBuilderField.Id);
        vm.OpenFilterBuilderCommand.Execute(null);   // 用户已打开弹层
        vm.BuilderValue = "0x100, zzz";
        vm.AddFilterConditionCommand.Execute(null);

        vm.BuilderError.Should().Contain("zzz");
        vm.IdListText.Should().BeEmpty("非法输入不得部分写入");
        vm.IsBuilderOpen.Should().BeTrue("出错时弹层保持打开，用户可原地改");
        vm.EntriesView.Count.Should().Be(1);
    }

    [Fact]
    public void Add_Excluded_Id_Uses_Exclusion_Dimension_Without_Touching_AllowList()
    {
        // 排除是独立的黑名单维度：绝不能写成 IdListText（那会把其它条件整体反转）。
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });

        SelectField(vm, FilterBuilderField.ExcludedId);
        vm.BuilderValue = "0x200";
        vm.AddFilterConditionCommand.Execute(null);

        vm.IdListText.Should().BeEmpty();
        vm.ExcludeMatch.Should().BeFalse();
        vm.EntriesView.Cast<TraceEntry>().Select(e => e.Id.Raw).Should().BeEquivalentTo(new uint[] { 0x100 });
        vm.FilterChips.Should().Contain(c => c.Kind == FilterChipKinds.ExcludedId);
    }

    [Fact]
    public void Add_Dbc_Choice_Writes_Message_Name()
    {
        var vm = new TraceViewModel();

        SelectField(vm, FilterBuilderField.Dbc);
        vm.SelectedBuilderChoice = "EEC1";
        vm.AddFilterConditionCommand.Execute(null);

        vm.DbcMessageName.Should().Be("EEC1");
        vm.IsBuilderOpen.Should().BeFalse();
    }

    [Fact]
    public void Add_Dbc_Without_Choice_Reports_Error()
    {
        var vm = new TraceViewModel();

        SelectField(vm, FilterBuilderField.Dbc);
        vm.SelectedBuilderChoice = null;
        vm.AddFilterConditionCommand.Execute(null);

        vm.BuilderError.Should().NotBeNullOrEmpty();
        vm.DbcMessageName.Should().BeEmpty();
    }

    [Fact]
    public void Add_Sa_Normalizes_To_Hex_Byte()
    {
        var vm = new TraceViewModel();

        SelectField(vm, FilterBuilderField.Sa);
        vm.BuilderValue = "f4";
        vm.AddFilterConditionCommand.Execute(null);

        vm.SaText.Should().Be("0xF4", "构建器把输入归一成 0xNN，避免 hex/十进制歧义");
    }

    [Fact]
    public void Add_Invalid_Sa_Reports_Error()
    {
        var vm = new TraceViewModel();

        SelectField(vm, FilterBuilderField.Sa);
        vm.BuilderValue = "1FF";
        vm.AddFilterConditionCommand.Execute(null);

        vm.BuilderError.Should().NotBeNullOrEmpty();
        vm.SaText.Should().BeEmpty();
    }

    [Fact]
    public void Add_ErrorsOnly_Sets_Flag()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200, isError: true) });

        SelectField(vm, FilterBuilderField.ErrorsOnly);
        vm.AddFilterConditionCommand.Execute(null);

        vm.ShowErrorsOnly.Should().BeTrue();
        vm.EntriesView.Cast<TraceEntry>().Select(e => e.Id.Raw).Should().BeEquivalentTo(new uint[] { 0x200 });
    }

    [Fact]
    public void Add_Payload_Writes_All_Three_Fields()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });

        SelectField(vm, FilterBuilderField.Payload);
        vm.BuilderPayloadOffset = "0";
        vm.BuilderPayloadMask = "0xFF";
        vm.BuilderPayloadValue = "0x01";
        vm.AddFilterConditionCommand.Execute(null);

        vm.PayloadOffsetText.Should().Be("0");
        vm.PayloadMaskHex.Should().Be("0xFF");
        vm.BuilderError.Should().BeNullOrEmpty();
        vm.FilterChips.Should().Contain(c => c.Kind == FilterChipKinds.Payload);
    }

    [Fact]
    public void Add_Partial_Payload_Reports_Error_And_Writes_Nothing()
    {
        var vm = new TraceViewModel();

        SelectField(vm, FilterBuilderField.Payload);
        vm.BuilderPayloadOffset = "0";
        vm.AddFilterConditionCommand.Execute(null);

        vm.BuilderError.Should().Contain("同时填写");
        vm.PayloadOffsetText.Should().BeEmpty();
        vm.PayloadMaskHex.Should().BeEmpty();
        vm.PayloadValueHex.Should().BeEmpty();
    }

    [Fact]
    public void Add_Pgn_Appends_And_Rejects_Out_Of_Range()
    {
        var vm = new TraceViewModel();

        SelectField(vm, FilterBuilderField.Pgn);
        vm.BuilderValue = "0xFEF1";
        vm.AddFilterConditionCommand.Execute(null);
        vm.PgnText.Should().Be("0xFEF1");

        vm.BuilderValue = "0x40000";   // > 0x3FFFF
        vm.AddFilterConditionCommand.Execute(null);
        vm.BuilderError.Should().Contain("PGN");
        vm.PgnText.Should().Be("0xFEF1", "越界值不得写入");
    }

    [Fact]
    public void Add_Id_Is_Rejected_In_Exclude_Mode_Instead_Of_Inverting_Intent()
    {
        // 「排除（整体取反）」生效时，新增 allow-list 条件会把结果整体反转——
        // 用户想看的 ID 反而被隐藏。必须拒绝而不是静默写反。
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });
        vm.ExcludeMatch = true;   // 空 allow-list + 整体取反 → 全部隐藏（既有语义）
        var before = vm.EntriesView.Count;

        SelectField(vm, FilterBuilderField.Id);
        vm.BuilderValue = "0x100";
        vm.AddFilterConditionCommand.Execute(null);

        vm.BuilderError.Should().Contain("排除");
        vm.IdListText.Should().BeEmpty("拒绝时不得写入任何字段");
        vm.EntriesView.Count.Should().Be(before, "拒绝时表格不得变化");
    }

    [Fact]
    public void Add_Is_Rejected_When_Advanced_Text_Is_Already_Invalid()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100) });
        vm.IdListText = "zzz";

        SelectField(vm, FilterBuilderField.Id);
        vm.BuilderValue = "0x100";
        vm.AddFilterConditionCommand.Execute(null);

        vm.BuilderError.Should().Contain("非法输入");
        vm.IdListText.Should().Be("zzz", "先让用户修好高级区，不叠加难以解释的去重行为");
    }

    [Fact]
    public void Add_Excluded_Id_Still_Works_When_Advanced_Text_Is_Invalid()
    {
        // 排除路径不读 IdListText（ApplyExclusionChange 本就支持非法态）：
        // 若被"先修好高级区"的守卫一起拦下，就与右键"排除这个 ID"分叉了。
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });
        vm.IdListText = "zzz";

        SelectField(vm, FilterBuilderField.ExcludedId);
        vm.BuilderValue = "0x200";
        vm.AddFilterConditionCommand.Execute(null);

        vm.BuilderError.Should().BeNullOrEmpty();
        vm.FilterChips.Should().Contain(c => c.Kind == FilterChipKinds.ExcludedId);
    }

    [Fact]
    public void Add_Id_Out_Of_Range_Is_Rejected()
    {
        var vm = new TraceViewModel();

        SelectField(vm, FilterBuilderField.Id);
        vm.BuilderValue = "0x20000000";   // > CanId.Raw 上限 0x1FFFFFFF
        vm.AddFilterConditionCommand.Execute(null);

        vm.BuilderError.Should().Contain("超出范围");
        vm.IdListText.Should().BeEmpty();
    }

    [Fact]
    public void Switching_Field_Clears_Stale_Value()
    {
        // ID 里输了 0x100 后改选 PGN：残留值在 PGN 语义下依然"合法"，
        // 不清就会把 ID 当 PGN 写进去。
        var vm = new TraceViewModel();

        SelectField(vm, FilterBuilderField.Id);
        vm.BuilderValue = "0x100";
        SelectField(vm, FilterBuilderField.Pgn);

        vm.BuilderValue.Should().BeEmpty();

        vm.AddFilterConditionCommand.Execute(null);
        vm.BuilderError.Should().Contain("请输入 PGN");
        vm.PgnText.Should().BeEmpty();
    }

    [Fact]
    public void Switching_Field_Clears_Previous_Error_And_Switches_Value_Mode()
    {
        var vm = new TraceViewModel();

        SelectField(vm, FilterBuilderField.Id);
        vm.BuilderValue = "zzz";
        vm.AddFilterConditionCommand.Execute(null);
        vm.BuilderError.Should().NotBeNullOrEmpty();

        SelectField(vm, FilterBuilderField.ErrorsOnly);

        vm.BuilderError.Should().BeNullOrEmpty();
        vm.IsBuilderValueVisible.Should().BeFalse("flag 字段没有值输入框");
        vm.IsBuilderFlagVisible.Should().BeTrue();

        SelectField(vm, FilterBuilderField.Dbc);
        vm.IsBuilderChoiceVisible.Should().BeTrue("DBC 字段用下拉选择");
        vm.IsBuilderValueVisible.Should().BeFalse();
    }
}
