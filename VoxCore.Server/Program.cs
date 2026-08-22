using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VoxCore.Server;

const ushort VoicePort = 9987;
const ushort ApiPort = 9988;
const int TimeoutSeconds = 10;

var dataDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "data");
var store = new Store(dataDir);
var rooms = new ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>>();

// WebRTC signaling: roomId -> (userId -> peerConnection)
var webrtcRooms = new ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>>();
// Pending offers: offerId -> (fromUserId, roomId, sdp)
var pendingOffers = new ConcurrentDictionary<string, (int UserId, string RoomId, string Sdp)>();

using var voiceUdp = new UdpClient(new IPEndPoint(IPAddress.Any, VoicePort));
var apiTcp = new TcpListener(IPAddress.Any, ApiPort);
apiTcp.Start();

Console.WriteLine($"VoxCore Server: voice UDP {VoicePort}, API TCP {ApiPort}, data={dataDir}");
Console.WriteLine("WebRTC signaling enabled.");
Console.WriteLine("Press Ctrl+C to stop.");

_ = CleanupLoopAsync(voiceUdp, rooms);
_ = AcceptApiClientsAsync(apiTcp, store, rooms, webrtcRooms);

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

static async Task AcceptApiClientsAsync(TcpListener listener, Store store, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
{
    while (true)
    {
        try
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleApiClientAsync(client, store, rooms, webrtcRooms);
        }
        catch { await Task.Delay(10); }
    }
}

static async Task HandleApiClientAsync(TcpClient client, Store store, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
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
                var response = ProcessApi(line, store, rooms, webrtcRooms);
                await writer.WriteLineAsync(response);
            }
        }
    }
    catch { }
}

static string ProcessApi(string line, Store store, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
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
        "send_message" => user is null ? Error("unauthorized") : SendMessage(req, store, user.Id),
        "get_messages" => user is null ? Error("unauthorized") : GetMessages(req, store, user.Id),
        "mark_read" => user is null ? Error("unauthorized") : MarkRead(req, store, user.Id),
        "unread_count" => user is null ? Error("unauthorized") : UnreadCount(store, user.Id),
        "send_channel_message" => user is null ? Error("unauthorized") : SendChannelMessage(req, store, user.Id),
        "get_channel_messages" => user is null ? Error("unauthorized") : GetChannelMessages(req, store, user.Id),
        "webrtc_join" => user is null ? Error("unauthorized") : WebRTCJoin(req, store, user, webrtcRooms),
        "webrtc_leave" => user is null ? Error("unauthorized") : WebRTCLeave(req, store, user.Id, webrtcRooms),
        "webrtc_offer" => user is null ? Error("unauthorized") : WebRTCOffer(req, store, user, webrtcRooms),
        "webrtc_answer" => user is null ? Error("unauthorized") : WebRTCAnswer(req, store, user.Id, webrtcRooms),
        "webrtc_ice" => user is null ? Error("unauthorized") : WebRTCIce(req, store, user.Id, webrtcRooms),
        "screen_start" => user is null ? Error("unauthorized") : ScreenStart(req, store, user.Id),
        "screen_stop" => user is null ? Error("unauthorized") : ScreenStop(user.Id),
        "screen_frame" => user is null ? Error("unauthorized") : ScreenFrame(req, user.Id, webrtcRooms),
        "screen_list" => ScreenList(store),
        "screen_get" => ScreenGet(req, store),
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
        .Select(u => new
        {
            u.Id,
            u.Name,
            u.Color,
            Online = store.Tokens.ContainsValue(u.Id),
            State = store.GetFriendState(userId, u.Id).ToString()
        })
        .ToList();
    return Ok(new { users = items });
}

static string SendFriendRequest(JsonElement req, Store store, int userId)
{
    var name = (GetString(req, "name") ?? "").Trim();
    if (name.Length == 0) return Error("укажи ник");
    var target = store.FindUserByName(name);
    if (target is null) return Error("пользователь не найден");
    switch (store.GetFriendState(userId, target.Id))
    {
        case FriendState.Friend:
            return Error("вы уже друзья");
        case FriendState.Requested:
            return Error("запрос уже отправлен, ожидает принятия");
        case FriendState.Incoming:
            return Error("этот пользователь уже отправил тебе запрос — прими его во вкладке Друзья");
    }
    if (!store.SendFriendRequest(userId, target.Id))
        return Error("нельзя отправить запрос");
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

static string SendMessage(JsonElement req, Store store, int userId)
{
    if (!req.TryGetProperty("to", out var toEl) || !toEl.TryGetInt32(out var toId)) return Error("bad to");
    var text = (GetString(req, "text") ?? "").Trim();
    if (text.Length == 0) return Error("текст пустой");
    if (text.Length > 2000) return Error("максимум 2000 символов");
    var target = store.FindUserById(toId);
    if (target is null) return Error("пользователь не найден");
    var msg = store.SendMessage(userId, toId, text);
    return Ok(new { message = new { msg.Id, From = userId, To = toId, msg.Text, Sent = msg.SentAt } });
}

static string GetMessages(JsonElement req, Store store, int userId)
{
    if (!req.TryGetProperty("with", out var withEl) || !withEl.TryGetInt32(out var withId)) return Error("bad with");
    var limit = req.TryGetProperty("limit", out var lEl) && lEl.TryGetInt32(out var l) ? Math.Clamp(l, 1, 200) : 50;
    var msgs = store.GetMessages(userId, withId, limit)
        .Select(m => new { m.Id, From = m.FromUserId, To = m.ToUserId, m.Text, Sent = m.SentAt, m.Read })
        .ToList();
    store.MarkAsRead(userId, withId);
    return Ok(new { messages = msgs });
}

static string MarkRead(JsonElement req, Store store, int userId)
{
    if (!req.TryGetProperty("with", out var withEl) || !withEl.TryGetInt32(out var withId)) return Error("bad with");
    store.MarkAsRead(userId, withId);
    return Ok(new { });
}

static string UnreadCount(Store store, int userId)
{
    return Ok(new { count = store.GetUnreadCount(userId) });
}

static string SendChannelMessage(JsonElement req, Store store, int userId)
{
    if (!req.TryGetProperty("channel", out var chEl) || !chEl.TryGetInt32(out var channelId)) return Error("bad channel");
    var text = (GetString(req, "text") ?? "").Trim();
    if (text.Length == 0) return Error("текст пустой");
    if (text.Length > 2000) return Error("максимум 2000 символов");
    if (store.Channels.All(c => c.Id != channelId)) return Error("канал не найден");
    var msg = store.SendChannelMessage(channelId, userId, text);
    var sender = store.FindUserById(userId);
    return Ok(new { message = new { msg.Id, SenderId = userId, Sender = sender?.Name ?? "?", SenderColor = sender?.Color ?? "#5865f2", msg.Text, Sent = msg.SentAt } });
}

static string GetChannelMessages(JsonElement req, Store store, int userId)
{
    if (!req.TryGetProperty("channel", out var chEl) || !chEl.TryGetInt32(out var channelId)) return Error("bad channel");
    var limit = req.TryGetProperty("limit", out var lEl) && lEl.TryGetInt32(out var l) ? Math.Clamp(l, 1, 500) : 100;
    var msgs = store.GetChannelMessages(channelId, limit)
        .Select(m =>
        {
            var sender = store.FindUserById(m.FromUserId);
            return new { m.Id, SenderId = m.FromUserId, Sender = sender?.Name ?? "?", SenderColor = sender?.Color ?? "#5865f2", m.Text, Sent = m.SentAt };
        })
        .ToList();
    return Ok(new { messages = msgs });
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

// ---------- WebRTC Signaling ----------

static string WebRTCJoin(JsonElement req, Store store, User user, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
{
    if (!req.TryGetProperty("channel", out var chEl) || !chEl.TryGetInt32(out var channelId)) return Error("bad channel");
    var channel = store.Channels.FirstOrDefault(c => c.Id == channelId);
    if (channel is null) return Error("канал не найден");
    var roomId = channel.Name;
    var room = webrtcRooms.GetOrAdd(roomId, _ => new ConcurrentDictionary<int, RTCPeerConnection>());
    if (room.ContainsKey(user.Id)) return Error("уже в комнате");

    var peers = room.Keys.Where(id => id != user.Id).ToList();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WEBRTC JOIN  room={roomId} user={user.Name} peers={peers.Count}");
    return Ok(new { peers, roomId });
}

static string WebRTCLeave(JsonElement req, Store store, int userId, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
{
    var roomId = GetString(req, "room") ?? "";
    if (!webrtcRooms.TryGetValue(roomId, out var room)) return Error("комната не найдена");
    if (room.TryRemove(userId, out var pc))
    {
        pc.Close("left");
        pc.Dispose();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WEBRTC LEAVE room={roomId} userId={userId}");
        if (room.IsEmpty) webrtcRooms.TryRemove(roomId, out _);
    }
    return Ok(new { });
}

static string WebRTCOffer(JsonElement req, Store store, User user, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
{
    if (!req.TryGetProperty("sdp", out var sdpEl)) return Error("bad sdp");
    var sdp = sdpEl.GetString() ?? "";
    var roomId = GetString(req, "room") ?? "";
    if (!webrtcRooms.TryGetValue(roomId, out var room)) return Error("комната не найдена");

    var pc = CreatePeerConnection(roomId, user, webrtcRooms);
    room[user.Id] = pc;

    var audioTrack = new MediaStreamTrack(
        new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1"));
    pc.addTrack(audioTrack);

    var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp };
    var result = pc.setRemoteDescription(offer);
    if (result != SetDescriptionResultEnum.OK) return Error($"setRemoteDescription failed: {result}");

    var answer = pc.createAnswer(null);
    pc.setLocalDescription(answer);

    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline && pc.iceConnectionState != RTCIceConnectionState.connected && pc.iceConnectionState != RTCIceConnectionState.failed)
    {
        Thread.Sleep(100);
    }

    var peers = room.Keys.Where(id => id != user.Id).ToList();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WEBRTC OFFER  room={roomId} user={user.Name} ice={pc.iceConnectionState} peers={peers.Count}");
    return Ok(new { sdp = pc.localDescription.sdp, peers });
}

static string WebRTCAnswer(JsonElement req, Store store, int userId, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
{
    if (!req.TryGetProperty("sdp", out var sdpEl)) return Error("bad sdp");
    var sdp = sdpEl.GetString() ?? "";
    var roomId = GetString(req, "room") ?? "";
    if (!webrtcRooms.TryGetValue(roomId, out var room)) return Error("комната не найдена");
    if (!room.TryGetValue(userId, out var pc)) return Error("не в комнате");

    var answer = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp };
    var result = pc.setRemoteDescription(answer);
    if (result != SetDescriptionResultEnum.OK) return Error($"setRemoteDescription failed: {result}");
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WEBRTC ANSWER userId={userId}");
    return Ok(new { });
}

static string WebRTCIce(JsonElement req, Store store, int userId, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
{
    var roomId = GetString(req, "room") ?? "";
    if (!webrtcRooms.TryGetValue(roomId, out var room)) return Error("комната не найдена");
    if (!room.TryGetValue(userId, out var pc)) return Error("не в комнате");

    if (req.TryGetProperty("candidate", out var candEl))
    {
        var candidate = candEl.GetString() ?? "";
        var iceInit = new RTCIceCandidateInit { candidate = candidate };
        pc.addIceCandidate(iceInit);
    }
    return Ok(new { });
}

static RTCPeerConnection CreatePeerConnection(string roomId, User user, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
{
    var config = new RTCConfiguration
    {
        iceServers = new List<RTCIceServer>
        {
            new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
            new RTCIceServer
            {
                urls = "turn:194.31.204.5:3478",
                username = "voxcore",
                credential = "voxcore123"
            },
            new RTCIceServer
            {
                urls = "turn:194.31.204.5:3478?transport=tcp",
                username = "voxcore",
                credential = "voxcore123"
            }
        }
    };
    var pc = new RTCPeerConnection(config);

    pc.OnRtpPacketReceived += (ep, media, rtpPkt) =>
    {
        if (webrtcRooms.TryGetValue(roomId, out var room))
        {
            foreach (var peer in room)
            {
                if (peer.Key != user.Id && peer.Value != null)
                {
                    try
                    {
                        peer.Value.SendRtpRaw(media, rtpPkt.Payload, rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
                    }
                    catch { }
                }
            }
        }
    };

    pc.onconnectionstatechange += (state) =>
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WebRTC state userId={user.Name}: {state}");
        if (state == RTCPeerConnectionState.failed)
        {
            pc.Close("ice failure");
        }
    };

    return pc;
}

static string ScreenStart(JsonElement req, Store store, int userId)
{
    var channelId = req.TryGetProperty("channel", out var ch) ? ch.GetInt32() : 0;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Screen share started by userId={userId} in channel {channelId}");
    return JsonSerializer.Serialize(new { ok = true });
}

static string ScreenStop(int userId)
{
    ScreenFrameStore.Remove(userId);
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Screen share stopped by userId={userId}");
    return JsonSerializer.Serialize(new { ok = true });
}

static string ScreenFrame(JsonElement req, int userId, ConcurrentDictionary<string, ConcurrentDictionary<int, RTCPeerConnection>> webrtcRooms)
{
    var room = req.TryGetProperty("room", out var r) ? r.GetString() ?? "" : "";
    var frameB64 = req.TryGetProperty("frame", out var f) ? f.GetString() ?? "" : "";

    if (frameB64.Length == 0) return Error("пустой кадр");

    ScreenFrameStore.StoreFrame(userId, frameB64);

    if (webrtcRooms.TryGetValue(room, out var peers))
    {
        foreach (var peer in peers)
        {
            if (peer.Key != userId)
                ScreenFrameStore.StoreFrame(userId, frameB64);
        }
    }

    return JsonSerializer.Serialize(new { ok = true });
}

static string ScreenList(Store store)
{
    var activeIds = ScreenFrameStore.GetActiveSharers();
    var names = new List<string>();
    foreach (var id in activeIds)
    {
        var u = store.FindUserById(id);
        if (u != null) names.Add(u.Name);
    }
    return JsonSerializer.Serialize(new { ok = true, sharers = names });
}

static string ScreenGet(JsonElement req, Store store)
{
    var name = req.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
    if (string.IsNullOrEmpty(name)) return Error("нет имени");

    var user = store.FindUserByName(name);
    if (user == null) return Error("нет такого юзера");

    var frame = ScreenFrameStore.GetFrameB64(user.Id);
    if (frame == null) return JsonSerializer.Serialize(new { ok = true, frame = "" });

    return JsonSerializer.Serialize(new { ok = true, frame });
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