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
    public void BuildM1_DoesNotMentionM2Fields()
    {
        var text = NmPromptRules.BuildM1();
        Assert.DoesNotContain("standard_id", text);
        Assert.DoesNotContain("profile_hash", text);
        Assert.DoesNotContain("unmodeled_rules", text);
    }
}
