using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

const ushort DefaultPort = 9987;
const int TimeoutSeconds = 10;

var port = args.Length > 0 && ushort.TryParse(args[0], out var p) ? p : DefaultPort;
using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
var rooms = new ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>>();

Console.WriteLine($"VoxCore Server listening on UDP {port}");
Console.WriteLine("Press Ctrl+C to stop.");

_ = CleanupLoopAsync(udp, rooms);

while (true)
{
    try
    {
        var result = await udp.ReceiveAsync();
        var data = result.Buffer;
        if (data.Length < 2) continue;
        _ = HandleAsync(udp, rooms, result.RemoteEndPoint, data);
    }
    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.HostUnreachable or SocketError.MessageSize or SocketError.OperationAborted)
    {
        // ICMP error от клиента, который закрыл сокет — пропускаем
        await Task.Delay(5);
    }
}

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
            if (dead.Count > 0 && !room.IsEmpty)
                await BroadcastMembers(udp, dead[0].Key, roomName, room);
            if (room.IsEmpty) rooms.TryRemove(roomName, out _);
        }
    }
}

static async Task HandleAsync(UdpClient udp, ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, Member>> rooms, IPEndPoint from, byte[] data)
{
    var type = data[0];
    var roomLen = data[1];
    if (data.Length < 2 + roomLen) return;
    var roomName = Encoding.UTF8.GetString(data, 2, roomLen);

    var room = rooms.GetOrAdd(roomName, _ => new ConcurrentDictionary<IPEndPoint, Member>());

    switch (type)
    {
        case 0x01: // join [0x01][roomLen][room][nameLen][name]
            if (data.Length < 3 + roomLen) return;
            var nameLen = data[2 + roomLen];
            if (data.Length < 3 + roomLen + nameLen) return;
            var name = Encoding.UTF8.GetString(data, 3 + roomLen, nameLen);
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

        case 0x03: // audio [0x03][roomLen][room][opus...]
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

class Member
{
    public string Name { get; }
    public DateTime LastSeen { get; set; }
    public Member(string name, DateTime lastSeen) { Name = name; LastSeen = lastSeen; }
}