using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;

namespace VoxCore.Bot;

/// <summary>
/// Voice bot for VoxCore testing.
/// Modes:
///   echo  — repeats received audio back
///   tone  — sends 1kHz test tone
///   listen — just receives and plays audio
/// </summary>
class Program
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSize = 960; // 20ms
    private const int FrameBytes = FrameSize * 2;

    static int Main(string[] args)
    {
        string channel = "test";
        string mode = "echo";
        string server = "194.31.204.5";
        int port = 9987;
        string name = "TestBot";

        // Parse args
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--channel" when i + 1 < args.Length: channel = args[++i]; break;
                case "--mode" when i + 1 < args.Length: mode = args[++i]; break;
                case "--server" when i + 1 < args.Length: server = args[++i]; break;
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--help":
                    Console.WriteLine("VoxCore Voice Bot");
                    Console.WriteLine("Usage: bot.exe [--channel name] [--mode echo|tone|listen] [--server ip] [--port port] [--name nickname]");
                    Console.WriteLine();
                    Console.WriteLine("Modes:");
                    Console.WriteLine("  echo   — repeats your voice back (test roundtrip)");
                    Console.WriteLine("  tone   — sends 1kHz test tone (test audio path)");
                    Console.WriteLine("  listen — just listens, plays received audio");
                    return 0;
            }
        }

        Console.WriteLine($"=== VoxCore Voice Bot ===");
        Console.WriteLine($"Channel: {channel}");
        Console.WriteLine($"Mode: {mode}");
        Console.WriteLine($"Server: {server}:{port}");
        Console.WriteLine($"Name: {name}");
        Console.WriteLine();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            RunBot(server, port, channel, name, mode, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nDisconnected.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        return 0;
    }

    static async Task RunBot(string server, int port, string channel, string name, string mode, CancellationToken ct)
    {
        var udp = new UdpClient(server, port);
        udp.Client.SendTimeout = 1000;
        udp.Client.ReceiveTimeout = 5000;

        // Opus encoder/decoder
        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 48000;
        encoder.Complexity = 5;
        encoder.UseDTX = false;
        encoder.UseInbandFEC = true;
        encoder.PacketLossPercent = 10;

        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        // Audio playback
        var waveFormat = new WaveFormat(SampleRate, 16, Channels);
        var playbackBuffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(200),
            DiscardOnBufferOverflow = true
        };
        var playback = new WaveOutEvent();
        playback.Init(playbackBuffer);
        playback.Volume = 1.0f;
        playback.Play();

        Console.WriteLine($"Connecting to {server}:{port}...");

        // Send join
        SendJoin(udp, channel, name);
        Console.WriteLine($"Joined channel \"{channel}\"");

        if (mode == "tone")
        {
            Console.WriteLine("Sending 1kHz test tone... (Ctrl+C to stop)");
            // Start tone sender
            _ = Task.Run(() => ToneSender(udp, encoder, channel, name, ct), ct);
        }
        else if (mode == "echo")
        {
            Console.WriteLine("Echo mode: speaking into mic will repeat back. (Ctrl+C to stop)");
        }
        else
        {
            Console.WriteLine("Listen mode: receiving audio only. (Ctrl+C to stop)");
        }

        // Receive loop
        var recvBuf = new byte[8192];
        var pcmBuf = new short[FrameSize];
        var outBytes = new byte[FrameBytes];
        long recvCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                int received = await udp.Client.ReceiveAsync(recvBuf, ct);
                if (received < 2) continue;
                var data = recvBuf.AsSpan(0, received).ToArray();

                switch (data[0])
                {
                    case 0x03: // audio
                        int roomLen = data[1];
                        int nameLen = data[2 + roomLen];
                        if (data.Length < 3 + roomLen + nameLen) continue;
                        var speaker = Encoding.UTF8.GetString(data, 3 + roomLen, nameLen);
                        var raw = data.AsSpan(3 + roomLen + nameLen).ToArray();
                        if (raw.Length == 0) continue;

                        int n = decoder.Decode(raw.AsSpan(), pcmBuf.AsSpan(), FrameSize, false);
                        recvCount++;

                        if (mode == "listen" || mode == "echo")
                        {
                            // Play received audio
                            for (int i = 0; i < n; i++)
                            {
                                outBytes[i * 2] = (byte)(pcmBuf[i] & 0xFF);
                                outBytes[i * 2 + 1] = (byte)((pcmBuf[i] >> 8) & 0xFF);
                            }
                            playbackBuffer.AddSamples(outBytes, 0, n * 2);

                            if (recvCount % 50 == 0)
                                Console.WriteLine($"  [recv #{recvCount}] from {speaker}: {n} samples");
                        }

                        if (mode == "echo" && n > 0)
                        {
                            // Echo: encode and send back
                            int encN = encoder.Encode(pcmBuf.AsSpan(), n, outBytes.AsSpan(), outBytes.Length);
                            if (encN > 0)
                                SendAudio(udp, encoder, outBytes, encN, channel, name);
                        }
                        break;

                    case 0x06: // members
                        var members = ParseMembers(data);
                        Console.WriteLine($"  Members: [{string.Join(", ", members)}]");
                        break;

                    case 0x07: // pong
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch { if (ct.IsCancellationRequested) break; }
        }

        // Send leave
        SendLeave(udp, channel);
        Console.WriteLine("Left channel.");
        playback.Stop();
        playback.Dispose();
        udp.Close();
    }

    static async Task ToneSender(UdpClient udp, IOpusEncoder encoder, string channel, string name, CancellationToken ct)
    {
        var pcmFrame = new short[FrameSize];
        var opusBuf = new byte[4000];
        double phase = 0;
        double freq = 1000; // 1kHz
        double amplitude = 0.5;

        while (!ct.IsCancellationRequested)
        {
            // Generate 20ms of 1kHz sine wave
            for (int i = 0; i < FrameSize; i++)
            {
                pcmFrame[i] = (short)(amplitude * 32767 * Math.Sin(2 * Math.PI * freq * phase));
                phase += 1.0 / SampleRate;
                if (phase > 1.0) phase -= 1.0;
            }

            int n = encoder.Encode(pcmFrame.AsSpan(), FrameSize, opusBuf.AsSpan(), opusBuf.Length);
            if (n > 0)
                SendAudio(udp, encoder, opusBuf, n, channel, name);

            await Task.Delay(20, ct); // 20ms frame
        }
    }

    static void SendJoin(UdpClient udp, string room, string name)
    {
        var roomBytes = Encoding.UTF8.GetBytes(room);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var packet = new byte[3 + roomBytes.Length + nameBytes.Length];
        packet[0] = 0x01;
        packet[1] = (byte)roomBytes.Length;
        roomBytes.CopyTo(packet, 2);
        packet[2 + roomBytes.Length] = (byte)nameBytes.Length;
        nameBytes.CopyTo(packet, 3 + roomBytes.Length);
        udp.Send(packet, packet.Length);
    }

    static void SendLeave(UdpClient udp, string room)
    {
        var roomBytes = Encoding.UTF8.GetBytes(room);
        var packet = new byte[2 + roomBytes.Length];
        packet[0] = 0x02;
        packet[1] = (byte)roomBytes.Length;
        roomBytes.CopyTo(packet, 2);
        try { udp.Send(packet, packet.Length); } catch { }
    }

    static void SendAudio(UdpClient udp, IOpusEncoder encoder, byte[] opus, int len, string room, string name)
    {
        var roomBytes = Encoding.UTF8.GetBytes(room);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var packet = new byte[2 + roomBytes.Length + 1 + nameBytes.Length + len];
        packet[0] = 0x03;
        packet[1] = (byte)roomBytes.Length;
        roomBytes.CopyTo(packet, 2);
        packet[2 + roomBytes.Length] = (byte)nameBytes.Length;
        nameBytes.CopyTo(packet, 3 + roomBytes.Length);
        Array.Copy(opus, 0, packet, 3 + roomBytes.Length + nameBytes.Length, len);
        try { udp.Send(packet, packet.Length); } catch { }
    }

    static List<string> ParseMembers(byte[] data)
    {
        int roomLen = data[1];
        int count = data[2 + roomLen];
        var names = new List<string>(count);
        var off = 3 + roomLen;
        for (int i = 0; i < count && off < data.Length; i++)
        {
            int nl = data[off++];
            if (off + nl > data.Length) break;
            names.Add(Encoding.UTF8.GetString(data, off, nl));
            off += nl;
        }
        return names;
    }
}
