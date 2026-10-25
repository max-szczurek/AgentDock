using System.Collections.Concurrent;

namespace AgentDock.Core.Runtime;

/// <summary>Single-instance sliding-window limiter (the app runs on one instance; see plan 7.6).</summary>
public sealed class SlidingWindowLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    public bool TryAcquire(string key, int max, TimeSpan window)
    {
        var now = time.GetUtcNow();
        Sweep(now);
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            if (q.Count >= max) return false;
            q.Enqueue(now);
            return true;
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        if (now - _lastSweep < TimeSpan.FromMinutes(10)) return;
        _lastSweep = now;
        foreach (var (k, q) in _hits)
            lock (q)
                if (q.Count == 0 || now - q.Peek() > TimeSpan.FromHours(2)) _hits.TryRemove(k, out _);
    }
}
