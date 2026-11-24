using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds.Rows;

public sealed class RoutineRowTests
{
    [Fact]
    public void Status_Defaults_To_Idle()
    {
        new RoutineRow().Status.Should().Be("Idle");
    }

    [Fact]
    public void Status_PropertyChanged_Fires_When_Set()
    {
        var row = new RoutineRow();
        var fired = false;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RoutineRow.Status)) fired = true;
        };
        row.Status = "Running";
        fired.Should().BeTrue();
        row.Status.Should().Be("Running");
    }

    /// <summary>
    /// 内部状态串保持英文（CanRun 判据 + 既有测试依赖），
    /// 表格显示的 StatusDisplay 负责中文化。
    /// </summary>
    [Theory]
    [InlineData("Idle", "空闲")]
    [InlineData("Running", "执行中")]
    [InlineData("Completed", "已完成")]
    [InlineData("Failed", "失败")]
    [InlineData("Stopped", "已停止")]
    public void StatusDisplay_Maps_Internal_State_To_Chinese(string status, string display)
    {
        var row = new RoutineRow { Status = status };
        row.StatusDisplay.Should().Be(display);
    }

    [Fact]
    public void StatusDisplay_PropertyChanged_Fires_When_Status_Changes()
    {
        var row = new RoutineRow();
        var fired = false;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RoutineRow.StatusDisplay)) fired = true;
        };
        row.Status = "Completed";
        fired.Should().BeTrue("表格绑定 StatusDisplay，不通知会停在旧状态");
    }
}
