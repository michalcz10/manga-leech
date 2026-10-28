namespace MangaLeech.Core;

internal sealed class MemoryBudgetExceededException(string message) : Exception(message);

// Limits the large native and managed pixel buffers shared by validation and PDF workers.
internal sealed class MemoryBudget
{
    private const long Unit = 16L * 1024 * 1024;
    private readonly object gate = new();
    private readonly int capacity;
    private int available;

    internal MemoryBudget(int mebibytes)
    {
        capacity = checked((int)(mebibytes * 1024L * 1024 / Unit));
        available = capacity;
    }

    internal IDisposable Acquire(long bytes, CancellationToken ct)
    {
        int count = checked((int)Math.Max(1, (bytes + Unit - 1) / Unit));
        if (count > capacity)
            throw new MemoryBudgetExceededException($"An image needs about {(bytes + 1048575) / 1048576} MiB of working memory; increase --memory-limit-mib.");
        using var registration = ct.Register(() => { lock (gate) Monitor.PulseAll(gate); });
        lock (gate)
        {
            while (available < count)
            {
                ct.ThrowIfCancellationRequested();
                Monitor.Wait(gate);
            }
            ct.ThrowIfCancellationRequested();
            available -= count;
        }
        return new Lease(this, count);
    }

    private sealed class Lease(MemoryBudget budget, int count) : IDisposable
    {
        private MemoryBudget? owner = budget;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current == null) return;
            lock (current.gate)
            {
                current.available += count;
                Monitor.PulseAll(current.gate);
            }
        }
    }
}
