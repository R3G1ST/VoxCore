using System.Collections.Concurrent;

namespace VoxCore.Client;

public static class ScreenShareReceiver
{
    private static readonly ConcurrentDictionary<string, byte[]> _frames = new();
    private static readonly ConcurrentDictionary<string, DateTime> _lastUpdate = new();

    public static void UpdateFrame(string from, byte[] jpeg)
    {
        _frames[from] = jpeg;
        _lastUpdate[from] = DateTime.UtcNow;
    }

    public static byte[]? GetLastFrame(string from)
    {
        if (_frames.TryGetValue(from, out var frame))
        {
            if (_lastUpdate.TryGetValue(from, out var ts) && (DateTime.UtcNow - ts).TotalSeconds < 5)
                return frame;
            _frames.TryRemove(from, out _);
            _lastUpdate.TryRemove(from, out _);
        }
        return null;
    }

    public static List<string> GetActiveSharers()
    {
        var now = DateTime.UtcNow;
        return _lastUpdate.Where(kv => (now - kv.Value).TotalSeconds < 5)
            .Select(kv => kv.Key).ToList();
    }

    public static void Remove(string from)
    {
        _frames.TryRemove(from, out _);
        _lastUpdate.TryRemove(from, out _);
    }
}
