using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace VoxCore.Client;

public sealed partial class VoiceGraphView : UserControl
{
    private const double NodeR = 26;
    private const double RestLen = 150;

    private static readonly Color ColWhite = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color ColCyanA = Color.FromArgb(45, 0, 229, 255);
    private static readonly Color ColCyanB = Color.FromArgb(255, 0, 229, 255);
    private static readonly Color ColMagentaA = Color.FromArgb(45, 255, 46, 196);
    private static readonly Color ColMagentaB = Color.FromArgb(255, 255, 46, 196);
    private static readonly Color ColRed = Color.FromArgb(255, 255, 92, 120);
    private static readonly Color ColMagentaText = Color.FromArgb(255, 255, 159, 232);
    private static readonly FontFamily Cascadia = new("Cascadia Code");

    private sealed class Node
    {
        public Node(MemberItem member) { Member = member; }
        public MemberItem Member;
        public double X, Y, VX, VY;
        public bool Speaking;
        public double Glow;
        public Grid Container = null!;
        public Ellipse Halo = null!;
    }

    private sealed class Edge
    {
        public int A;
        public int B;
        public Line Glow = null!;
        public Line Mid = null!;
        public Line Core = null!;
    }

    private sealed class Pulse
    {
        public double T;
        public Node Node = null!;
        public Ellipse Shape = null!;
    }

    private ObservableCollection<MemberItem>? _members;
    private string _selfName = "";
    private VoiceClient? _voice;
    private WebRTCVoiceClient? _webrtc;
    private AppSettings? _settings;
    private Action? _onEndCall;

    private readonly List<Node> _nodes = [];
    private readonly List<Edge> _edges = [];
    private readonly List<Pulse> _pulses = [];
    private readonly Dictionary<string, double[]> _waves = [];
    private readonly Dictionary<string, Rectangle[]> _streamBars = [];
    private readonly Random _rnd = new();
    private readonly DispatcherTimer _timer;
    private bool _inited;

    public event System.Action? HomeRequested;
    public event System.Action? SettingsRequested;

    public VoiceGraphView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Init(ObservableCollection<MemberItem> members, string selfName,
        VoiceClient voice, WebRTCVoiceClient? webrtc, AppSettings settings, Action onEndCall)
    {
        if (_inited) return;
        _inited = true;
        _members = members;
        _selfName = selfName;
        _voice = voice;
        _webrtc = webrtc;
        _settings = settings;
        _onEndCall = onEndCall;

        _members.CollectionChanged += Members_CollectionChanged;
        _voice.SpeakerStarted += OnSpeakerStarted;
        _voice.SpeakerStopped += OnSpeakerStopped;
        if (_webrtc is not null)
        {
            _webrtc.SpeakerStarted += OnSpeakerStarted;
            _webrtc.SpeakerStopped += OnSpeakerStopped;
        }

        foreach (var b in new[] { MicBtn, SpkBtn, HubSettingsBtn, EndCallBtn, HubHomeBtn })
        {
            b.PointerEntered += (s, _) => ((Button)s!).Opacity = 0.8;
            b.PointerExited += (s, _) => ((Button)s!).Opacity = 1;
        }

        RebuildNodes();
        UpdateUsersPill();
        UpdateStreamPanel();
        UpdateMicSpkUi();
        _timer.Start();
    }

    public void SetActive(bool active)
    {
        if (!_inited) return;
        if (active) _timer.Start();
        else _timer.Stop();
    }

    public void Shutdown()
    {
        _timer.Stop();
        if (!_inited || _members is null || _voice is null) return;
        _members.CollectionChanged -= Members_CollectionChanged;
        _voice.SpeakerStarted -= OnSpeakerStarted;
        _voice.SpeakerStopped -= OnSpeakerStopped;
        if (_webrtc is not null)
        {
            _webrtc.SpeakerStarted -= OnSpeakerStarted;
            _webrtc.SpeakerStopped -= OnSpeakerStopped;
        }
    }

    // ---------- построение графа ----------

    private void RebuildNodes()
    {
        if (_members is null) return;
        var old = _nodes.ToDictionary(n => n.Member.Name, n => (n.X, n.Y, n.Speaking));
        NodesCanvas.Children.Clear();
        _nodes.Clear();
        foreach (var m in _members)
        {
            var node = new Node(m);
            if (old.TryGetValue(m.Name, out var p))
            {
                node.X = p.X;
                node.Y = p.Y;
                node.Speaking = p.Speaking;
            }
            node.Container = BuildNodeVisual(node);
            NodesCanvas.Children.Add(node.Container);
            _nodes.Add(node);
        }
        RebuildEdges();
    }

    private Grid BuildNodeVisual(Node node)
    {
        bool self = node.Member.Name == _selfName;
        var container = new Grid { Width = 72, Height = 92 };

        node.Halo = new Ellipse
        {
            Width = 68,
            Height = 68,
            Stroke = node.Member.ColorBrush,
            StrokeThickness = 3,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var ring = new Ellipse
        {
            Width = 60,
            Height = 60,
            Stroke = new SolidColorBrush(ColWhite),
            StrokeThickness = self ? 2.2 : 1.1,
            Opacity = self ? 0.7 : 0.22,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var avatar = new Grid
        {
            Width = 52,
            Height = 52,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0)
        };
        avatar.Children.Add(new Ellipse { Fill = node.Member.ColorBrush });
        avatar.Children.Add(new TextBlock
        {
            Text = node.Member.Letter,
            FontFamily = Cascadia,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(ColWhite),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        var name = new TextBlock
        {
            Text = self ? node.Member.Name + " (ты)" : node.Member.Name,
            FontFamily = Cascadia,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 207, 214, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            MaxWidth = 130,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        container.Children.Add(node.Halo);
        container.Children.Add(ring);
        container.Children.Add(avatar);
        container.Children.Add(name);
        return container;
    }

    private void RebuildEdges()
    {
        EdgesCanvas.Children.Clear();
        _edges.Clear();
        int n = _nodes.Count;
        if (n < 2) return;

        var pairs = new List<(int A, int B)>();
        void Add(int a, int b)
        {
            if (a == b) return;
            if (a > b) (a, b) = (b, a);
            if (!pairs.Contains((a, b))) pairs.Add((a, b));
        }
        for (int i = 0; i < n; i++)
        {
            Add(i, (i + 1) % n);
            if (n > 3) Add(i, (i + 2) % n);
            if (n > 6) Add(i, (i + 3) % n);
        }

        int idx = 0;
        foreach (var (a, b) in pairs)
        {
            bool flip = idx++ % 2 == 1;
            var e = new Edge { A = a, B = b };
            e.Glow = MakeLine(flip ? ColMagentaA : ColCyanA, flip ? ColMagentaA : ColCyanA, 7);
            e.Mid = MakeLine(flip ? ColMagentaB : ColCyanB, flip ? ColCyanB : ColMagentaB, 3);
            e.Core = MakeLine(ColWhite, Color.FromArgb(200, 191, 251, 255), 1.4);
            EdgesCanvas.Children.Add(e.Glow);
            EdgesCanvas.Children.Add(e.Mid);
            EdgesCanvas.Children.Add(e.Core);
            _edges.Add(e);
        }
    }

    private static Line MakeLine(Color c1, Color c2, double thickness)
    {
        var grad = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0)
        };
        grad.GradientStops.Add(new GradientStop { Color = c1, Offset = 0 });
        grad.GradientStops.Add(new GradientStop { Color = c2, Offset = 1 });
        return new Line { Stroke = grad, StrokeThickness = thickness, IsHitTestVisible = false, Opacity = 0.5 };
    }

    // ---------- кадр ----------

    private void Tick()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w < 100 || h < 100) return;
        bool connected = (_voice is not null && _voice.IsConnected) || (_webrtc is not null && _webrtc.IsConnected);
        EndCallBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        EnsureLayout(w, h);
        StepPhysics(w, h);
        foreach (var nd in _nodes)
        {
            double target = nd.Speaking ? 1 : 0;
            nd.Glow += (target - nd.Glow) * 0.18;
            nd.Halo.Opacity = 0.55 * nd.Glow;
        }
        DrawEdges();
        StepPulses();
        foreach (var nd in _nodes)
        {
            Canvas.SetLeft(nd.Container, nd.X - 36);
            Canvas.SetTop(nd.Container, nd.Y - 36);
        }
        StepWaveforms();
    }

    private void EnsureLayout(double w, double h)
    {
        int empty = 0;
        foreach (var nd in _nodes)
            if (nd.X == 0 && nd.Y == 0) empty++;
        if (empty == 0) return;
        double cx = w / 2, cy = h / 2;
        double rad = Math.Min(w, h) / 3.5;
        int i = 0;
        foreach (var nd in _nodes)
        {
            if (nd.X != 0 || nd.Y != 0) continue;
            double ang = 2 * Math.PI * i / Math.Max(empty, 1) + _rnd.NextDouble() * 0.4;
            nd.X = cx + Math.Cos(ang) * rad;
            nd.Y = cy + Math.Sin(ang) * rad;
            i++;
        }
    }

    private void StepPhysics(double w, double h)
    {
        double cx = w / 2, cy = h / 2;
        int n = _nodes.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                var a = _nodes[i];
                var b = _nodes[j];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < 1)
                {
                    dx = _rnd.NextDouble() - 0.5;
                    dy = _rnd.NextDouble() - 0.5;
                    d2 = 1;
                }
                double f = 3000 / d2;
                if (f > 6) f = 6;
                double d = Math.Sqrt(d2);
                double ux = dx / d, uy = dy / d;
                a.VX -= ux * f; a.VY -= uy * f;
                b.VX += ux * f; b.VY += uy * f;
            }
        }
        foreach (var e in _edges)
        {
            var a = _nodes[e.A];
            var b = _nodes[e.B];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1) continue;
            double f = (d - RestLen) * 0.006;
            double ux = dx / d, uy = dy / d;
            a.VX += ux * f; a.VY += uy * f;
            b.VX -= ux * f; b.VY -= uy * f;
        }
        double topM = 90, botM = 110, sideM = 70;
        foreach (var nd in _nodes)
        {
            nd.VX += (cx - nd.X) * 0.0012;
            nd.VY += (cy - nd.Y) * 0.0012;
            if (nd.X < sideM) nd.VX += (sideM - nd.X) * 0.05;
            if (nd.X > w - sideM) nd.VX -= (nd.X - (w - sideM)) * 0.05;
            if (nd.Y < topM) nd.VY += (topM - nd.Y) * 0.05;
            if (nd.Y > h - botM) nd.VY -= (nd.Y - (h - botM)) * 0.05;
            nd.VX *= 0.82;
            nd.VY *= 0.82;
            double sp = Math.Sqrt(nd.VX * nd.VX + nd.VY * nd.VY);
            if (sp > 5)
            {
                nd.VX = nd.VX / sp * 5;
                nd.VY = nd.VY / sp * 5;
            }
            nd.X += nd.VX;
            nd.Y += nd.VY;
        }
    }

    private void DrawEdges()
    {
        foreach (var e in _edges)
        {
            var na = _nodes[e.A];
            var nb = _nodes[e.B];
            double boost = Math.Max(na.Glow, nb.Glow);
            SetLine(e.Glow, na, nb, 7 + 6 * boost, 0.12 + 0.35 * boost);
            SetLine(e.Mid, na, nb, 3 + 2 * boost, 0.30 + 0.45 * boost);
            SetLine(e.Core, na, nb, 1.4, 0.55 + 0.45 * boost);
        }
    }

    private static void SetLine(Line l, Node a, Node b, double width, double opacity)
    {
        l.X1 = a.X; l.Y1 = a.Y;
        l.X2 = b.X; l.Y2 = b.Y;
        l.StrokeThickness = width;
        l.Opacity = Math.Min(opacity, 1);
        if (l.Stroke is LinearGradientBrush g)
        {
            g.StartPoint = new Windows.Foundation.Point(a.X, a.Y);
            g.EndPoint = new Windows.Foundation.Point(b.X, b.Y);
        }
    }

    // ---------- пульсации ----------

    private void SpawnPulse(Node node)
    {
        var el = new Ellipse
        {
            Width = NodeR * 2,
            Height = NodeR * 2,
            Stroke = new SolidColorBrush(_pulses.Count % 2 == 0 ? ColCyanB : ColMagentaB),
            StrokeThickness = 2.5,
            IsHitTestVisible = false,
            Opacity = 0.6
        };
        PulsesCanvas.Children.Add(el);
        _pulses.Add(new Pulse { T = 0, Node = node, Shape = el });
    }

    private void StepPulses()
    {
        for (int i = _pulses.Count - 1; i >= 0; i--)
        {
            var p = _pulses[i];
            p.T += 0.035;
            if (p.T >= 1)
            {
                PulsesCanvas.Children.Remove(p.Shape);
                _pulses.RemoveAt(i);
                continue;
            }
            double r = NodeR + p.T * 110;
            p.Shape.Width = r * 2;
            p.Shape.Height = r * 2;
            p.Shape.Opacity = 0.6 * (1 - p.T);
            Canvas.SetLeft(p.Shape, p.Node.X - r);
            Canvas.SetTop(p.Shape, p.Node.Y - r);
        }
    }

    // ---------- речь ----------

    private void OnSpeakerStarted(string name) => DispatcherQueue.TryEnqueue(() => SetSpeaking(name, true));
    private void OnSpeakerStopped(string name) => DispatcherQueue.TryEnqueue(() => SetSpeaking(name, false));

    private void SetSpeaking(string name, bool on)
    {
        var node = _nodes.FirstOrDefault(x => x.Member.Name == name);
        if (node is null || node.Speaking == on) return;
        node.Speaking = on;
        if (on) SpawnPulse(node);
        UpdateStreamPanel();
    }

    private void Members_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            RebuildNodes();
            UpdateUsersPill();
            UpdateStreamPanel();
        });

    // ---------- active stream ----------

    private void UpdateStreamPanel()
    {
        StreamList.Children.Clear();
        _streamBars.Clear();
        var speaking = _nodes.Where(n => n.Speaking).ToList();
        NoStreamBorder.Visibility = speaking.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int idx = 0;
        foreach (var node in speaking)
        {
            var accent = idx % 2 == 0 ? ColCyanB : ColMagentaB;
            idx++;

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 13, 15, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0)
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var colorBar = new Rectangle { Fill = new SolidColorBrush(accent), RadiusX = 1.5, RadiusY = 1.5 };
            Grid.SetColumn(colorBar, 0);

            var av = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
            av.Children.Add(new Ellipse { Fill = node.Member.ColorBrush });
            av.Children.Add(new TextBlock
            {
                Text = node.Member.Letter,
                FontFamily = Cascadia,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColWhite),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(av, 1);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = node.Member.Name,
                FontFamily = Cascadia,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColWhite),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = "(Speaking)",
                FontFamily = Cascadia,
                FontSize = 9,
                Foreground = new SolidColorBrush(accent)
            });
            Grid.SetColumn(info, 2);

            var wave = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var bars = new Rectangle[20];
            for (int i = 0; i < bars.Length; i++)
            {
                bars[i] = new Rectangle
                {
                    Width = 2,
                    Height = 4,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = new SolidColorBrush(accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.85
                };
                wave.Children.Add(bars[i]);
            }
            _streamBars[node.Member.Name] = bars;
            Grid.SetColumn(wave, 3);

            grid.Children.Add(colorBar);
            grid.Children.Add(av);
            grid.Children.Add(info);
            grid.Children.Add(wave);
            border.Child = grid;
            StreamList.Children.Add(border);
        }
    }

    private void StepWaveforms()
    {
        foreach (var node in _nodes)
        {
            if (!_streamBars.TryGetValue(node.Member.Name, out var bars)) continue;
            if (!_waves.TryGetValue(node.Member.Name, out var amps))
                _waves[node.Member.Name] = amps = new double[bars.Length];
            amps[^1] = node.Speaking ? 0.2 + 0.8 * Math.Pow(_rnd.NextDouble(), 1.6) : amps[^1] * 0.7;
            for (int i = 0; i < bars.Length - 1; i++) amps[i] = amps[i + 1];
            for (int i = 0; i < bars.Length; i++)
                bars[i].Height = 3 + amps[i] * 16;
        }
    }

    // ---------- панели и кнопки ----------

    private void UpdateUsersPill() => UsersCountText.Text = $"{_nodes.Count}";

    private void UpdateMicSpkUi()
    {
        if (_voice is null) return;
        bool micMuted = _voice.MicMuted || (_webrtc?.MicMuted ?? false);
        bool spkMuted = _voice.PlaybackMuted || (_webrtc?.PlaybackMuted ?? false);
        MicText.Text = micMuted ? "MICROPHONE MUTED" : "MICROPHONE ACTIVE";
        MicText.Foreground = new SolidColorBrush(micMuted ? ColRed : ColCyanB);
        SpkText.Text = spkMuted ? "SPEAKER OFF" : "SPEAKER";
        SpkText.Foreground = new SolidColorBrush(spkMuted ? ColRed : ColMagentaText);
    }

    private void MicBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;
        bool mute = !(_voice.MicMuted || (_webrtc?.MicMuted ?? false));
        _voice.MicMuted = mute;
        if (_webrtc is not null) _webrtc.MicMuted = mute;
        UpdateMicSpkUi();
    }

    private void SpkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;
        bool mute = !(_voice.PlaybackMuted || (_webrtc?.PlaybackMuted ?? false));
        _voice.PlaybackMuted = mute;
        if (_webrtc is not null) _webrtc.PlaybackMuted = mute;
        UpdateMicSpkUi();
    }

    private void HubHomeBtn_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();

    private void GearBtn_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void EndCallBtn_Click(object sender, RoutedEventArgs e) => _onEndCall?.Invoke();

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => RegenerateStars();

    private void RegenerateStars()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w < 50 || h < 50) return;
        StarsCanvas.Children.Clear();
        int count = Math.Min(170, (int)(w * h / 8500));
        for (int i = 0; i < count; i++)
        {
            var c = _rnd.Next(100) switch
            {
                < 70 => Color.FromArgb(255, 235, 240, 255),
                < 85 => ColCyanB,
                _ => ColMagentaB
            };
            double size = 1 + _rnd.NextDouble() * 1.8;
            var s = new Ellipse
            {
                Width = size,
                Height = size,
                Opacity = 0.12 + _rnd.NextDouble() * 0.5,
                Fill = new SolidColorBrush(c)
            };
            Canvas.SetLeft(s, _rnd.NextDouble() * w);
            Canvas.SetTop(s, _rnd.NextDouble() * h);
            StarsCanvas.Children.Add(s);
        }
    }
}
