using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class NmPromptRulesTests
{
    [Fact]
    public void BuildM1_RequiresProfileFirst()
    {
        var text = NmPromptRules.BuildM1();
        Assert.Contains("get_nm_profile", text);
        Assert.Contains("先", text);
    }

    [Fact]
    public void BuildM1_ForbidsSleepInferenceFromAbsence()
    {
        var text = NmPromptRules.BuildM1();
        Assert.Contains("睡眠", text);
        Assert.Contains("没有 NM 帧", text);
        Assert.Contains("不等于", text);
    }

    [Fact]
    public void BuildM1_ForbidsGuessingMissingInputs()
    {
        var text = NmPromptRules.BuildM1();
        Assert.Contains("missing_inputs", text);
        Assert.Contains("不许", text);
        Assert.Contains("补全", text);
    }

    [Fact]
    public void BuildM1_ForbidsReferencingM2Fields()
    {
        // spec §8.2 规则 9：提示词必须明确“不引用”三个 M2 字段，而不是只做到
        // 字面不出现（audit ai.md #8 指出旧测试只守卫了后者）。
        var text = NmPromptRules.BuildM1();
        Assert.Contains("不引用", text);
        Assert.Contains("standard_id", text);
        Assert.Contains("profile_hash", text);
        Assert.Contains("unmodeled_rules", text);
        Assert.Contains("M1", text);
    }

    [Fact]
    public void BuildM1_PrefersOverviewToLimitToolCalls()
    {
        // spec §8.4：优先用 overview，避免一轮调用过多工具。
        var text = NmPromptRules.BuildM1();
        Assert.Contains("优先", text);
        Assert.Contains("get_nm_overview", text);
        Assert.Contains("避免一轮调用过多工具", text);
    }

    [Fact]
    public void BuildM1_RequiresEvidenceGrading()
    {
        // spec §8.3：回答必须区分测到的事实/推断/不可知，并给出两条禁令。
        var text = NmPromptRules.BuildM1();
        Assert.Contains("测到的事实", text);
        Assert.Contains("推断", text);
        Assert.Contains("不可知", text);
        Assert.Contains("NM_CONFIG_CONFLICT", text);
        Assert.Contains("配置错了", text);
        Assert.Contains("NID 样本", text);
        Assert.Contains("NID 正常", text);
        // findings 用 is_inference 布尔，需要提示词给出映射，否则模型无法分级。
        Assert.Contains("is_inference", text);
    }

    [Fact]
    public void BuildM1_SilencePhasesAreInferredNotMeasured()
    {
        var text = NmPromptRules.BuildM1();

        Assert.Contains("silence_phases", text);
        Assert.Contains("不得写成 measured", text);
        // 不新增编号规则：1–10 的编号语义保持不变。
        for (var i = 1; i <= 10; i++) Assert.Contains($"\n{i}. ", text);
    }

    [Fact]
    public void BuildM1_KeepsTenRuleNumberingAndRule6Example()
    {
        // 现有 1–10 条编号语义保持不变；规则 9 必须逐字指向 M2 三字段。
        var text = NmPromptRules.BuildM1();
        for (var i = 1; i <= 10; i++)
            Assert.Contains($"\n{i}. ", text);
        Assert.Contains("\n9. 不引用 standard_id/profile_hash/unmodeled_rules（M1 不存在）。", text);
        Assert.Contains("如查看某信号/某报文", text);
    }
}
