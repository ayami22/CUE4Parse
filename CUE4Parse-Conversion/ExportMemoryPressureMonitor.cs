using System.Runtime;
using System.Runtime.InteropServices;

namespace CUE4Parse_Conversion;

/// <summary>One process-wide monitor, leased only while an export/scan is running.</summary>
internal sealed class ExportMemoryPressureMonitor(
    Func<double?> readMemoryPercent, Action collect, Func<long> clockMilliseconds,
    bool automaticPolling = true)
{
    internal const double ThresholdPercent = 90;
    internal const long CooldownMilliseconds = 30_000;
    private static readonly ExportMemoryPressureMonitor Shared = new(
        ReadSystemMemoryPercent, CollectUnusedMemory, () => Environment.TickCount64);

    private readonly object _gate = new();
    private Timer? _timer;
    private int _leases;
    private int _checking;
    private long? _lastCollection;

    public static IDisposable Start() => Shared.Acquire();

    internal IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_leases++ == 0 && automaticPolling)
                _timer = new Timer(_ => Check(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        }
        return new Lease(this);
    }

    internal void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            lock (_gate)
                if (_leases == 0) return;

            var percent = readMemoryPercent();
            if (percent is not { } load || !double.IsFinite(load) || load < ThresholdPercent || load > 100) return;

            lock (_gate)
            {
                if (_leases == 0) return;
                var now = clockMilliseconds();
                if (_lastCollection is { } last && now - last < CooldownMilliseconds) return;
                _lastCollection = now;
            }

            Serilog.Log.Warning("System physical memory usage is {MemoryPercent:F1}%; reclaiming unused export memory", load);
            collect();
        }
        catch (Exception ex)
        {
            // A diagnostic timer must never crash an otherwise valid export.
            Serilog.Log.Warning(ex, "Export memory pressure check failed");
        }
        finally { Volatile.Write(ref _checking, 0); }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_leases != 0) return;
            _timer?.Dispose();
            _timer = null;
            // Preserve the cooldown across consecutive sessions and concurrent export windows.
        }
    }

    private sealed class Lease(ExportMemoryPressureMonitor owner) : IDisposable
    {
        private ExportMemoryPressureMonitor? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    internal static double? ReadSystemMemoryPercent()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0 || status.AvailablePhysical > status.TotalPhysical)
            return null;
        return 100.0 * (status.TotalPhysical - status.AvailablePhysical) / status.TotalPhysical;
    }

    private static void CollectUnusedMemory()
    {
        var before = GC.GetTotalMemory(false);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var after = GC.GetTotalMemory(false);
        Serilog.Log.Information("Memory pressure GC finished: managed heap {BeforeMiB:F1} -> {AfterMiB:F1} MiB; live assets are retained",
            before / 1048576.0, after / 1048576.0);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
