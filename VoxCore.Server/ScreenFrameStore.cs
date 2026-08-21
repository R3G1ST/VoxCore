using System.Collections.Concurrent;

namespace VoxCore.Server;

public class ScreenFrameStore
{
    private static readonly ConcurrentDictionary<int, string> _frames = new();
    private static readonly ConcurrentDictionary<int, DateTime> _lastUpdate = new();

    public static void StoreFrame(int userId, string frameB64)
    {
        _frames[userId] = frameB64;
        _lastUpdate[userId] = DateTime.UtcNow;
    }

    public static string? GetFrameB64(int userId)
    {
        if (_frames.TryGetValue(userId, out var frame))
        {
            if (_lastUpdate.TryGetValue(userId, out var ts) && (DateTime.UtcNow - ts).TotalSeconds < 5)
                return frame;
            _frames.TryRemove(userId, out _);
            _lastUpdate.TryRemove(userId, out _);
        }
        return null;
    }
}
