using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoxCore.Server;

public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string PassHash { get; set; } = "";
    public string Color { get; set; } = "#5865f2";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Channel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int OwnerId { get; set; }
    public string? PassHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Store
{
    private readonly string _dir;
    private readonly object _lock = new();

    public List<User> Users { get; private set; } = [];
    public List<Channel> Channels { get; private set; } = [];
    public Dictionary<string, int> Tokens { get; } = new(); // token -> userId

    public Store(string dataDir)
    {
        _dir = dataDir;
        Directory.CreateDirectory(_dir);
        Load();
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string UsersPath => Path.Combine(_dir, "users.json");
    private string ChannelsPath => Path.Combine(_dir, "channels.json");

    private void Load()
    {
        lock (_lock)
        {
            if (File.Exists(UsersPath))
                Users = JsonSerializer.Deserialize<List<User>>(File.ReadAllText(UsersPath)) ?? [];
            if (File.Exists(ChannelsPath))
                Channels = JsonSerializer.Deserialize<List<Channel>>(File.ReadAllText(ChannelsPath)) ?? [];
        }
    }

    public void SaveUsers() => Save(UsersPath, Users);
    public void SaveChannels() => Save(ChannelsPath, Channels);

    private void Save(string path, object data)
    {
        lock (_lock)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public User? FindUserByName(string name) => Users.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public User? FindUserById(int id) => Users.FirstOrDefault(u => u.Id == id);

    public int NextUserId => Users.Count > 0 ? Users.Max(u => u.Id) + 1 : 1;
    public int NextChannelId => Channels.Count > 0 ? Channels.Max(c => c.Id) + 1 : 1;

    public User? CreateUser(string name, string passHash, string color)
    {
        lock (_lock)
        {
            if (FindUserByName(name) is not null) return null;
            var user = new User { Id = NextUserId, Name = name, PassHash = passHash, Color = color };
            Users.Add(user);
            SaveUsers();
            return user;
        }
    }

    public Channel? CreateChannel(string name, string? passHash, int ownerId)
    {
        lock (_lock)
        {
            var channel = new Channel { Id = NextChannelId, Name = name, PassHash = passHash, OwnerId = ownerId };
            Channels.Add(channel);
            SaveChannels();
            return channel;
        }
    }

    public bool DeleteChannel(int id, int userId)
    {
        lock (_lock)
        {
            var ch = Channels.FirstOrDefault(c => c.Id == id);
            if (ch is null || ch.OwnerId != userId) return false;
            Channels.Remove(ch);
            SaveChannels();
            return true;
        }
    }

    public string IssueToken(User user)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        lock (_lock) Tokens[token] = user.Id;
        return token;
    }

    public User? AuthByToken(string token)
    {
        lock (_lock)
            return token is not null && Tokens.TryGetValue(token, out var id) ? FindUserById(id) : null;
    }
}