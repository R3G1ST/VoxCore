using System.Net;
using System.Net.Sockets;
using System.Text;
using Concentus;
using Concentus.Enums;

namespace VoxCore.DiagBot;

class Program
{
    const int SampleRate = 48000;
    const int Channels = 1;
    const int FrameSize = 960;

    static int Main(string[] args)
    {
        string channel = "test2";
        string server = "194.31.204.5";
        int port = 9987;
        string name = "DiagBot";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--channel" when i + 1 < args.Length: channel = args[++i]; break;
                case "--server" when i + 1 < args.Length: server = args[++i]; break;
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
            }
        }

        Console.WriteLine($"=== VoxCore Diagnostic Bot ===");
        Console.WriteLine($"Channel: {channel} | Server: {server}:{port} | Name: {name}");
        Console.WriteLine();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try { RunDiagBot(server, port, channel, name, cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { Console.WriteLine("\nStopped."); }
        catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); return 1; }
        return 0;
    }

    static async Task RunDiagBot(string server, int port, string channel, string name, CancellationToken ct)
    {
        var udp = new UdpClient(server, port);
        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        var sendLock = new object();

        // Per-speaker stats
        var speakers = new Dictionary<string, SpeakerStats>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long nextReport = 5000;
        long nextHeartbeat = 3000;
        long recvCount = 0;
        long lostCount = 0;
        int lastSeq = -1;

        SendJoin(udp, channel, name, sendLock);
        Console.WriteLine($"Joined \"{channel}\" — listening for audio...");

        // Heartbeat
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                SendHeartbeat(udp, channel, sendLock);
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }, ct);

        var pcmBuf = new short[FrameSize];
        var recvBuf = new byte[8192];

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

                        if (n > 0)
                        {
                            if (!speakers.ContainsKey(speaker))
                                speakers[speaker] = new SpeakerStats();
                            AnalyzeFrame(speakers[speaker], pcmBuf, n);
                        }
                        break;

                    case 0x06: // members
                        var members = ParseMembers(data);
                        Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] Members: [{string.Join(", ", members)}]");
                        break;

                    case 0x07: break; // pong
                }

                // Periodic report
                if (sw.ElapsedMilliseconds >= nextReport)
                {
                    nextReport += 5000;
                    PrintReport(speakers, recvCount);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { if (ct.IsCancellationRequested) break; }
        }

        SendLeave(udp, channel, sendLock);
        udp.Close();
    }

    // ===== Analysis =====
    class SpeakerStats
    {
        public long Frames;
        public long ClipFrames;
        public double SumRms;
        public double MaxPeak;
        public double MinRms = double.MaxValue;
        public long ActiveFrames;
        public double[] BandEnergy = new double[8];
    }

    static void AnalyzeFrame(SpeakerStats s, short[] pcm, int n)
    {
        s.Frames++;
        double sumSq = 0, peak = 0;
        for (int i = 0; i < n; i++)
        {
            double v = pcm[i] / 32768.0;
            sumSq += v * v;
            double a = Math.Abs(v);
            if (a > peak) peak = a;
            if (a > 0.95) s.ClipFrames++;
        }
        double rms = Math.Sqrt(sumSq / n);
        s.SumRms += rms;
        if (peak > s.MaxPeak) s.MaxPeak = peak;
        if (rms < s.MinRms && rms > 0.001) s.MinRms = rms;
        if (rms > 0.02) s.ActiveFrames++;

        for (int b = 0; b < 8; b++)
        {
            double freq = (b + 0.5) * 375.0;
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double angle = 2.0 * Math.PI * freq * i / SampleRate;
                re += pcm[i] / 32768.0 * Math.Cos(angle);
                im -= pcm[i] / 32768.0 * Math.Sin(angle);
            }
            s.BandEnergy[b] += Math.Sqrt(re * re + im * im) / n;
        }
    }

    static void PrintReport(Dictionary<string, SpeakerStats> speakers, long totalRecv)
    {
        Console.WriteLine();
        Console.WriteLine($"═══════ DIAGNOSIS ({DateTime.Now:HH:mm:ss}) | received: {totalRecv} ═══════");

        if (speakers.Count == 0)
        {
            Console.WriteLine("  No speakers yet. Waiting...");
            return;
        }

        foreach (var kv in speakers)
        {
            string nick = kv.Key;
            var s = kv.Value;
            if (s.Frames == 0) continue;

            double avgRms = s.SumRms / s.Frames;
            double avgDb = 20 * Math.Log10(Math.Max(avgRms, 1e-10));
            double peakDb = 20 * Math.Log10(Math.Max(s.MaxPeak, 1e-10));
            double noiseDb = s.MinRms < double.MaxValue ? 20 * Math.Log10(Math.Max(s.MinRms, 1e-10)) : -999;
            double actPct = s.ActiveFrames * 100.0 / s.Frames;
            double clipPct = s.ClipFrames * 100.0 / s.Frames;

            // Noise floor verdict
            string noiseVerdict = noiseDb > -15 ? "LOUD (background noise or echo)"
                                : noiseDb > -30 ? "moderate (some ambient noise)"
                                : "quiet (good)";

            // Clipping
            string clipVerdict = clipPct > 5 ? $"CLIPPING ({clipPct:F1}%)"
                               : clipPct > 0 ? $"slight clip ({clipPct:F1}%)"
                               : "clean";

            // Spectral analysis
            double lowE = s.BandEnergy[0] + s.BandEnergy[1];   // 0-750Hz
            double midE = s.BandEnergy[2] + s.BandEnergy[3] + s.BandEnergy[4]; // 750-1875Hz
            double highE = s.BandEnergy[5] + s.BandEnergy[6] + s.BandEnergy[7]; // 1875-3000Hz
            string spectral;
            if (lowE > midE * 2 && lowE > highE * 3)
                spectral = "LOW-heavy (fan/AC/rumble)";
            else if (highE > lowE * 2 && highE > midE * 2)
                spectral = "HIGH-heavy (hiss/static)";
            else if (midE > lowE * 1.5 && midE > highE * 1.5)
                spectral = "MID-heavy (voice/garbled)";
            else
                spectral = "balanced";

            // Echo detection: high noise floor + low speech activity
            string echo = (noiseDb > -25 && actPct < 30) ? " ⚠ POSSIBLE ECHO" : "";

            Console.WriteLine($"  🎤 {nick}:");
            Console.WriteLine($"     Peak: {peakDb:F1} dBFS | Avg: {avgDb:F1} dBFS | Noise floor: {noiseDb:F1} dBFS [{noiseVerdict}]");
            Console.WriteLine($"     Speech: {actPct:F0}% | Clip: [{clipVerdict}]");
            Console.WriteLine($"     Spectrum: [{spectral}]{echo}");
        }
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine();
    }

    // ===== Protocol =====
    static void SendJoin(UdpClient udp, string room, string name, object sendLock)
    {
        var roomBytes = Encoding.UTF8.GetBytes(room);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var pkt = new byte[3 + roomBytes.Length + nameBytes.Length];
        pkt[0] = 0x01;
        pkt[1] = (byte)roomBytes.Length;
        roomBytes.CopyTo(pkt, 2);
        pkt[2 + roomBytes.Length] = (byte)nameBytes.Length;
        nameBytes.CopyTo(pkt, 3 + roomBytes.Length);
        lock (sendLock) { udp.Send(pkt, pkt.Length); }
    }

    static void SendLeave(UdpClient udp, string room, object sendLock)
    {
        var roomBytes = Encoding.UTF8.GetBytes(room);
        var pkt = new byte[2 + roomBytes.Length];
        pkt[0] = 0x02;
        pkt[1] = (byte)roomBytes.Length;
        roomBytes.CopyTo(pkt, 2);
        lock (sendLock) { try { udp.Send(pkt, pkt.Length); } catch { } }
    }

    static void SendHeartbeat(UdpClient udp, string room, object sendLock)
    {
        var roomBytes = Encoding.UTF8.GetBytes(room);
        var pkt = new byte[2 + roomBytes.Length];
        pkt[0] = 0x04;
        pkt[1] = (byte)roomBytes.Length;
        roomBytes.CopyTo(pkt, 2);
        lock (sendLock) { try { udp.Send(pkt, pkt.Length); } catch { } }
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
