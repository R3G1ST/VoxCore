using System;
using System.Collections.Generic;

namespace VoxCore.Client.Dsp;

/// <summary>
/// Адаптивный jitter buffer (замена NetEQ на приёме):
/// - хранит УЖЕ ДЕКОДИРОВАННЫЕ 20ms PCM-кадры (декод сразу при приёме RTP)
/// - target delay 60..300ms, адаптируется по девиации межпакетных интервалов
/// - reorder по sequence number, потеря = затухающий дубль (PLC-lite)
/// - переполнение = пропуск старых, недобор = прогрев тишиной
/// </summary>
public sealed class AdaptiveJitterBuffer
{
    private const int FrameSamples = 960;   // 20ms @48k
    private const int MinTargetMs = 40;
    private const int MaxTargetMs = 200;

    private readonly SortedDictionary<int, short[]> _slots = new();
    private readonly Queue<double> _arrivalDeltas = new();
    private readonly object _lock = new();

    private int _nextSeq = -1;
    private long _lastArrivalTicks;
    private double _targetMs = 100;
    private short[] _lastFrame = new short[FrameSamples];
    private bool _hasLast;
    private int _concealRun;

    public int TargetMs => (int)_targetMs;
    public int BufferedMs { get { lock (_lock) return _slots.Count * 20; } }
    public long LostFrames { get; private set; }
    public long PulledFrames { get; private set; }
    public int NextExpectedSeq { get { lock (_lock) return _nextSeq; } }

    public void PushDecoded(int seq, short[] frame)
    {
        long now = DateTime.UtcNow.Ticks;
        lock (_lock)
        {
            if (_nextSeq < 0) _nextSeq = seq;

            if (_lastArrivalTicks != 0)
            {
                double deltaMs = (now - _lastArrivalTicks) / (double)TimeSpan.TicksPerMillisecond;
                _arrivalDeltas.Enqueue(deltaMs);
                while (_arrivalDeltas.Count > 64) _arrivalDeltas.Dequeue();
                if (_arrivalDeltas.Count >= 8)
                {
                    double mean = 0; foreach (var d in _arrivalDeltas) mean += d;
                    mean /= _arrivalDeltas.Count;
                    double dev = 0; foreach (var d in _arrivalDeltas) dev += (d - mean) * (d - mean);
                    dev = Math.Sqrt(dev / _arrivalDeltas.Count);
                    double target = Math.Clamp(2.5 * dev + 40, MinTargetMs, MaxTargetMs);
                    _targetMs += (target - _targetMs) * 0.05;
                }
            }
            _lastArrivalTicks = now;

            if (seq < _nextSeq) return;                 // поздний/дубль
            if (!_slots.TryAdd(seq, frame)) return;

            // Переполнение относительно target: роняем самые старые
            int maxSlots = (int)(_targetMs / 20) + 4;
            while (_slots.Count > maxSlots)
            {
                var first = FirstEntry();
                _slots.Remove(first.Key);
                _nextSeq = Math.Max(_nextSeq, first.Key + 1);
                LostFrames++;
            }
        }
    }

    /// <summary>Взять 20ms кадр. false = прогрев/нет данных (outFrame = тишина или последний).</summary>
    public bool Pull(short[] outFrame)
    {
        lock (_lock)
        {
            if (_slots.Count == 0)
            {
                if (_hasLast) Array.Copy(_lastFrame, outFrame, FrameSamples);
                else Array.Clear(outFrame);
                return false;
            }

            if (_slots.TryGetValue(_nextSeq, out var frame))
            {
                _slots.Remove(_nextSeq);
                _concealRun = 0;
            }
            else
            {
                LostFrames++;
                _concealRun++;
                frame = new short[FrameSamples];
                float fade = MathF.Pow(0.7f, _concealRun);
                for (int i = 0; i < FrameSamples; i++)
                    frame[i] = (short)(_lastFrame[i] * fade);
            }

            _nextSeq = _slots.Count > 0 ? FirstEntry().Key : _nextSeq + 1;
            Array.Copy(frame, outFrame, FrameSamples);
            Array.Copy(frame, _lastFrame, FrameSamples);
            _hasLast = true;
            PulledFrames++;
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _slots.Clear();
            _nextSeq = -1;
            _lastArrivalTicks = 0;
            _hasLast = false;
            _concealRun = 0;
        }
    }

    private KeyValuePair<int, short[]> FirstEntry()
    {
        var e = _slots.GetEnumerator();
        e.MoveNext();
        var kvp = e.Current;
        e.Dispose();
        return kvp;
    }
}
