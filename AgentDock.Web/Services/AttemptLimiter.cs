using System.Collections.Concurrent;

namespace AgentDock.Web.Services;

/// <summary>Single-instance sliding-window limiter for dev sign-in attempts.</summary>
public class AttemptLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();
    public int Max { get; init; } = 10;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(15);

    public bool TryAcquire(string key)
    {
        var now = time.GetUtcNow();
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > Window) q.Dequeue();
            if (q.Count >= Max) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
