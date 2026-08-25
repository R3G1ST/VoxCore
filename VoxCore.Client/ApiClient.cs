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
    public string State { get; set; } = "None";
}

public sealed class MessageInfo
{
    public int Id { get; set; }
    public int SenderId { get; set; }
    public string SenderName { get; set; } = "";
    public string SenderColor { get; set; } = "#5865f2";
    public int FromUserId { get; set; }
    public int ToUserId { get; set; }
    public string Text { get; set; } = "";
    public DateTime SentAt { get; set; }
    public bool Read { get; set; }
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
        var r = await CallAsync(new { op = "friend_request", token = Token, name });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось отправить запрос");
        return new UserInfo { Name = name };
    }

    public async Task<List<UserInfo>> GetFriendRequestsAsync()
    {
        var r = await CallAsync(new { op = "friend_requests", token = Token });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось получить запросы");
        return ParseUsers(r.Data.GetProperty("requests"));
    }

    public async Task AcceptFriendRequestAsync(int id)
    {
        var r = await CallAsync(new { op = "accept_friend", token = Token, id });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось принять запрос");
    }

    public async Task DeclineFriendRequestAsync(int id)
    {
        var r = await CallAsync(new { op = "decline_friend", token = Token, id });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось отклонить запрос");
    }

    public async Task RemoveFriendAsync(int id)
    {
        var r = await CallAsync(new { op = "remove_friend", token = Token, id });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось удалить друга");
    }

    public async Task SendMessageAsync(int toId, string text)
    {
        var r = await CallAsync(new { op = "send_message", token = Token, to = toId, text });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось отправить сообщение");
    }

    public async Task<List<MessageInfo>> GetMessagesAsync(int withId, int limit = 50)
    {
        var r = await CallAsync(new { op = "get_messages", token = Token, with = withId, limit });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось получить сообщения");
        var msgs = new List<MessageInfo>();
        foreach (var m in r.Data.GetProperty("messages").EnumerateArray())
        {
            msgs.Add(new MessageInfo
            {
                Id = m.GetProperty("Id").GetInt32(),
                FromUserId = m.GetProperty("From").GetInt32(),
                ToUserId = m.GetProperty("To").GetInt32(),
                Text = m.GetProperty("Text").GetString() ?? "",
                SentAt = m.GetProperty("Sent").GetDateTime(),
                Read = m.GetProperty("Read").GetBoolean()
            });
        }
        return msgs;
    }

    public async Task MarkAsReadAsync(int withId)
    {
        var r = await CallAsync(new { op = "mark_read", token = Token, with = withId });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось пометить как прочитанное");
    }

    public async Task<int> GetUnreadCountAsync()
    {
        var r = await CallAsync(new { op = "unread_count", token = Token });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось получить количество непрочитанных");
        return r.Data.GetProperty("count").GetInt32();
    }

    public async Task SendChannelMessageAsync(int channelId, string text)
    {
        var r = await CallAsync(new { op = "send_channel_message", token = Token, channel = channelId, text });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось отправить сообщение");
    }

    public async Task<List<MessageInfo>> GetChannelMessagesAsync(int channelId, int limit = 100)
    {
        var r = await CallAsync(new { op = "get_channel_messages", token = Token, channel = channelId, limit });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось получить сообщения");
        var msgs = new List<MessageInfo>();
        foreach (var m in r.Data.GetProperty("messages").EnumerateArray())
        {
            msgs.Add(new MessageInfo
            {
                Id = m.GetProperty("Id").GetInt32(),
                SenderId = m.GetProperty("SenderId").GetInt32(),
                SenderName = m.GetProperty("Sender").GetString() ?? "",
                SenderColor = m.GetProperty("SenderColor").GetString() ?? "#5865f2",
                Text = m.GetProperty("Text").GetString() ?? "",
                SentAt = m.GetProperty("Sent").GetDateTime()
            });
        }
        return msgs;
    }

    public async Task<(List<int> Peers, List<string> Names, string RoomId)> WebRTCJoinAsync(int channelId)
    {
        var r = await CallAsync(new { op = "webrtc_join", token = Token, channel = channelId });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось подключиться к комнате");
        var peers = r.Data.GetProperty("peers").EnumerateArray().Select(x => x.GetInt32()).ToList();
        var roomId = r.Data.GetProperty("roomId").GetString() ?? "";
        List<string> names = [];
        if (r.Data.TryGetProperty("names", out var namesEl))
            names = namesEl.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        return (peers, names, roomId);
    }

    public async Task<(List<int> Peers, List<string> Names)> WebRTCSyncAsync(string roomId)
    {
        var r = await CallAsync(new { op = "webrtc_sync", token = Token, room = roomId });
        if (!r.Ok) throw new ApiException(r.Err ?? "sync failed");
        var peers = r.Data.GetProperty("peers").EnumerateArray().Select(x => x.GetInt32()).ToList();
        var names = r.Data.GetProperty("names").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        return (peers, names);
    }

    public async Task WebRTCLeaveAsync(string roomId)
    {
        var r = await CallAsync(new { op = "webrtc_leave", token = Token, room = roomId });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось покинуть комнату");
    }

    public async Task<(string Sdp, List<int> Peers)> WebRTCOfferAsync(string roomId, string sdp)
    {
        var r = await CallAsync(new { op = "webrtc_offer", token = Token, room = roomId, sdp });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось отправить offer");
        var answerSdp = r.Data.GetProperty("sdp").GetString() ?? "";
        var peers = r.Data.GetProperty("peers").EnumerateArray().Select(x => x.GetInt32()).ToList();
        return (answerSdp, peers);
    }

    public async Task WebRTCAnswerAsync(string roomId, string sdp)
    {
        var r = await CallAsync(new { op = "webrtc_answer", token = Token, room = roomId, sdp });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось отправить answer");
    }

    public async Task WebRTCIceAsync(string roomId, string candidate)
    {
        var r = await CallAsync(new { op = "webrtc_ice", token = Token, room = roomId, candidate });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось отправить ICE candidate");
    }

    public async Task WebRTCNackAsync(string roomId, int seq)
    {
        try { await CallAsync(new { op = "webrtc_nack", token = Token, room = roomId, seq }); } catch { }
    }

    public async Task<int> PingAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await CallAsync(new { op = "ping" });
        sw.Stop();
        return (int)sw.ElapsedMilliseconds;
    }

    public async Task SendScreenFrameAsync(string roomId, byte[] jpegData)
    {
        var b64 = Convert.ToBase64String(jpegData);
        var r = await CallAsync(new { op = "screen_frame", token = Token, room = roomId, frame = b64 });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось отправить кадр");
    }

    public async Task ScreenShareStartAsync(int channelId)
    {
        var r = await CallAsync(new { op = "screen_start", token = Token, channel = channelId });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось начать демонстрацию");
    }

    public async Task ScreenShareStopAsync()
    {
        var r = await CallAsync(new { op = "screen_stop", token = Token });
        if (!r.Ok) throw new ApiException(r.Err ?? "не удалось остановить демонстрацию");
    }

    public async Task<List<string>> ScreenListAsync()
    {
        var r = await CallAsync(new { op = "screen_list", token = Token });
        if (!r.Ok) return [];
        if (r.Data.ValueKind != JsonValueKind.Object) return [];
        var sharers = r.Data.TryGetProperty("sharers", out var s) ? s : default;
        var result = new List<string>();
        if (sharers.ValueKind == JsonValueKind.Array)
            foreach (var item in sharers.EnumerateArray())
                result.Add(item.GetString() ?? "");
        return result;
    }

    public async Task<byte[]?> ScreenGetFrameAsync(string name)
    {
        var r = await CallAsync(new { op = "screen_get", token = Token, name });
        if (!r.Ok) return null;
        if (r.Data.ValueKind != JsonValueKind.Object) return null;
        var frame = r.Data.TryGetProperty("frame", out var f) ? f.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(frame)) return null;
        return Convert.FromBase64String(frame);
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
        Online = el.TryGetProperty("Online", out var on) && on.GetBoolean(),
        State = el.TryGetProperty("State", out var st) ? st.GetString() : "None"
    };

    private static List<UserInfo> ParseUsers(JsonElement arr) =>
        arr.EnumerateArray().Select(ParseUser).ToList();
}