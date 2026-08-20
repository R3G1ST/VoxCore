using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VoxCore.Server;

const ushort VoicePort = 9987;
const ushort ApiPort = 9988;
const int TimeoutSeconds = 10;

var dataDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "data");
var store = new Store(dataDir);
var rooms = new ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>>();

using var voiceUdp = new UdpClient(new IPEndPoint(IPAddress.Any, VoicePort));
var apiTcp = new TcpListener(IPAddress.Any, ApiPort);
apiTcp.Start();

Console.WriteLine($"VoxCore Server: voice UDP {VoicePort}, API TCP {ApiPort}, data={dataDir}");
Console.WriteLine("Press Ctrl+C to stop.");

_ = CleanupLoopAsync(voiceUdp, rooms);
_ = AcceptApiClientsAsync(apiTcp, store, rooms);

while (true)
{
    try
    {
        var result = await voiceUdp.ReceiveAsync();
        var data = result.Buffer;
        if (data.Length < 2) continue;
        _ = HandleVoiceAsync(voiceUdp, rooms, store, result.RemoteEndPoint, data);
    }
    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.HostUnreachable or SocketError.MessageSize or SocketError.OperationAborted)
    {
        await Task.Delay(5);
    }
}

static async Task AcceptApiClientsAsync(TcpListener listener, Store store, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms)
{
    while (true)
    {
        try
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleApiClientAsync(client, store, rooms);
        }
        catch { await Task.Delay(10); }
    }
}

static async Task HandleApiClientAsync(TcpClient client, Store store, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms)
{
    try
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;
                var response = ProcessApi(line, store, rooms);
                await writer.WriteLineAsync(response);
            }
        }
    }
    catch { }
}

static string ProcessApi(string line, Store store, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms)
{
    JsonElement req;
    try { req = JsonDocument.Parse(line).RootElement; }
    catch { return Error("bad json"); }

    var op = req.GetProperty("op").GetString() ?? "";
    var token = GetString(req, "token");
    var user = token is null ? null : store.AuthByToken(token);

    return op switch
    {
        "register" => Register(req, store),
        "login" => Login(req, store),
        "channels" => user is null ? Error("unauthorized") : Channels(store, rooms),
        "create_channel" => user is null ? Error("unauthorized") : CreateChannel(req, store, user.Id),
        "delete_channel" => user is null ? Error("unauthorized") : DeleteChannel(req, store, user.Id),
        "join_channel" => user is null ? Error("unauthorized") : JoinChannel(req, store),
        "friends" => user is null ? Error("unauthorized") : Friends(store, user.Id),
        "add_friend" => user is null ? Error("unauthorized") : AddFriend(req, store, user.Id),
        "remove_friend" => user is null ? Error("unauthorized") : RemoveFriend(req, store, user.Id),
        "search_users" => user is null ? Error("unauthorized") : SearchUsers(req, store, user.Id),
        "friend_request" => user is null ? Error("unauthorized") : SendFriendRequest(req, store, user.Id),
        "friend_requests" => user is null ? Error("unauthorized") : FriendRequests(store, user.Id),
        "accept_friend" => user is null ? Error("unauthorized") : AcceptFriend(req, store, user.Id),
        "decline_friend" => user is null ? Error("unauthorized") : DeclineFriend(req, store, user.Id),
        _ => Error("unknown op")
    };
}

static string Register(JsonElement req, Store store)
{
    var name = (GetString(req, "name") ?? "").Trim();
    var pass = GetString(req, "pass") ?? "";
    if (name.Length < 2 || name.Length > 32) return Error("имя: 2-32 символа");
    if (pass.Length < 4) return Error("пароль: минимум 4 символа");
    var colors = new[] { "#5865f2", "#eb459e", "#faa61a", "#3ba55d", "#ed4245", "#9b59b6", "#00b0f4", "#f0b232" };
    var color = colors[Math.Abs(name.GetHashCode()) % colors.Length];
    var user = store.CreateUser(name, Store.Hash(pass), color);
    if (user is null) return Error("имя занято");
    var token = store.IssueToken(user);
    return Ok(new { user = new { user.Id, user.Name, user.Color }, token });
}

static string Login(JsonElement req, Store store)
{
    var name = (GetString(req, "name") ?? "").Trim();
    var pass = GetString(req, "pass") ?? "";
    var user = store.FindUserByName(name);
    if (user is null || !string.Equals(user.PassHash, Store.Hash(pass), StringComparison.Ordinal))
        return Error("неверный логин или пароль");
    var token = store.IssueToken(user);
    return Ok(new { user = new { user.Id, user.Name, user.Color }, token });
}

static string Channels(Store store, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms)
{
    var items = store.Channels
        .OrderBy(c => c.CreatedAt)
        .Select(c => new
        {
            c.Id,
            c.Name,
            c.OwnerId,
            HasPassword = c.PassHash is not null,
            Users = rooms.TryGetValue(c.Id.ToString(), out var room) ? room.Count : 0
        })
        .ToList();
    return Ok(new { channels = items });
}

static string CreateChannel(JsonElement req, Store store, int ownerId)
{
    var name = (GetString(req, "name") ?? "").Trim();
    if (name.Length < 1 || name.Length > 40) return Error("название: 1-40 символов");
    var password = GetString(req, "password");
    if (password is not null && password.Length > 0 && password.Length < 4) return Error("пароль: минимум 4 символа");
    var passHash = string.IsNullOrEmpty(password) ? null : Store.Hash(password);
    var channel = store.CreateChannel(name, passHash, ownerId);
    if (channel is null) return Error("не удалось создать");
    return Ok(new { channel = new { channel.Id, channel.Name, channel.OwnerId, HasPassword = channel.PassHash is not null } });
}

static string DeleteChannel(JsonElement req, Store store, int userId)
{
    if (!req.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return Error("bad id");
    return store.DeleteChannel(id, userId)
        ? Ok(new { })
        : Error("нет доступа или канал не найден");
}

static string JoinChannel(JsonElement req, Store store)
{
    if (!req.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return Error("bad id");
    var channel = store.Channels.FirstOrDefault(c => c.Id == id);
    if (channel is null) return Error("канал не найден");
    if (channel.PassHash is not null)
    {
        var password = GetString(req, "password") ?? "";
        if (!string.Equals(channel.PassHash, Store.Hash(password), StringComparison.Ordinal))
            return Error("неверный пароль канала");
    }
    return Ok(new { channel = new { channel.Id, channel.Name } });
}

static string Friends(Store store, int userId)
{
    var user = store.FindUserById(userId);
    if (user is null) return Error("пользователь не найден");
    var onlineIds = store.Tokens.Values.ToHashSet();
    var items = user.Friends
        .Select(store.FindUserById)
        .Where(f => f is not null)
        .Select(f => new { f!.Id, f.Name, f.Color, Online = onlineIds.Contains(f.Id) })
        .OrderByDescending(f => f.Online)
        .ThenBy(f => f.Name)
        .ToList();
    return Ok(new { friends = items });
}

static string AddFriend(JsonElement req, Store store, int userId)
{
    var name = (GetString(req, "name") ?? "").Trim();
    if (name.Length == 0) return Error("укажи ник");
    var friend = store.FindUserByName(name);
    if (friend is null) return Error("пользователь не найден");
    if (!store.AddFriend(userId, friend.Id)) return Error("нельзя добавить себя");
    return Ok(new { friend = new { friend.Id, friend.Name, friend.Color, Online = store.Tokens.ContainsValue(friend.Id) } });
}

static string RemoveFriend(JsonElement req, Store store, int userId)
{
    if (!req.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return Error("bad id");
    return store.RemoveFriend(userId, id)
        ? Ok(new { })
        : Error("друг не найден");
}

static string SearchUsers(JsonElement req, Store store, int userId)
{
    var query = (GetString(req, "query") ?? "").Trim();
    if (query.Length < 1) return Error("минимум 1 символ");
    var items = store.SearchUsers(query, userId)
        .Select(u => new { u.Id, u.Name, u.Color, Online = store.Tokens.ContainsValue(u.Id) })
        .ToList();
    return Ok(new { users = items });
}

static string SendFriendRequest(JsonElement req, Store store, int userId)
{
    var name = (GetString(req, "name") ?? "").Trim();
    if (name.Length == 0) return Error("укажи ник");
    var target = store.FindUserByName(name);
    if (target is null) return Error("пользователь не найден");
    if (!store.SendFriendRequest(userId, target.Id))
        return Error("нельзя отправить запрос (уже друзья или запрос отправлен)");
    return Ok(new { });
}

static string FriendRequests(Store store, int userId)
{
    var user = store.FindUserById(userId);
    if (user is null) return Error("пользователь не найден");
    var onlineIds = store.Tokens.Values.ToHashSet();
    var items = user.PendingRequests
        .Select(store.FindUserById)
        .Where(f => f is not null)
        .Select(f => new { f!.Id, f.Name, f.Color, Online = onlineIds.Contains(f.Id) })
        .ToList();
    return Ok(new { requests = items });
}

static string AcceptFriend(JsonElement req, Store store, int userId)
{
    if (!req.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return Error("bad id");
    return store.AcceptFriendRequest(userId, id)
        ? Ok(new { })
        : Error("запрос не найден");
}

static string DeclineFriend(JsonElement req, Store store, int userId)
{
    if (!req.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return Error("bad id");
    return store.DeclineFriendRequest(userId, id)
        ? Ok(new { })
        : Error("запрос не найден");
}

static string GetString(JsonElement el, string prop) =>
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

static string Ok(object data) => JsonSerializer.Serialize(new { ok = true, data });
static string Error(string err) => JsonSerializer.Serialize(new { ok = false, err });

static async Task CleanupLoopAsync(UdpClient udp, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms)
{
    while (true)
    {
        await Task.Delay(5000);
        foreach (var (roomName, room) in rooms)
        {
            var dead = room.Where(m => (DateTime.UtcNow - m.Value.LastSeen).TotalSeconds > TimeoutSeconds).ToList();
            foreach (var m in dead)
            {
                room.TryRemove(m.Key, out _);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TIMEOUT {roomName} <- {m.Key} ({m.Value.Name})");
            }
            if (room.IsEmpty) rooms.TryRemove(roomName, out _);
        }
    }
}

static async Task HandleVoiceAsync(UdpClient udp, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms, Store store, IPEndPoint from, byte[] data)
{
    var type = data[0];
    var roomName = Encoding.UTF8.GetString(data, 2, data[1]);
    var room = rooms.GetOrAdd(roomName, _ => new ConcurrentDictionary<IPEndPoint, Member>());

    switch (type)
    {
        case 0x01: // join [0x01][roomLen][room][nameLen][name]
            if (data.Length < 3 + roomName.Length) break;
            var nameLen = data[2 + roomName.Length];
            if (data.Length < 3 + roomName.Length + nameLen) break;
            var name = Encoding.UTF8.GetString(data, 3 + roomName.Length, nameLen);
            room[from] = new Member(name, DateTime.UtcNow);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] JOIN  {roomName} <- {from} ({name})  total: {room.Count}");
            await BroadcastMembers(udp, from, roomName, room);
            break;

        case 0x02: // leave
            if (room.TryRemove(from, out _))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] LEAVE {roomName} <- {from}");
                await BroadcastMembers(udp, from, roomName, room);
            }
            break;

        case 0x03: // audio [0x03][roomLen][room][nameLen][name][...]
            foreach (var member in room)
                if (!member.Key.Equals(from))
                    await udp.SendAsync(data, data.Length, member.Key);
            break;

        case 0x04: // heartbeat
            if (room.TryGetValue(from, out var m)) m.LastSeen = DateTime.UtcNow;
            break;
    }
}

static async Task BroadcastMembers(UdpClient udp, IPEndPoint from, string roomName, ConcurrentDictionary<IPEndPoint, Member> room)
{
    if (room.IsEmpty) return;
    var list = room.ToArray();
    var size = 2 + roomName.Length + 1 + list.Sum(m => 1 + m.Value.Name.Length);
    var buf = new byte[size];
    buf[0] = 0x06;
    buf[1] = (byte)roomName.Length;
    Encoding.UTF8.GetBytes(roomName, 0, roomName.Length, buf, 2);
    buf[2 + roomName.Length] = (byte)list.Length;
    var off = 3 + roomName.Length;
    foreach (var m in list)
    {
        var n = Encoding.UTF8.GetBytes(m.Value.Name);
        buf[off++] = (byte)n.Length;
        n.CopyTo(buf, off);
        off += n.Length;
    }
    foreach (var m in list)
        await udp.SendAsync(buf, buf.Length, m.Key);
}

public sealed class Member
{
    public string Name { get; }
    public DateTime LastSeen { get; set; }
    public Member(string name, DateTime lastSeen)
    {
        Name = name;
        LastSeen = lastSeen;
    }
}