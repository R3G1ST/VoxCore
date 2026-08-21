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
    public string? Token { get; set; }
    public List<int> Friends { get; set; } = [];
    public List<int> PendingRequests { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum FriendState
{
    None,
    Friend,
    Requested,  // запрос отправил я, ждёт принятия
    Incoming    // запрос пришёл мне
}

public sealed class Message
{
    public int Id { get; set; }
    public int FromUserId { get; set; }
    public int ToUserId { get; set; }
    public int ChannelId { get; set; }
    public string Text { get; set; } = "";
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public bool Read { get; set; }
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
    public List<Message> Messages { get; private set; } = [];
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
    private string MessagesPath => Path.Combine(_dir, "messages.json");

    private void Load()
    {
        lock (_lock)
        {
            if (File.Exists(UsersPath))
                Users = JsonSerializer.Deserialize<List<User>>(File.ReadAllText(UsersPath)) ?? [];
            if (File.Exists(ChannelsPath))
                Channels = JsonSerializer.Deserialize<List<Channel>>(File.ReadAllText(ChannelsPath)) ?? [];
            if (File.Exists(MessagesPath))
                Messages = JsonSerializer.Deserialize<List<Message>>(File.ReadAllText(MessagesPath)) ?? [];
        }
    }

    public void SaveUsers() => Save(UsersPath, Users);
    public void SaveChannels() => Save(ChannelsPath, Channels);
    public void SaveMessages() => Save(MessagesPath, Messages);

    public Message SendMessage(int fromUserId, int toUserId, string text)
    {
        lock (_lock)
        {
            var msg = new Message
            {
                Id = Messages.Count > 0 ? Messages.Max(m => m.Id) + 1 : 1,
                FromUserId = fromUserId,
                ToUserId = toUserId,
                Text = text,
                SentAt = DateTime.UtcNow
            };
            Messages.Add(msg);
            SaveMessages();
            return msg;
        }
    }

    public List<Message> GetMessages(int userId, int otherUserId, int limit = 50)
    {
        lock (_lock)
        {
            return Messages
                .Where(m => (m.FromUserId == userId && m.ToUserId == otherUserId) ||
                           (m.FromUserId == otherUserId && m.ToUserId == userId))
                .OrderByDescending(m => m.SentAt)
                .Take(limit)
                .OrderBy(m => m.SentAt)
                .ToList();
        }
    }

    public void MarkAsRead(int userId, int otherUserId)
    {
        lock (_lock)
        {
            foreach (var m in Messages.Where(m => m.FromUserId == otherUserId && m.ToUserId == userId && !m.Read))
                m.Read = true;
            SaveMessages();
        }
    }

    public int GetUnreadCount(int userId)
    {
        lock (_lock)
        {
            return Messages.Count(m => m.ToUserId == userId && !m.Read);
        }
    }

    public Message SendChannelMessage(int channelId, int fromUserId, string text)
    {
        lock (_lock)
        {
            var msg = new Message
            {
                Id = Messages.Count > 0 ? Messages.Max(m => m.Id) + 1 : 1,
                FromUserId = fromUserId,
                ToUserId = 0,
                ChannelId = channelId,
                Text = text,
                SentAt = DateTime.UtcNow
            };
            Messages.Add(msg);
            SaveMessages();
            return msg;
        }
    }

    public List<Message> GetChannelMessages(int channelId, int limit = 100)
    {
        lock (_lock)
        {
            return Messages
                .Where(m => m.ChannelId == channelId)
                .OrderByDescending(m => m.SentAt)
                .Take(limit)
                .OrderBy(m => m.SentAt)
                .ToList();
        }
    }

    private void Save(string path, object data)
    {
        lock (_lock)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public User? FindUserByName(string name) => Users.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public User? FindUserById(int id) => Users.FirstOrDefault(u => u.Id == id);

    public List<User> SearchUsers(string query, int excludeId, int limit = 20)
    {
        lock (_lock)
        {
            return Users
                .Where(u => u.Id != excludeId && u.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(u => u.Name)
                .Take(limit)
                .ToList();
        }
    }

    public bool AddFriend(int userId, int friendId)
    {
        lock (_lock)
        {
            var user = FindUserById(userId);
            var friend = FindUserById(friendId);
            if (user is null || friend is null || user.Id == friend.Id) return false;
            if (user.Friends.Contains(friendId)) return true;
            user.Friends.Add(friendId);
            SaveUsers();
            return true;
        }
    }

    public bool RemoveFriend(int userId, int friendId)
    {
        lock (_lock)
        {
            var user = FindUserById(userId);
            if (user is null || !user.Friends.Remove(friendId)) return false;
            SaveUsers();
            return true;
        }
    }

    public bool SendFriendRequest(int userId, int targetId)
    {
        lock (_lock)
        {
            var target = FindUserById(targetId);
            var user = FindUserById(userId);
            if (user is null || target is null || user.Id == target.Id) return false;
            if (user.Friends.Contains(targetId) || target.PendingRequests.Contains(userId) || user.PendingRequests.Contains(targetId)) return false;
            if (target.Friends.Contains(userId))
            {
                // target уже считает меня другом (старые данные) — делаем дружбу взаимной
                if (!user.Friends.Contains(targetId)) user.Friends.Add(targetId);
                SaveUsers();
                return true;
            }
            target.PendingRequests.Add(userId);
            SaveUsers();
            return true;
        }
    }

    public FriendState GetFriendState(int userId, int targetId)
    {
        lock (_lock)
        {
            var user = FindUserById(userId);
            var target = FindUserById(targetId);
            if (user is null || target is null) return FriendState.None;
            if (user.Friends.Contains(targetId)) return FriendState.Friend;
            if (user.PendingRequests.Contains(targetId)) return FriendState.Incoming;
            if (target.PendingRequests.Contains(userId)) return FriendState.Requested;
            return FriendState.None;
        }
    }

    public bool AcceptFriendRequest(int userId, int fromId)
    {
        lock (_lock)
        {
            var user = FindUserById(userId);
            var from = FindUserById(fromId);
            if (user is null || from is null) return false;
            if (!user.PendingRequests.Remove(fromId)) return false;
            if (!user.Friends.Contains(fromId)) user.Friends.Add(fromId);
            if (!from.Friends.Contains(userId)) from.Friends.Add(userId);
            SaveUsers();
            return true;
        }
    }

    public bool DeclineFriendRequest(int userId, int fromId)
    {
        lock (_lock)
        {
            var user = FindUserById(userId);
            if (user is null || !user.PendingRequests.Remove(fromId)) return false;
            SaveUsers();
            return true;
        }
    }

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
        lock (_lock)
        {
            user.Token = token;
            Tokens[token] = user.Id;
            SaveUsers();
        }
        return token;
    }

    public User? AuthByToken(string token)
    {
        lock (_lock)
        {
            if (token is null) return null;
            if (Tokens.TryGetValue(token, out var id)) return FindUserById(id);
            var user = Users.FirstOrDefault(u => u.Token == token);
            if (user is not null) Tokens[token] = user.Id; // восстановление сессии после рестарта
            return user;
        }
    }
}