using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.Tests.HIL.Contracts;

public class EcuContextStoreTests
{
    [Fact]
    public void Get_ReturnsDefault_WhenKeyNotExists()
    {
        var store = new EcuContextStore();

        var value = store.Get<int>("missing");

        Assert.Equal(0, value);
    }

    [Fact]
    public void SetAndGet_RoundTrips_Value()
    {
        var store = new EcuContextStore();

        store.Set("bytes", new byte[] { 0xAA, 0xBB });
        store.Set("number", 42);

        var bytes = store.Get<byte[]>("bytes");
        var number = store.Get<int>("number");

        Assert.Equal(new byte[] { 0xAA, 0xBB }, bytes);
        Assert.Equal(42, number);
    }

    [Fact]
    public void Clear_RemovesAll_Keys()
    {
        var store = new EcuContextStore();
        store.Set("key1", 100);
        store.Set("key2", "hello");

        store.Clear();

        Assert.False(store.HasKey("key1"));
        Assert.False(store.HasKey("key2"));
    }
}
