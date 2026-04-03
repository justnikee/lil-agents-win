using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LilAgents.Windows.Characters;

/// <summary>
/// Sprite-based animation engine — replaces AVQueuePlayer/AVPlayerLooper.
/// Pre-loads PNG frames into memory for zero-lag frame switching.
/// Falls back to procedurally generated placeholder frames if PNGs aren't available.
/// </summary>
public class SpriteAnimator
{
    private readonly List<BitmapSource> _frames = new();
    private int _currentFrameIndex;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _playbackClock = new();
    private readonly double _fps;
    private bool _isPlaying;
    private readonly string _characterName;
    private readonly int _targetLoopFrameCount;

    /// <summary>Current frame to display.</summary>
    public BitmapSource? CurrentFrame => _frames.Count > 0 ? _frames[_currentFrameIndex] : null;

    /// <summary>Total number of frames.</summary>
    public int FrameCount => _frames.Count;

    /// <summary>Fires when frame changes.</summary>
    public event Action<BitmapSource>? OnFrameChanged;

    public SpriteAnimator(string characterName, double fps = 24)
    {
        _characterName = characterName;
        _fps = fps;
        // Matches the original 10-second walk video timing used by movement logic.
        _targetLoopFrameCount = Math.Max(1, (int)Math.Round(10.0 * fps));
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / fps)
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// Load sprite frames from embedded resources or a directory.
    /// </summary>
    public void LoadFrames(string? spriteDirectory = null)
    {
        _frames.Clear();

        // Try to load from directory if provided
        if (spriteDirectory != null && System.IO.Directory.Exists(spriteDirectory))
        {
            var files = System.IO.Directory.GetFiles(spriteDirectory, "*.png")
                .OrderBy(f => f)
                .ToArray();

            foreach (var file in files)
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(file);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze(); // Critical for performance — makes cross-thread safe
                    _frames.Add(bitmap);
                }
                catch { /* skip bad frames */ }
            }

            // ffmpeg extraction can include a trailing frame; keep timing in sync with movement.
            if (_frames.Count > _targetLoopFrameCount)
            {
                _frames.RemoveRange(_targetLoopFrameCount, _frames.Count - _targetLoopFrameCount);
            }
        }

        // If no frames loaded, generate placeholder animation
        if (_frames.Count == 0)
        {
            GeneratePlaceholderFrames();
        }

        _currentFrameIndex = 0;
    }

    /// <summary>
    /// Generates a simple animated character as placeholder.
    /// Creates a cute walking figure with bouncing animation.
    /// </summary>
    private void GeneratePlaceholderFrames()
    {
        const int frameCount = 24;
        const int width = 64;
        const int height = 80;

        var baseColor = _characterName.ToLower() switch
        {
            "bruce" => Color.FromRgb(224, 122, 95),   // Warm orange
            "jazz" => Color.FromRgb(130, 170, 255),    // Cool blue
            _ => Color.FromRgb(200, 200, 200)
        };

        for (int i = 0; i < frameCount; i++)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double phase = (double)i / frameCount * Math.PI * 2;
                double bounce = Math.Abs(Math.Sin(phase)) * 6;
                double lean = Math.Sin(phase) * 3;

                // Body
                var bodyBrush = new SolidColorBrush(baseColor);
                bodyBrush.Freeze();
                dc.DrawEllipse(bodyBrush, null,
                    new Point(width / 2 + lean, height / 2 - bounce - 5),
                    14, 18);

                // Head
                var headColor = Color.FromRgb(
                    (byte)Math.Min(255, baseColor.R + 30),
                    (byte)Math.Min(255, baseColor.G + 30),
                    (byte)Math.Min(255, baseColor.B + 30));
                var headBrush = new SolidColorBrush(headColor);
                headBrush.Freeze();
                dc.DrawEllipse(headBrush, null,
                    new Point(width / 2 + lean, height / 2 - bounce - 28),
                    10, 10);

                // Eyes
                var eyeBrush = new SolidColorBrush(Colors.White);
                eyeBrush.Freeze();
                var pupilBrush = new SolidColorBrush(Color.FromRgb(40, 40, 40));
                pupilBrush.Freeze();

                dc.DrawEllipse(eyeBrush, null,
                    new Point(width / 2 + lean - 4, height / 2 - bounce - 30), 3, 3);
                dc.DrawEllipse(eyeBrush, null,
                    new Point(width / 2 + lean + 4, height / 2 - bounce - 30), 3, 3);
                dc.DrawEllipse(pupilBrush, null,
                    new Point(width / 2 + lean - 3, height / 2 - bounce - 30), 1.5, 1.5);
                dc.DrawEllipse(pupilBrush, null,
                    new Point(width / 2 + lean + 5, height / 2 - bounce - 30), 1.5, 1.5);

                // Legs (walking animation)
                var legPen = new Pen(bodyBrush, 3);
                legPen.Freeze();

                double legSwing = Math.Sin(phase) * 8;
                // Left leg
                dc.DrawLine(legPen,
                    new Point(width / 2 + lean - 5, height / 2 - bounce + 10),
                    new Point(width / 2 + lean - 5 - legSwing, height / 2 + 15));
                // Right leg
                dc.DrawLine(legPen,
                    new Point(width / 2 + lean + 5, height / 2 - bounce + 10),
                    new Point(width / 2 + lean + 5 + legSwing, height / 2 + 15));

                // Feet (little circles)
                var footBrush = new SolidColorBrush(Color.FromRgb(
                    (byte)Math.Max(0, baseColor.R - 40),
                    (byte)Math.Max(0, baseColor.G - 40),
                    (byte)Math.Max(0, baseColor.B - 40)));
                footBrush.Freeze();
                dc.DrawEllipse(footBrush, null,
                    new Point(width / 2 + lean - 5 - legSwing, height / 2 + 17), 3, 2);
                dc.DrawEllipse(footBrush, null,
                    new Point(width / 2 + lean + 5 + legSwing, height / 2 + 17), 3, 2);
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            _frames.Add(rtb);
        }
    }

    public void Play()
    {
        if (_frames.Count == 0) return;
        if (_isPlaying) return;
        _isPlaying = true;
        _playbackClock.Restart();
        _timer.Start();
    }

    public void Pause()
    {
        _isPlaying = false;
        _playbackClock.Stop();
        _timer.Stop();
    }

    public void Stop()
    {
        _isPlaying = false;
        _playbackClock.Reset();
        _timer.Stop();
        _currentFrameIndex = 0;
        if (_frames.Count > 0)
            OnFrameChanged?.Invoke(_frames[0]);
    }

    public void SeekToStart()
    {
        _playbackClock.Reset();
        _currentFrameIndex = 0;
        if (_frames.Count > 0)
            OnFrameChanged?.Invoke(_frames[0]);
    }

    public bool IsPlaying => _isPlaying;

    /// <summary>Current time in seconds.</summary>
    public double CurrentTime => _frames.Count > 0
        ? _currentFrameIndex * _timer.Interval.TotalSeconds
        : 0;

    /// <summary>Total duration in seconds.</summary>
    public double Duration => _frames.Count * _timer.Interval.TotalSeconds;

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_isPlaying || _frames.Count == 0) return;

        var elapsedFrames = (int)Math.Floor(_playbackClock.Elapsed.TotalSeconds * _fps);
        var nextFrameIndex = elapsedFrames % _frames.Count;
        if (nextFrameIndex == _currentFrameIndex)
        {
            return;
        }

        _currentFrameIndex = nextFrameIndex;
        OnFrameChanged?.Invoke(_frames[nextFrameIndex]);
    }
}
