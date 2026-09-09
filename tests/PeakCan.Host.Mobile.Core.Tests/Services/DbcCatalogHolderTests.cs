using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class DbcCatalogHolderTests
{
    private static DbcCatalog CreateCatalog() => DbcCatalog.Parse("""
        VERSION ""

        NS_ :

        BS_:

        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
        """, "engine.dbc").Catalog!;

    [Fact]
    public void Current_Is_Null_Before_Set()
    {
        var holder = new DbcCatalogHolder();

        holder.Current.Should().BeNull();
    }

    [Fact]
    public void Set_Stores_Catalog_And_Can_Clear_It()
    {
        var holder = new DbcCatalogHolder();
        var catalog = CreateCatalog();

        holder.Set(catalog);
        holder.Current.Should().BeSameAs(catalog);

        holder.Set(null);
        holder.Current.Should().BeNull();
    }

    [Fact]
    public void Set_Raises_Changed_Every_Time()
    {
        var holder = new DbcCatalogHolder();
        var catalog = CreateCatalog();
        var changes = 0;
        holder.Changed += () => changes++;

        holder.Set(catalog);
        holder.Set(catalog);

        changes.Should().Be(2);
    }
}
