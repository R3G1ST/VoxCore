using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VoxCore.Client;

public sealed class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

public sealed class ApiResult
{
    public bool Ok { get; set; }
    public string? Err { get; set; }
    public JsonElement Data { get; set; }
}

public sealed class ChannelInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int OwnerId { get; set; }
    public bool HasPassword { get; set; }
    public int Users { get; set; }
}

public sealed class UserInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#5865f2";
    public bool Online { get; set; }
}

public sealed class ApiClient
{
    private readonly string _host;
    private readonly int _port;

    public string? Token { get; private set; }
    public UserInfo? User { get; private set; }

    public void RestoreToken(string token)
    {
        Token = token;
    }

    public ApiClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task<(string token, UserInfo user)> RegisterAsync(string name, string pass)
    {
        var r = await CallAsync(new { op = "register", name, pass });
        if (!r.Ok) throw new ApiException(r.Err ?? "регистрация не удалась");
        Token = r.Data.GetProperty("token").GetString();
        User = ParseUser(r.Data.GetProperty("user"));
        return (Token!, User);
    }

    public async Task<(string token, UserInfo user)> LoginAsync(string name, string pass)
    {
        var r = await CallAsync(new { op = "login", name, pass });
        if (!r.Ok) throw new ApiException(r.Err ?? "вход не удался");
        Token = r.Data.GetProperty("token").GetString();
        User = ParseUser(r.Data.GetProperty("user"));
        return (Token!, User);
    }

    public async Task<List<ChannelInfo>> GetChannelsAsync()
    {
        var r = await CallAsync(new { op = "channels", token = Token });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось получить каналы");
        return r.Data.GetProperty("channels").EnumerateArray()
            .Select(el => new ChannelInfo
            {
                Id = el.GetProperty("Id").GetInt32(),
                Name = el.GetProperty("Name").GetString() ?? "",
                OwnerId = el.GetProperty("OwnerId").GetInt32(),
                HasPassword = el.GetProperty("HasPassword").GetBoolean(),
                Users = el.GetProperty("Users").GetInt32()
            }).ToList();
    }

    public async Task<ChannelInfo> CreateChannelAsync(string name, string password)
    {
        var r = await CallAsync(new { op = "create_channel", token = Token, name, password });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось создать канал");
        var ch = r.Data.GetProperty("channel");
        return new ChannelInfo
        {
            Id = ch.GetProperty("Id").GetInt32(),
            Name = ch.GetProperty("Name").GetString() ?? "",
            OwnerId = ch.GetProperty("OwnerId").GetInt32(),
            HasPassword = ch.GetProperty("HasPassword").GetBoolean()
        };
    }

    public async Task DeleteChannelAsync(int id)
    {
        var r = await CallAsync(new { op = "delete_channel", token = Token, id });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось удалить канал");
    }

    public async Task VerifyChannelPasswordAsync(int id, string password)
    {
        var r = await CallAsync(new { op = "join_channel", token = Token, id, password });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось войти в канал");
    }

    public async Task<List<UserInfo>> GetFriendsAsync()
    {
        var r = await CallAsync(new { op = "friends", token = Token });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось получить друзей");
        return ParseUsers(r.Data.GetProperty("friends"));
    }

    public async Task<List<UserInfo>> SearchUsersAsync(string query)
    {
        var r = await CallAsync(new { op = "search_users", token = Token, query });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось выполнить поиск");
        return ParseUsers(r.Data.GetProperty("users"));
    }

    public async Task<UserInfo> AddFriendAsync(string name)
    {
        var r = await CallAsync(new { op = "add_friend", token = Token, name });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось добавить друга");
        return ParseUser(r.Data.GetProperty("friend"));
    }

    public async Task RemoveFriendAsync(int id)
    {
        var r = await CallAsync(new { op = "remove_friend", token = Token, id });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось удалить друга");
    }

    private async Task<ApiResult> CallAsync(object payload)
    {
        using var client = new TcpClient();
        client.ReceiveTimeout = 10000;
        client.SendTimeout = 10000;
        await client.ConnectAsync(_host, _port);
        await using var stream = client.GetStream();
        var json = JsonSerializer.Serialize(payload) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync();
        if (line is null) throw new ApiException("нет ответа от сервера");
        var doc = JsonDocument.Parse(line).RootElement;
        return new ApiResult
        {
            Ok = doc.GetProperty("ok").GetBoolean(),
            Err = doc.TryGetProperty("err", out var err) ? err.GetString() : null,
            Data = doc.TryGetProperty("data", out var data) ? data.Clone() : default
        };
    }

    private static UserInfo ParseUser(JsonElement el) => new()
    {
        Id = el.GetProperty("Id").GetInt32(),
        Name = el.GetProperty("Name").GetString() ?? "",
        Color = el.GetProperty("Color").GetString() ?? "#5865f2",
        Online = el.TryGetProperty("Online", out var on) && on.GetBoolean()
    };

    private static List<UserInfo> ParseUsers(JsonElement arr) =>
        arr.EnumerateArray().Select(ParseUser).ToList();
}