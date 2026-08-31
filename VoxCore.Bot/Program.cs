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
///   echo     — repeats received audio back
///   tone     — sends 1kHz test tone
///   listen   — just receives and plays audio
///   diagnose — analyzes audio quality from all participants
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
                    Console.WriteLine("  echo     — repeats your voice back (test roundtrip)");
                    Console.WriteLine("  tone     — sends 1kHz test tone (test audio path)");
                    Console.WriteLine("  listen   — just listens, plays received audio");
                    Console.WriteLine("  diagnose — analyzes audio quality from all participants");
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
        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        encoder.Bitrate = 48000;
        encoder.Complexity = 5;
        encoder.UseDTX = false;
        encoder.UseInbandFEC = false;

        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        // Audio playback
        var waveFormat = new WaveFormat(SampleRate, 16, Channels);
        var playbackBuffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(400),
            DiscardOnBufferOverflow = true
        };
        var playback = new WaveOutEvent();
        playback.Init(playbackBuffer);
        playback.Volume = 1.0f;
        playback.Play();

        Console.WriteLine($"Connecting to {server}:{port}...");

        var sendLock = new object();

        // Send join
        SendJoin(udp, channel, name, sendLock);
        Console.WriteLine($"Joined channel \"{channel}\"");

        // Heartbeat sender — keep alive in server room
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long nextBeat = 5000;
            while (!ct.IsCancellationRequested)
            {
                long now = sw.ElapsedMilliseconds;
                if (now < nextBeat)
                {
                    int sleep = (int)(nextBeat - now);
                    if (sleep > 0)
                        await Task.Delay(sleep, ct).ConfigureAwait(false);
                }
                try
                {
                    SendHeartbeat(udp, channel, sendLock);
                    nextBeat += 5000;
                    if (nextBeat < sw.ElapsedMilliseconds - 5000)
                        nextBeat = sw.ElapsedMilliseconds + 5000;
                }
                catch { break; }
            }
        }, ct);

        if (mode == "tone")
        {
            Console.WriteLine("Sending 1kHz test tone... (Ctrl+C to stop)");
            _ = Task.Run(() => ToneSender(udp, encoder, channel, name, sendLock, ct), ct);
        }
        else if (mode == "echo")
        {
            Console.WriteLine("Echo mode: speaking into mic will repeat back. (Ctrl+C to stop)");
        }
        else if (mode == "diagnose")
        {
            Console.WriteLine("Diagnose mode: analyzing audio quality from participants. (Ctrl+C to stop)");
        }
        else
        {
            Console.WriteLine("Listen mode: receiving audio only. (Ctrl+C to stop)");
        }

        // Receive loop
        var recvBuf = new byte[8192];
        var pcmBuf = new short[FrameSize];
        var outBytes = new byte[FrameBytes];
        var opusBuf = new byte[4000];
        long recvCount = 0;

        // Diagnose mode: per-speaker stats
        var speakerStats = new Dictionary<string, SpeakerStats>();
        var analyzeSw = System.Diagnostics.Stopwatch.StartNew();
        long nextAnalyze = 5000;

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

                        // Diagnose: analyze every speaker
                        if (mode == "diagnose" && n > 0)
                        {
                            if (!speakerStats.ContainsKey(speaker))
                                speakerStats[speaker] = new SpeakerStats();
                            AnalyzeFrame(speakerStats[speaker], pcmBuf, n);
                        }

                        if (mode == "listen" && n > 0)
                        {
                            // Listen mode only: play received audio
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
                            // Echo: encode and send back (do NOT play locally — causes feedback)
                            int encN = encoder.Encode(pcmBuf.AsSpan(), n, opusBuf.AsSpan(), opusBuf.Length);
                            if (encN > 0)
                                SendAudio(udp, encoder, opusBuf, encN, channel, name, sendLock);
                        }
                        break;

                    case 0x06: // members
                        var members = ParseMembers(data);
                        Console.WriteLine($"  Members: [{string.Join(", ", members)}]");
                        break;

                    case 0x07: // pong
                        break;
                }

                // Periodic analyze report
                if (mode == "diagnose" && analyzeSw.ElapsedMilliseconds >= nextAnalyze)
                {
                    nextAnalyze += 5000;
                    PrintAnalysis(speakerStats);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { if (ct.IsCancellationRequested) break; }
        }

        // Send leave
        SendLeave(udp, channel, sendLock);
        Console.WriteLine("Left channel.");
        playback.Stop();
        playback.Dispose();
        udp.Close();
    }

    static async Task ToneSender(UdpClient udp, IOpusEncoder encoder, string channel, string name, object sendLock, CancellationToken ct)
    {
        var pcmFrame = new short[FrameSize];
        var opusBuf = new byte[4000];
        double phase = 0;
        double freq = 1000; // 1kHz
        double amplitude = 0.5;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long nextFrameMs = 0;

        while (!ct.IsCancellationRequested)
        {
            long now = sw.ElapsedMilliseconds;
            if (now < nextFrameMs)
            {
                int sleep = (int)(nextFrameMs - now);
                if (sleep > 0)
                    await Task.Delay(sleep, ct).ConfigureAwait(false);
            }

            for (int i = 0; i < FrameSize; i++)
            {
                pcmFrame[i] = (short)(amplitude * 32767 * Math.Sin(2 * Math.PI * freq * phase));
                phase += 1.0 / SampleRate;
                if (phase > 1.0) phase -= 1.0;
            }

            int n = encoder.Encode(pcmFrame.AsSpan(), FrameSize, opusBuf.AsSpan(), opusBuf.Length);
            if (n > 0)
                SendAudio(udp, encoder, opusBuf, n, channel, name, sendLock);

            nextFrameMs += 20;
            if (nextFrameMs < sw.ElapsedMilliseconds - 100)
                nextFrameMs = sw.ElapsedMilliseconds;
        }
    }

    static void SendHeartbeat(UdpClient udp, string room, object sendLock)
    {
        var roomBytes = Encoding.UTF8.GetBytes(room);
        var packet = new byte[2 + roomBytes.Length];
        packet[0] = 0x04;
        packet[1] = (byte)roomBytes.Length;
        roomBytes.CopyTo(packet, 2);
        lock (sendLock) { try { udp.Send(packet, packet.Length); } catch { } }
    }

    static void SendJoin(UdpClient udp, string room, string name, object sendLock)
    {
        var roomBytes = Encoding.UTF8.GetBytes(room);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var packet = new byte[3 + roomBytes.Length + nameBytes.Length];
        packet[0] = 0x01;
        packet[1] = (byte)roomBytes.Length;
        roomBytes.CopyTo(packet, 2);
        packet[2 + roomBytes.Length] = (byte)nameBytes.Length;
        nameBytes.CopyTo(packet, 3 + roomBytes.Length);
        lock (sendLock) { udp.Send(packet, packet.Length); }
    }

    static void SendLeave(UdpClient udp, string room, object sendLock)
    {
        var roomBytes = Encoding.UTF8.GetBytes(room);
        var packet = new byte[2 + roomBytes.Length];
        packet[0] = 0x02;
        packet[1] = (byte)roomBytes.Length;
        roomBytes.CopyTo(packet, 2);
        lock (sendLock) { try { udp.Send(packet, packet.Length); } catch { } }
    }

    static void SendAudio(UdpClient udp, IOpusEncoder encoder, byte[] opus, int len, string room, string name, object sendLock)
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
        lock (sendLock) { try { udp.Send(packet, packet.Length); } catch { } }
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

    // ===== Diagnostic Analysis =====

    class SpeakerStats
    {
        public long FrameCount;
        public long ClipCount;
        public double SumRms;
        public double MaxPeak;
        public double MinRms = double.MaxValue;
        public double[] FreqEnergy = new double[8]; // 8 bands
        public long ActiveFrames; // frames above noise floor
    }

    static void AnalyzeFrame(SpeakerStats stats, short[] pcm, int n)
    {
        stats.FrameCount++;

        // RMS + Peak
        double sumSq = 0;
        double peak = 0;
        for (int i = 0; i < n; i++)
        {
            double s = pcm[i] / 32768.0;
            sumSq += s * s;
            double abs = Math.Abs(s);
            if (abs > peak) peak = abs;
            if (abs > 0.95) stats.ClipCount++;
        }
        double rms = Math.Sqrt(sumSq / n);
        stats.SumRms += rms;
        if (rms > stats.MaxPeak) stats.MaxPeak = rms;
        if (rms < stats.MinRms && rms > 0.001) stats.MinRms = rms;
        if (rms > 0.02) stats.ActiveFrames++;

        // Simple DFT: 8 frequency bands (0-3kHz, split into 375Hz bands)
        for (int band = 0; band < 8; band++)
        {
            double freq = (band + 0.5) * 375.0;
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double angle = 2.0 * Math.PI * freq * i / SampleRate;
                re += pcm[i] / 32768.0 * Math.Cos(angle);
                im -= pcm[i] / 32768.0 * Math.Sin(angle);
            }
            stats.FreqEnergy[band] += Math.Sqrt(re * re + im * im) / n;
        }
    }

    static void PrintAnalysis(Dictionary<string, SpeakerStats> stats)
    {
        Console.WriteLine();
        Console.WriteLine($"═══════ AUDIO ANALYSIS ({DateTime.Now:HH:mm:ss}) ═══════");

        if (stats.Count == 0)
        {
            Console.WriteLine("  No speakers detected.");
            return;
        }

        foreach (var kv in stats)
        {
            string nick = kv.Key;
            var s = kv.Value;
            if (s.FrameCount == 0) continue;

            double avgRms = s.SumRms / s.FrameCount;
            double avgDbFs = 20 * Math.Log10(Math.Max(avgRms, 1e-10));
            double peakDbFs = 20 * Math.Log10(Math.Max(s.MaxPeak, 1e-10));
            double minDbFs = s.MinRms < double.MaxValue ? 20 * Math.Log10(Math.Max(s.MinRms, 1e-10)) : -999;
            double activityPct = s.ActiveFrames * 100.0 / s.FrameCount;
            double clipPct = s.ClipCount * 100.0 / s.FrameCount;

            // Noise floor estimate (from quietest 10% of active frames)
            string noiseFloor = minDbFs > -20 ? "⚠️ LOUD" : minDbFs > -40 ? "moderate" : "✅ quiet";

            // Clipping
            string clipStatus = clipPct > 5 ? $"🔴 CLIPPING ({clipPct:F1}%)" : clipPct > 0 ? $"⚠️ slight clip ({clipPct:F1}%)" : "✅ no clip";

            // Spectral tilt: low vs high energy
            double lowE = s.FreqEnergy[0] + s.FreqEnergy[1]; // 0-750Hz
            double midE = s.FreqEnergy[2] + s.FreqEnergy[3] + s.FreqEnergy[4]; // 750-1875Hz
            double highE = s.FreqEnergy[5] + s.FreqEnergy[6] + s.FreqEnergy[7]; // 1875-3000Hz
            string spectral;
            if (lowE > midE * 2 && lowE > highE * 3)
                spectral = "🔽 LOW (вентилятор/гул)";
            else if (highE > lowE * 2 && highE > midE * 2)
                spectral = "🔼 HIGH (шипение/фиск)";
            else
                spectral = "📊 balanced";

            // Echo hint: if noise floor is high but activity is low → possible echo
            string echoHint = "";
            if (minDbFs > -30 && activityPct < 50)
                echoHint = " ← 🔄 possible echo/feedback";

            Console.WriteLine($"  🎤 {nick}:");
            Console.WriteLine($"     Peak: {peakDbFs:F1} dBFS | Avg: {avgDbFs:F1} dBFS | Noise floor: {minDbFs:F1} dBFS ({noiseFloor})");
            Console.WriteLine($"     Activity: {activityPct:F0}% | Clip: {clipStatus}");
            Console.WriteLine($"     Spectrum: {spectral}{echoHint}");
        }

        Console.WriteLine("═══════════════════════════════════════════════");
        Console.WriteLine();
    }
}
