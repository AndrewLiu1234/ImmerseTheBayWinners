using System;

namespace UntitledGuitarApp.Core.Tests.Audio
{
    /// <summary>
    /// Zero-allocation check that is robust to a parallel test run. <c>GC.GetAllocatedBytesForCurrentThread</c> can report a
    /// few KB (one allocation quantum, ≈ 8 KB) on a thread that allocated nothing when another thread's allocations trigger a
    /// GC mid-measurement (observed: 8,160 / 1,128 bytes while the synthetic strum grid ran beside these tests). Code that
    /// really allocates per call shows it in every attempt, so the minimum over a few attempts is the honest number.
    /// </summary>
    public static class AllocProbe
    {
        public static long MinAllocatedBytes(Action body, int attempts = 5)
        {
            long min = long.MaxValue;
            for (int a = 0; a < attempts && min > 0; a++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return min;
        }
    }
}
