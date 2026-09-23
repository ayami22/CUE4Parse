using CUE4Parse_Conversion;

namespace CUE4Parse.Tests;

public class ExportMemoryPressureMonitorTests
{
    [Theory]
    [InlineData(89.99, 0)]
    [InlineData(90, 1)]
    [InlineData(100, 1)]
    [InlineData(-1, 0)]
    [InlineData(101, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void UsesPhysicalMemoryThreshold(double percent, int expected)
    {
        var collections = 0;
        var monitor = new ExportMemoryPressureMonitor(() => percent, () => collections++, () => 0, false);
        using var lease = monitor.Acquire();
        monitor.Check();
        Assert.Equal(expected, collections);
    }

    [Fact]
    public void MissingSampleDoesNotTriggerCollection()
    {
        var monitor = new ExportMemoryPressureMonitor(() => null, () => Assert.Fail("Unexpected collection"), () => 0, false);
        using var lease = monitor.Acquire();
        monitor.Check();
    }

    [Fact]
    public void SustainedPressureIsThrottledAcrossSessions()
    {
        long time = 0;
        var collections = 0;
        var monitor = new ExportMemoryPressureMonitor(() => 95, () => collections++, () => time, false);
        using (monitor.Acquire())
        {
            monitor.Check();
            time = 29_999;
            monitor.Check();
            Assert.Equal(1, collections);
        }
        using (monitor.Acquire())
        {
            monitor.Check();
            Assert.Equal(1, collections);
            time = 30_000;
            monitor.Check();
            Assert.Equal(2, collections);
        }
    }

    [Fact]
    public void StopsWhenLastLeaseEndsAndDisposeIsIdempotent()
    {
        var collections = 0;
        long time = 0;
        var monitor = new ExportMemoryPressureMonitor(() => 95, () => collections++, () => time, false);
        var first = monitor.Acquire();
        var second = monitor.Acquire();
        first.Dispose();
        first.Dispose();
        monitor.Check();
        Assert.Equal(1, collections);
        second.Dispose();
        time = 30_000;
        monitor.Check();
        Assert.Equal(1, collections);
    }

    [Fact]
    public void RecoveryBelowThresholdDoesNotCollectAgain()
    {
        var collections = 0;
        double load = 95;
        long time = 0;
        var monitor = new ExportMemoryPressureMonitor(() => load, () => collections++, () => time, false);
        using var lease = monitor.Acquire();
        monitor.Check();
        load = 50;
        time = 60_000;
        monitor.Check();
        Assert.Equal(1, collections);
        load = 90;
        monitor.Check();
        Assert.Equal(2, collections);
    }

    [Fact]
    public void OverlappingChecksCannotCollectConcurrently()
    {
        var collections = 0;
        ExportMemoryPressureMonitor? monitor = null;
        monitor = new ExportMemoryPressureMonitor(() => 95, () => { collections++; monitor!.Check(); }, () => 0, false);
        using var lease = monitor.Acquire();
        monitor.Check();
        Assert.Equal(1, collections);
    }

    [Fact]
    public void SamplerFailureDoesNotStopLaterChecks()
    {
        var fail = true;
        var collections = 0;
        var monitor = new ExportMemoryPressureMonitor(() => fail ? throw new InvalidOperationException() : 95,
            () => collections++, () => 0, false);
        using var lease = monitor.Acquire();
        monitor.Check();
        fail = false;
        monitor.Check();
        Assert.Equal(1, collections);
    }

    [Fact]
    public void NativeWindowsSampleIsInRange()
    {
        var percent = ExportMemoryPressureMonitor.ReadSystemMemoryPercent();
        if (!OperatingSystem.IsWindows()) { Assert.Null(percent); return; }
        Assert.NotNull(percent);
        Assert.InRange(percent.Value, 0, 100);
    }
}
