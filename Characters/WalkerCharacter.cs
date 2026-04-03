using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Media;
using LilAgents.Windows.Core;
using LilAgents.Windows.Sessions;
using LilAgents.Windows.Themes;
using LilAgents.Windows.UI;

namespace LilAgents.Windows.Characters;

public enum CharacterSize
{
    Large,
    Medium,
    Small
}

public static class CharacterSizeExtensions
{
    public static double Height(this CharacterSize size) => size switch
    {
        CharacterSize.Large => 200,
        CharacterSize.Medium => 150,
        CharacterSize.Small => 100,
        _ => 200,
    };

    public static string DisplayName(this CharacterSize size) => size switch
    {
        CharacterSize.Large => "Large",
        CharacterSize.Medium => "Medium",
        CharacterSize.Small => "Small",
        _ => "Large",
    };
}

/// <summary>
/// Character controller with movement/popover/bubble/session logic.
/// Ported from WalkerCharacter.swift.
/// </summary>
public class WalkerCharacter : IDisposable
{
    private const double VideoWidth = 1080;
    private const double VideoHeight = 1920;
    private const double VideoDuration = 10.0;

    private static readonly Random Random = new();
    private static readonly string[] ThinkingPhrases =
    [
        "hmm...", "thinking...", "one sec...", "ok hold on",
        "let me check", "working on it", "almost...", "bear with me",
        "on it!", "gimme a sec", "brb", "processing...",
        "hang tight", "just a moment", "figuring it out",
        "crunching...", "reading...", "looking...",
        "cooking...", "vibing...", "digging in",
        "connecting dots", "give me a sec", "don't rush me",
        "calculating...", "assembling..."
    ];
    private static readonly string[] CompletionPhrases =
    [
        "done!", "all set!", "ready!", "here you go", "got it!",
        "finished!", "ta-da!", "voila!", "boom!", "there ya go!", "check it out!"
    ];
    private static readonly (string Name, string Ext)[] CompletionSounds =
    [
        ("ping-aa", "mp3"), ("ping-bb", "mp3"), ("ping-cc", "mp3"),
        ("ping-dd", "mp3"), ("ping-ee", "mp3"), ("ping-ff", "mp3"),
        ("ping-gg", "mp3"), ("ping-hh", "mp3"), ("ping-jj", "m4a")
    ];
    private static int _lastSoundIndex = -1;

    private readonly string _videoName;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly MediaPlayer _soundPlayer = new();

    private CharacterWindow? _window;
    private SpriteAnimator? _spriteAnimator;
    private PopoverWindow? _popoverWindow;
    private BubbleWindow? _bubbleWindow;
    private TerminalControl? _terminalView;
    private IAgentSession? _session;

    // Walk state
    private double _walkStartTime;
    private double _positionProgress;
    private bool _isWalking;
    private bool _isPaused = true;
    private double _pauseEndTime;
    private bool _goingRight = true;
    private double _walkStartPos;
    private double _walkEndPos;
    private double _currentTravelDistance = 500;
    private double _walkStartPixel;
    private double _walkEndPixel;

    // Popover state
    private bool _isIdleForPopover;
    private string _currentStreamingText = string.Empty;
    private bool _isSessionBusy;
    private bool _showingCompletion;
    private string _currentPhrase = string.Empty;
    private double _completionBubbleExpiry;
    private double _lastPhraseUpdate;

    // Visibility state
    private bool _isManuallyVisible = true;
    private double? _environmentHiddenAt;
    private bool _wasPopoverVisibleBeforeEnvironmentHide;
    private bool _wasBubbleVisibleBeforeEnvironmentHide;

    private AgentProvider _provider;
    private CharacterSize _size = CharacterSize.Large;
    private readonly List<AgentMessage> _history = [];

    public string Name { get; }
    public LilAgentsController? Controller { get; set; }
    public bool IsOnboarding { get; set; }
    public bool IsWalking => _isWalking;
    public bool IsPaused => _isPaused;
    public bool IsIdleForPopover => _isIdleForPopover;
    public bool IsVisible => _window?.IsVisible == true;
    public bool IsManuallyVisible => _isManuallyVisible;
    public double PositionProgress
    {
        get => _positionProgress;
        set => _positionProgress = Math.Clamp(value, 0, 1);
    }

    public AgentProvider Provider => _provider;
    public CharacterSize Size => _size;
    public Color CharacterColor { get; set; } = Colors.Gray;

    // Walk tuning
    public double AccelStart { get; set; } = 3.0;
    public double FullSpeedStart { get; set; } = 3.75;
    public double DecelStart { get; set; } = 7.5;
    public double WalkStop { get; set; } = 8.25;
    public double WalkAmountMin { get; set; } = 0.25;
    public double WalkAmountMax { get; set; } = 0.5;
    public double YOffset { get; set; }
    public double FlipXOffset { get; set; }

    public static bool SoundsEnabled { get; set; } = true;

    private double DisplayHeight => _size.Height();
    private double DisplayWidth => Math.Round(DisplayHeight * (VideoWidth / VideoHeight));
    private double CurrentTime => _clock.Elapsed.TotalSeconds;
    private double CurrentFlipCompensation => _goingRight ? 0 : FlipXOffset;
    private PopoverTheme ResolvedTheme => PopoverTheme.Current.WithCharacterColor(CharacterColor).WithCustomFont();

    public WalkerCharacter(string videoName, string name, AgentProvider defaultProvider)
    {
        _videoName = videoName;
        Name = name;
        _provider = defaultProvider;
    }

    public void Setup()
    {
        _window = new CharacterWindow();
        _window.SetSize(DisplayWidth, DisplayHeight);
        _window.OnCharacterClicked += HandleClick;
        _window.Show();

        _spriteAnimator = new SpriteAnimator(Name, 24);
        var spriteDirectory = ResolveBundledSpriteDirectory();
        _spriteAnimator.LoadFrames(spriteDirectory);
        _spriteAnimator.OnFrameChanged += frame => _window?.SetFrame(frame);
        if (_spriteAnimator.CurrentFrame != null)
        {
            _window.SetFrame(_spriteAnimator.CurrentFrame);
        }

        _spriteAnimator.Pause();
        _spriteAnimator.SeekToStart();
    }

    public void SetProvider(AgentProvider provider)
    {
        if (provider == _provider)
        {
            return;
        }

        _provider = provider;
        _terminalView?.SetProvider(provider);
        _popoverWindow?.SetProvider(provider);

        _session?.Stop();
        _session?.Dispose();
        _session = null;
        _history.Clear();
        _currentStreamingText = string.Empty;
        _isSessionBusy = false;
        _showingCompletion = false;
        _completionBubbleExpiry = 0;
        _currentPhrase = string.Empty;
    }

    public void SetSize(CharacterSize size)
    {
        if (_size == size)
        {
            return;
        }

        _size = size;
        _window?.SetSize(DisplayWidth, DisplayHeight);
        UpdateFlip();
    }

    public void SetManuallyVisible(bool visible)
    {
        _isManuallyVisible = visible;
        if (visible)
        {
            if (_environmentHiddenAt == null)
            {
                _window?.Show();
            }
        }
        else
        {
            _spriteAnimator?.Pause();
            _window?.Hide();
            _popoverWindow?.Hide();
            _bubbleWindow?.Hide();
        }
    }

    public void Show() => SetManuallyVisible(true);

    public void Hide() => SetManuallyVisible(false);

    public void SetPauseDelay(double seconds)
    {
        _isPaused = true;
        _pauseEndTime = CurrentTime + Math.Max(seconds, 0);
    }

    public void DelayPauseIfReady(double minDelay, double maxDelay)
    {
        if (_isPaused && CurrentTime >= _pauseEndTime)
        {
            _pauseEndTime = CurrentTime + RandomRange(minDelay, maxDelay);
        }
    }

    public void RaiseWindow()
    {
        if (_isIdleForPopover && _popoverWindow?.IsVisible == true)
        {
            return;
        }

        _window?.RaiseToTop();
    }

    public void RaiseOverlayWindows()
    {
        if (_isIdleForPopover && _popoverWindow?.IsVisible == true)
        {
            _popoverWindow.RaiseToTop();
        }

        if (_bubbleWindow?.IsVisible == true)
        {
            _bubbleWindow.RaiseToTop();
        }
    }

    public void HideForEnvironment()
    {
        if (_environmentHiddenAt != null)
        {
            return;
        }

        _environmentHiddenAt = CurrentTime;
        _wasPopoverVisibleBeforeEnvironmentHide = _popoverWindow?.IsVisible == true;
        _wasBubbleVisibleBeforeEnvironmentHide = _bubbleWindow?.IsVisible == true;

        _spriteAnimator?.Pause();
        _window?.Hide();
        _popoverWindow?.Hide();
        _bubbleWindow?.Hide();
    }

    public void ShowForEnvironmentIfNeeded()
    {
        if (_environmentHiddenAt == null)
        {
            return;
        }

        var hiddenDuration = CurrentTime - _environmentHiddenAt.Value;
        _environmentHiddenAt = null;
        _walkStartTime += hiddenDuration;
        _pauseEndTime += hiddenDuration;
        _completionBubbleExpiry += hiddenDuration;
        _lastPhraseUpdate += hiddenDuration;

        if (!_isManuallyVisible)
        {
            return;
        }

        _window?.Show();
        if (_isWalking)
        {
            _spriteAnimator?.Play();
        }

        if (_isIdleForPopover && _wasPopoverVisibleBeforeEnvironmentHide)
        {
            UpdatePopoverPosition();
            _popoverWindow?.Show();
            _terminalView?.FocusInput();
        }

        if (_wasBubbleVisibleBeforeEnvironmentHide)
        {
            UpdateThinkingBubble();
        }
    }

    private void HandleClick()
    {
        if (IsOnboarding)
        {
            OpenOnboardingPopover();
            return;
        }

        if (_isIdleForPopover)
        {
            ClosePopover();
        }
        else
        {
            OpenPopover();
        }
    }

    private void OpenOnboardingPopover()
    {
        _showingCompletion = false;
        HideBubble();

        _isIdleForPopover = true;
        _isWalking = false;
        _isPaused = true;
        _spriteAnimator?.Pause();
        _spriteAnimator?.SeekToStart();

        EnsurePopoverWindow();
        if (_terminalView == null)
        {
            return;
        }

        _terminalView.SetInputEnabled(false);
        _terminalView.Clear();
        _terminalView.AppendStreamingText("""
            hey! we're bruce and jazz - your lil dock agents.

            click either of us to open a CLI chat. we'll walk around while you work and let you know when the agent is thinking.

            check the tray icon for themes, sounds, and more options.

            click anywhere outside to dismiss, then click us again to start chatting.
            """);
        _terminalView.EndStreaming();

        UpdatePopoverPosition();
        _popoverWindow?.ShowWithAnimation();
        _popoverWindow?.RaiseToTop();
    }

    private void CloseOnboarding()
    {
        _popoverWindow?.Hide();
        _popoverWindow?.Close();
        _popoverWindow = null;
        _terminalView = null;

        _isIdleForPopover = false;
        IsOnboarding = false;
        _isPaused = true;
        _pauseEndTime = CurrentTime + RandomRange(1.0, 3.0);
        _spriteAnimator?.SeekToStart();
        Controller?.CompleteOnboarding();
    }

    public void OpenPopover()
    {
        Controller?.CloseOtherPopovers(this);

        _isIdleForPopover = true;
        _isWalking = false;
        _isPaused = true;
        _spriteAnimator?.Pause();
        _spriteAnimator?.SeekToStart();

        _showingCompletion = false;
        HideBubble();

        EnsureSession();
        EnsurePopoverWindow();

        _terminalView?.SetInputEnabled(true);
        _terminalView?.SetProvider(_provider);
        if (_history.Count > 0)
        {
            _terminalView?.ReplayHistory(_history);
        }
        else
        {
            _terminalView?.ShowSessionMessage();
        }

        UpdatePopoverPosition();
        _popoverWindow?.ShowWithAnimation();
        _popoverWindow?.RaiseToTop();
    }

    public void ClosePopover()
    {
        if (!_isIdleForPopover)
        {
            return;
        }

        if (IsOnboarding)
        {
            CloseOnboarding();
            return;
        }

        _popoverWindow?.HideWithAnimation();
        _isIdleForPopover = false;

        if (_showingCompletion)
        {
            _completionBubbleExpiry = CurrentTime + 3.0;
            ShowBubble(_currentPhrase, true);
        }
        else if (_isSessionBusy)
        {
            _currentPhrase = string.Empty;
            _lastPhraseUpdate = 0;
            UpdateThinkingPhrase();
            ShowBubble(_currentPhrase, false);
        }

        _pauseEndTime = CurrentTime + RandomRange(2.0, 5.0);
    }

    private void EnsurePopoverWindow()
    {
        if (_popoverWindow != null)
        {
            return;
        }

        _popoverWindow = new PopoverWindow(_provider);
        _popoverWindow.SetCharacterColor(CharacterColor);
        _popoverWindow.OnMessageSubmitted += HandleUserMessage;
        _popoverWindow.OnRefreshSession += ResetSession;
        _popoverWindow.OnProviderChanged += provider => SetProvider(provider);
        _popoverWindow.OnRequestClose += () =>
        {
            if (IsOnboarding)
            {
                CloseOnboarding();
            }
            else
            {
                ClosePopover();
            }
        };
        _terminalView = _popoverWindow.TerminalView;
    }

    private void EnsureSession()
    {
        if (_session != null && _session.IsRunning)
        {
            return;
        }

        _session?.Dispose();
        _session = _provider.CreateSession(ShellEnvironment.GetWorkingDirectory());
        WireSession(_session);
        _ = _session.StartAsync();
    }

    private void WireSession(IAgentSession session)
    {
        var firstToken = true;
        session.OnText += text =>
        {
            if (firstToken)
            {
                firstToken = false;
                _terminalView?.HideThinkingIndicator();
            }
            _currentStreamingText += text;
            _terminalView?.AppendStreamingText(text);
        };

        session.OnTurnComplete += () =>
        {
            firstToken = true;
            _terminalView?.HideThinkingIndicator();
            _terminalView?.EndStreaming();
            _isSessionBusy = false;
            PlayCompletionSound();
            ShowCompletionBubble();
        };

        session.OnError += text =>
        {
            _terminalView?.AppendError(text);
            _history.Add(new AgentMessage(AgentMessageRole.Error, text));
        };

        session.OnToolUse += (toolName, input) =>
        {
            _isSessionBusy = true;
            _terminalView?.AppendToolCall(toolName, input);
            _history.Add(new AgentMessage(AgentMessageRole.ToolUse, $"{toolName}: {input}"));
        };

        session.OnToolResult += (summary, success) =>
        {
            _terminalView?.AppendToolResult(summary, success);
            _history.Add(new AgentMessage(
                AgentMessageRole.ToolResult,
                success ? summary : $"ERROR: {summary}"
            ));
        };

        session.OnProcessExit += _ =>
        {
            _isSessionBusy = false;
            _terminalView?.EndStreaming();
            _terminalView?.AppendError($"{_provider.DisplayName()} session ended.");
        };

        session.OnSessionReady += () => { };
    }

    private async void HandleUserMessage(string message)
    {
        EnsureSession();
        if (_session == null)
        {
            return;
        }

        _history.Add(new AgentMessage(AgentMessageRole.User, message));
        _isSessionBusy = true;
        _showingCompletion = false;
        _currentStreamingText = string.Empty;

        // Immediately show thinking state – don't wait for the game tick or CLI response
        _currentPhrase = string.Empty;
        _lastPhraseUpdate = 0;
        UpdateThinkingPhrase();
        ShowBubble(_currentPhrase, false);
        _terminalView?.ShowThinkingIndicator();

        try
        {
            await _session.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            _terminalView?.AppendError($"Failed to send: {ex.Message}");
            _terminalView?.HideThinkingIndicator();
            _isSessionBusy = false;
        }
    }

    private void ResetSession()
    {
        if (IsOnboarding)
        {
            return;
        }

        _session?.Stop();
        _session?.Dispose();
        _session = null;

        _currentStreamingText = string.Empty;
        _history.Clear();
        _showingCompletion = false;
        _currentPhrase = string.Empty;
        _completionBubbleExpiry = 0;
        _isSessionBusy = false;
        HideBubble();

        _terminalView?.ResetState();
        _terminalView?.ShowSessionMessage();
        EnsureSession();
    }

    private void UpdatePopoverPosition()
    {
        if (_popoverWindow == null || !_isIdleForPopover || _window == null)
        {
            return;
        }

        var centerX = _window.Left + (_window.Width / 2.0);
        var topY = _window.Top;
        _popoverWindow.PositionAboveCharacter(centerX, topY);
    }

    private void UpdateThinkingBubble()
    {
        var now = CurrentTime;

        if (_showingCompletion)
        {
            if (now >= _completionBubbleExpiry)
            {
                _showingCompletion = false;
                HideBubble();
                return;
            }

            if (_isIdleForPopover)
            {
                _completionBubbleExpiry += 1.0 / 60.0;
                HideBubble();
            }
            else
            {
                ShowBubble(_currentPhrase, true);
            }
            return;
        }

        if (_isSessionBusy)
        {
            var oldPhrase = _currentPhrase;
            UpdateThinkingPhrase();
            if (_currentPhrase != oldPhrase || string.IsNullOrEmpty(oldPhrase))
            {
                ShowBubble(_currentPhrase, false);
            }
        }
        else
        {
            HideBubble();
        }
    }

    private void HideBubble()
    {
        _bubbleWindow?.FadeOut();
    }

    private void ShowBubble(string text, bool isCompletion)
    {
        if (_window == null)
        {
            return;
        }

        if (_bubbleWindow == null)
        {
            _bubbleWindow = new BubbleWindow();
            _bubbleWindow.SetCharacterColor(CharacterColor);
        }

        _bubbleWindow.ShowText(text, isCompletion);
        _bubbleWindow.PositionAboveFrame(_window.Left, _window.Top, _window.Width, _window.Height);
        _bubbleWindow.RaiseToTop();
    }

    private void UpdateThinkingPhrase()
    {
        var now = CurrentTime;
        if (string.IsNullOrEmpty(_currentPhrase) || now - _lastPhraseUpdate > RandomRange(3.0, 5.0))
        {
            var next = ThinkingPhrases[Random.Next(ThinkingPhrases.Length)];
            while (next == _currentPhrase && ThinkingPhrases.Length > 1)
            {
                next = ThinkingPhrases[Random.Next(ThinkingPhrases.Length)];
            }
            _currentPhrase = next;
            _lastPhraseUpdate = now;
        }
    }

    public void ShowCompletionBubble(string? phrase = null, double durationSeconds = 3.0)
    {
        _currentPhrase = phrase ?? CompletionPhrases[Random.Next(CompletionPhrases.Length)];
        _showingCompletion = true;
        _completionBubbleExpiry = CurrentTime + durationSeconds;
        _lastPhraseUpdate = 0;
        if (!_isIdleForPopover)
        {
            ShowBubble(_currentPhrase, true);
        }
    }

    public void PlayCompletionSound()
    {
        if (!SoundsEnabled)
        {
            return;
        }

        int index;
        do
        {
            index = Random.Next(CompletionSounds.Length);
        } while (index == _lastSoundIndex && CompletionSounds.Length > 1);
        _lastSoundIndex = index;

        var sound = CompletionSounds[index];
        var path = ResolveBundledSound(sound.Name, sound.Ext);
        if (path == null)
        {
            SystemSounds.Asterisk.Play();
            return;
        }

        try
        {
            _soundPlayer.Open(new Uri(path));
            _soundPlayer.Volume = 1.0;
            _soundPlayer.Play();
        }
        catch
        {
            SystemSounds.Asterisk.Play();
        }
    }

    public void StartWalk()
    {
        _isPaused = false;
        _isWalking = true;
        _walkStartTime = CurrentTime;

        if (_positionProgress > 0.85)
        {
            _goingRight = false;
        }
        else if (_positionProgress < 0.15)
        {
            _goingRight = true;
        }
        else
        {
            _goingRight = Random.NextDouble() > 0.5;
        }

        _walkStartPos = _positionProgress;
        var walkPixels = RandomRange(WalkAmountMin, WalkAmountMax) * 500.0;
        var walkAmount = _currentTravelDistance > 0 ? walkPixels / _currentTravelDistance : 0.3;
        _walkEndPos = _goingRight
            ? Math.Min(_walkStartPos + walkAmount, 1.0)
            : Math.Max(_walkStartPos - walkAmount, 0.0);

        _walkStartPixel = _walkStartPos * _currentTravelDistance;
        _walkEndPixel = _walkEndPos * _currentTravelDistance;

        const double minSeparation = 0.12;
        if (Controller != null)
        {
            foreach (var sibling in Controller.Characters)
            {
                if (ReferenceEquals(sibling, this))
                {
                    continue;
                }

                var siblingPos = sibling.PositionProgress;
                if (Math.Abs(_walkEndPos - siblingPos) < minSeparation)
                {
                    _walkEndPos = _goingRight
                        ? Math.Max(_walkStartPos, siblingPos - minSeparation)
                        : Math.Min(_walkStartPos, siblingPos + minSeparation);
                }
            }
        }

        UpdateFlip();
        _spriteAnimator?.SeekToStart();
        _spriteAnimator?.Play();
    }

    public void EnterPause()
    {
        _isWalking = false;
        _isPaused = true;
        _spriteAnimator?.Pause();
        _spriteAnimator?.SeekToStart();
        _pauseEndTime = CurrentTime + RandomRange(5.0, 12.0);
    }

    private void UpdateFlip()
    {
        if (_window != null)
        {
            _window.IsFacingLeft = !_goingRight;
        }
    }

    private double MovementPosition(double videoTime)
    {
        var dIn = FullSpeedStart - AccelStart;
        var dLinear = DecelStart - FullSpeedStart;
        var dOut = WalkStop - DecelStart;
        var velocity = 1.0 / (dIn / 2.0 + dLinear + dOut / 2.0);

        if (videoTime <= AccelStart)
        {
            return 0.0;
        }
        if (videoTime <= FullSpeedStart)
        {
            var t = videoTime - AccelStart;
            return velocity * t * t / (2.0 * dIn);
        }
        if (videoTime <= DecelStart)
        {
            var easeInDistance = velocity * dIn / 2.0;
            var t = videoTime - FullSpeedStart;
            return easeInDistance + velocity * t;
        }
        if (videoTime <= WalkStop)
        {
            var easeInDistance = velocity * dIn / 2.0;
            var linearDistance = velocity * dLinear;
            var t = videoTime - DecelStart;
            return easeInDistance + linearDistance + velocity * (t - t * t / (2.0 * dOut));
        }

        return 1.0;
    }

    public void Update(double dockX, double dockWidth, double dockTopY)
    {
        if (_window == null)
        {
            return;
        }

        _currentTravelDistance = Math.Max(dockWidth - DisplayWidth, 0);

        if (_isIdleForPopover)
        {
            PlaceCharacter(dockX, dockTopY, _currentTravelDistance);
            UpdatePopoverPosition();
            UpdateThinkingBubble();
            return;
        }

        var now = CurrentTime;
        if (_isPaused)
        {
            if (now >= _pauseEndTime)
            {
                StartWalk();
            }
            else
            {
                PlaceCharacter(dockX, dockTopY, _currentTravelDistance);
                return;
            }
        }

        if (_isWalking)
        {
            var elapsed = now - _walkStartTime;
            var videoTime = Math.Min(elapsed, VideoDuration);

            var walkNorm = elapsed >= VideoDuration ? 1.0 : MovementPosition(videoTime);
            var currentPixel = _walkStartPixel + (_walkEndPixel - _walkStartPixel) * walkNorm;
            if (_currentTravelDistance > 0)
            {
                _positionProgress = Math.Clamp(currentPixel / _currentTravelDistance, 0.0, 1.0);
            }

            if (elapsed >= VideoDuration)
            {
                _walkEndPos = _positionProgress;
                EnterPause();
                return;
            }

            PlaceCharacter(dockX, dockTopY, _currentTravelDistance);
        }

        UpdateThinkingBubble();
    }

    private void PlaceCharacter(double dockX, double dockTopY, double travelDistance)
    {
        if (_window == null)
        {
            return;
        }

        var x = dockX + (travelDistance * _positionProgress) + CurrentFlipCompensation;
        var bottomPadding = DisplayHeight * 0.15;
        var y = dockTopY - (DisplayHeight - bottomPadding) + YOffset;
        _window.SetFrameOrigin(x, y);

        if (_bubbleWindow?.IsVisible == true)
        {
            _bubbleWindow.PositionAboveFrame(_window.Left, _window.Top, _window.Width, _window.Height);
        }
    }

    private static double RandomRange(double min, double max) => min + (Random.NextDouble() * (max - min));

    private string? ResolveBundledSpriteDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "Sprites", _videoName),
            Path.Combine(AppContext.BaseDirectory, "Resources", "Sprites", Name.ToLowerInvariant()),
            Path.Combine(AppContext.BaseDirectory, "Sprites", _videoName),
            Path.Combine(AppContext.BaseDirectory, "Sprites", Name.ToLowerInvariant()),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "LilAgents.Windows", "Resources", "Sprites", _videoName)),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "LilAgents.Windows", "Resources", "Sprites", Name.ToLowerInvariant())),
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? ResolveBundledSound(string name, string ext)
    {
        var fileName = $"{name}.{ext}";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "Sounds", fileName),
            Path.Combine(AppContext.BaseDirectory, "Sounds", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "LilAgents.Windows", "Resources", "Sounds", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LilAgents", "Sounds", fileName)),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        _session?.Stop();
        _session?.Dispose();
        _session = null;

        _spriteAnimator?.Stop();
        _spriteAnimator = null;

        if (_popoverWindow != null)
        {
            _popoverWindow.Close();
            _popoverWindow = null;
        }

        if (_bubbleWindow != null)
        {
            _bubbleWindow.Close();
            _bubbleWindow = null;
        }

        if (_window != null)
        {
            _window.Close();
            _window = null;
        }

        _soundPlayer.Close();
    }
}
