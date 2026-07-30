using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

public class CircularBufferTests
{
    private static readonly int[] _expectedOrder = { 1, 2, 3 };
    private static readonly int[] _expectedOverflow = { 2, 3, 4 };

    [Fact]
    public void Add_BelowCapacity_ReturnsAllInOrder()
    {
        // Arrange
        var buffer = new CircularBuffer<int>(3);

        // Act
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        // Assert
        Assert.Equal(_expectedOrder, buffer.Snapshot());
    }

    [Fact]
    public void Add_Overflow_DropsOldest()
    {
        // Arrange
        var buffer = new CircularBuffer<int>(3);

        // Act
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);

        // Assert
        Assert.Equal(_expectedOverflow, buffer.Snapshot());
    }

    [Fact]
    public void Snapshot_Empty_ReturnsEmptyList()
    {
        // Arrange
        var buffer = new CircularBuffer<int>(3);

        // Act & Assert
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public async Task Add_Concurrent_ThreadSafe()
    {
        // Arrange
        var buffer = new CircularBuffer<int>(1000);
        var tasks = new List<Task>();

        // Act — 2 threads x 500 adds each
        for (int t = 0; t < 2; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 500; i++)
                    buffer.Add(i);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1000, buffer.Snapshot().Count);
    }
}
