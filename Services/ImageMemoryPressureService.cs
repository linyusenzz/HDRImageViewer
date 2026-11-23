using System.Runtime;

namespace HdrImageViewer.Services;

internal readonly record struct ImageMemoryPressureSnapshot(
    long MemoryLoadBytes,
    long HighMemoryLoadThresholdBytes,
    long HeapSizeBytes,
    long FragmentedBytes);

internal static class ImageMemoryPressurePolicy
{
    public const long MinimumEvictedBytes = 128L * 1024L * 1024L;
    private const long MinimumFragmentedBytes = 64L * 1024L * 1024L;
    private const double HighMemoryLoadRatio = 0.85;
    private const double FragmentationRatio = 0.20;

    public static bool IsHighMemoryLoad(ImageMemoryPressureSnapshot snapshot)
    {
        return snapshot.HighMemoryLoadThresholdBytes > 0
            && snapshot.MemoryLoadBytes >= snapshot.HighMemoryLoadThresholdBytes * HighMemoryLoadRatio;
    }

    public static bool ShouldCollect(long evictedBytes, ImageMemoryPressureSnapshot snapshot)
    {
        return evictedBytes >= MinimumEvictedBytes
            && IsHighMemoryLoad(snapshot);
    }

    public static long GetRetainedEvictedBytesAfterCheck(
        long evictedBytes,
        ImageMemoryPressureSnapshot snapshot)
    {
        if (evictedBytes < MinimumEvictedBytes)
        {
            return Math.Max(0L, evictedBytes);
        }

        // Reset whether collection runs or is skipped due to low memory load (< 85%),
        // preventing s_evictedBytes from staying latched >= 128 MB indefinitely.
        return 0L;
    }

    public static bool ShouldCompactLargeObjectHeap(ImageMemoryPressureSnapshot snapshot)
    {
        return snapshot.FragmentedBytes >= MinimumFragmentedBytes
            && snapshot.HeapSizeBytes > 0
            && snapshot.FragmentedBytes >= snapshot.HeapSizeBytes * FragmentationRatio;
    }
}

internal static class ImageMemoryPressureService
{
    private const long CheckCooldownMilliseconds = 15_000;
    private static long s_evictedBytes;
    private static long s_lastCheckTicks = -CheckCooldownMilliseconds;
    private static int s_checkScheduled;

    public static void ReportEvictedBytes(long byteCount)
    {
        if (byteCount <= 0)
        {
            return;
        }

        var totalBytes = Interlocked.Add(ref s_evictedBytes, byteCount);
        if (totalBytes < ImageMemoryPressurePolicy.MinimumEvictedBytes)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref s_lastCheckTicks) < CheckCooldownMilliseconds
            || Interlocked.CompareExchange(ref s_checkScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(EvaluateMemoryPressure);
    }

    internal static bool EvaluateSnapshotCore(
        ref long evictedBytesState,
        ImageMemoryPressureSnapshot snapshot)
    {
        var evictedBytes = Interlocked.Read(ref evictedBytesState);
        if (!ImageMemoryPressurePolicy.ShouldCollect(evictedBytes, snapshot))
        {
            if (ImageMemoryPressurePolicy.GetRetainedEvictedBytesAfterCheck(evictedBytes, snapshot) == 0L)
            {
                Interlocked.Exchange(ref evictedBytesState, 0);
            }

            return false;
        }

        Interlocked.Exchange(ref evictedBytesState, 0);
        return true;
    }

    private static void EvaluateMemoryPressure()
    {
        try
        {
            Interlocked.Exchange(ref s_lastCheckTicks, Environment.TickCount64);
            var memoryInfo = GC.GetGCMemoryInfo();
            var snapshot = new ImageMemoryPressureSnapshot(
                memoryInfo.MemoryLoadBytes,
                memoryInfo.HighMemoryLoadThresholdBytes,
                memoryInfo.HeapSizeBytes,
                memoryInfo.FragmentedBytes);
            if (!EvaluateSnapshotCore(ref s_evictedBytes, snapshot))
            {
                return;
            }

            if (ImageMemoryPressurePolicy.ShouldCompactLargeObjectHeap(snapshot))
            {
                GCSettings.LargeObjectHeapCompactionMode =
                    GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(
                    2,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: true);
            }
            else
            {
                GC.Collect(
                    2,
                    GCCollectionMode.Forced,
                    blocking: false,
                    compacting: false);
            }
        }
        finally
        {
            Volatile.Write(ref s_checkScheduled, 0);
        }
    }
}
