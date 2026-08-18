using PeakCan.HIL.Core.HIL;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.StepParams;

public class StepParametersFactoryTests
{
    [Fact]
    public void Create_WaitForSignal_CorrectFields()
    {
        var p = new Dictionary<string, object>
        {
            ["SignalName"] = "BMS_Status.EngineRPM",
            ["Expected"] = 3000.0,
            ["Tolerance"] = 50.0,
            ["TimeoutMs"] = 5000,
        };

        var result = StepParametersFactory.Create(TestCaseStepKind.WaitForSignal, p);

        Assert.IsType<WaitForSignalStep>(result);
        var step = (WaitForSignalStep)result;
        Assert.Equal("BMS_Status.EngineRPM", step.SignalName);
        Assert.Equal("3000.0", step.Expected);
        Assert.Equal("50.0", step.Tolerance);
        Assert.Equal("5000", step.TimeoutMs);
    }

    [Theory]
    [InlineData("0x7DF", true, 0x7DFu)]
    [InlineData("0X7DF", true, 0x7DFu)]
    [InlineData("7DF", true, 0x7DFu)]
    [InlineData("1FFFFFFF", true, 0x1FFFFFFFu)]  // max extended, Extended=true
    [InlineData("7FF", false, 0x7FFu)]            // max standard, Extended=false
    public void Create_SendFrame_CanIdParsing(string idStr, bool extended, uint expectedRaw)
    {
        var p = new Dictionary<string, object>
        {
            ["Id"] = idStr,
            ["Data"] = "0210030000000000",
            ["Fd"] = false,
            ["Extended"] = extended,
        };

        var result = StepParametersFactory.Create(TestCaseStepKind.SendFrame, p);
        var step = (SendFrameStep)result;

        Assert.Equal(expectedRaw, step.Id.Raw);
        Assert.Equal(extended, step.Id.IsExtended);
    }

    [Fact]
    public void Create_SendFrame_HexData_ParsesCorrectly()
    {
        var p = new Dictionary<string, object>
        {
            ["Id"] = "7DF",
            ["Data"] = "0210030000000000",
            ["Fd"] = false,
            ["Extended"] = false,
        };

        var result = StepParametersFactory.Create(TestCaseStepKind.SendFrame, p);
        var step = (SendFrameStep)result;

        Assert.Equal(new byte[] { 0x02, 0x10, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 }, step.Data);
    }

    [Fact]
    public void Create_Delay_CorrectMilliseconds()
    {
        var p = new Dictionary<string, object> { ["Milliseconds"] = 1500 };

        var result = StepParametersFactory.Create(TestCaseStepKind.Delay, p);

        Assert.IsType<DelayStep>(result);
        Assert.Equal("1500", ((DelayStep)result).Milliseconds);
    }

    [Fact]
    public void Create_Comment_CorrectText()
    {
        var p = new Dictionary<string, object> { ["Text"] = "Initialize bus" };

        var result = StepParametersFactory.Create(TestCaseStepKind.Comment, p);

        Assert.IsType<CommentStep>(result);
        Assert.Equal("Initialize bus", ((CommentStep)result).Text);
    }

    [Fact]
    public void Create_UnknownKind_ThrowsArgumentException()
    {
        var p = new Dictionary<string, object>();
        Assert.Throws<ArgumentException>(() => StepParametersFactory.Create((TestCaseStepKind)999, p));
    }

    [Fact]
    public void Create_MissingKey_Throws()
    {
        var p = new Dictionary<string, object> { ["SignalName"] = "Test" };
        Assert.Throws<KeyNotFoundException>(() => StepParametersFactory.Create(TestCaseStepKind.WaitForSignal, p));
    }

    [Fact]
    public void Create_InvariantCulture_ParsesOnCommaCulture()
    {
        var originalCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var p = new Dictionary<string, object>
            {
                ["SignalName"] = "Test",
                ["Expected"] = 3.14,
                ["Tolerance"] = 0.5,
                ["TimeoutMs"] = 1000,
            };

            var result = StepParametersFactory.Create(TestCaseStepKind.WaitForSignal, p);
            var step = (WaitForSignalStep)result;

            Assert.Equal("3.14", step.Expected);
            Assert.Equal("0.5", step.Tolerance);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Create_SendFrame_InvalidHex_ThrowsFormatException()
    {
        var p = new Dictionary<string, object>
        {
            ["Id"] = "ZZZZ",
            ["Data"] = "00",
            ["Fd"] = false,
            ["Extended"] = false,
        };

        Assert.Throws<FormatException>(() => StepParametersFactory.Create(TestCaseStepKind.SendFrame, p));
    }
}
