using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 报文在**用户可见文本**里的标识（spec §10.2 标定面板）。
/// <para>
/// <b>实测发现的问题</b>：标定面板与事件表只显示 CAN ID（<c>0x18FFFA27</c>），
/// 而同面板的时间条行标签已经显示「<c>0x18FFFA27 NM_Message (ECU_A)</c>」——
/// 同一个面板里两套口径。排查时人想的是报文名，不是十六进制。
/// </para>
/// <para>
/// <b>为什么不能只显示名字</b>：<see cref="NmMessageDef.Name"/> 可为 null
/// （DBC 没写名字），不同网段也可能重名，而 CAN ID 是唯一且稳定的。
/// </para>
/// </summary>
public class NmMessageDisplayNameTests
{
    private static NmMessageDef Def(string? name, string canId = "0x18FFFA27")
        => new(canId, true, name, "ECU_A", 8, true, Array.Empty<string>());

    [Fact]
    public void WithName_ShowsNameThenCanId()
    {
        Assert.Equal("NM_Message (0x18FFFA27)", Def("NM_Message").DisplayName);
    }

    /// <summary>DBC 没给名字时退化成纯 CAN ID，不能渲染出空括号。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutName_FallsBackToCanId(string? name)
    {
        Assert.Equal("0x18FFFA27", Def(name).DisplayName);
    }

    /// <summary>
    /// 两个不同网段的同名报文必须仍可区分——这正是不能只显示名字的原因。
    /// </summary>
    [Fact]
    public void SameNameDifferentId_RemainsDistinguishable()
    {
        var a = Def("NM_Message", "0x18FFFA27").DisplayName;
        var b = Def("NM_Message", "0x18FFFB27").DisplayName;

        Assert.NotEqual(a, b);
    }
}
