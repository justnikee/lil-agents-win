using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using LilAgents.Windows.Characters;
using LilAgents.Windows.Core;
using LilAgents.Windows.Sessions;
using LilAgents.Windows.Themes;
using LilAgents.Windows.UI;

namespace LilAgents.Windows;

/// <summary>
/// Main controller coordinating character movement, visibility, and tray actions.
/// </summary>
public class LilAgentsController : IDisposable
{
    private readonly List<WalkerCharacter> _characters = [];
    private readonly TrayIconManager _trayManager;
    private readonly AppSettings _settings;
    private IReadOnlyList<TaskbarDetector.DisplayInfo> _displays = [];
    private int _pinnedDisplayIndex;
    private DateTime _lastDisplayRefreshUtc = DateTime.MinValue;
    private bool _isRunning;
    private bool _isHiddenForEnvironment;

    public IReadOnlyList<WalkerCharacter> Characters => _characters;

    public LilAgentsController()
    {
        _trayManager = new TrayIconManager();
        _settings = AppSettings.Load();
        _pinnedDisplayIndex = _settings.PinnedDisplayIndex;
    }

    public void Initialize()
    {
        var availableProviders = AgentProviderExtensions.DetectAvailableProviders();
        var defaultProvider = availableProviders.Count > 0
            ? availableProviders[0]
            : AgentProvider.Claude;

        var bruceProvider = _settings.GetProvider("Bruce", defaultProvider);
        var jazzProvider = _settings.GetProvider("Jazz", defaultProvider);

        var bruce = new WalkerCharacter("walk-bruce-01", "Bruce", bruceProvider)
        {
            Controller = this,
            AccelStart = 3.0,
            FullSpeedStart = 3.75,
            DecelStart = 8.0,
            WalkStop = 8.5,
            WalkAmountMin = 0.4,
            WalkAmountMax = 0.65,
            YOffset = -3,
            CharacterColor = Color.FromRgb(102, 184, 140),
            FlipXOffset = 0,
            PositionProgress = 0.3
        };

        var jazz = new WalkerCharacter("walk-jazz-01", "Jazz", jazzProvider)
        {
            Controller = this,
            AccelStart = 3.9,
            FullSpeedStart = 4.5,
            DecelStart = 8.0,
            WalkStop = 8.75,
            WalkAmountMin = 0.35,
            WalkAmountMax = 0.6,
            YOffset = -7,
            CharacterColor = Color.FromRgb(255, 102, 0),
            FlipXOffset = -9,
            PositionProgress = 0.7
        };

        bruce.SetSize(_settings.GetSize("Bruce", CharacterSize.Large));
        jazz.SetSize(_settings.GetSize("Jazz", CharacterSize.Large));

        bruce.Setup();
        jazz.Setup();

        bruce.SetPauseDelay(Random.Shared.NextDouble() * 1.5 + 0.5);
        jazz.SetPauseDelay(Random.Shared.NextDouble() * 6.0 + 8.0);

        _characters.Add(bruce);
        _characters.Add(jazz);

        PopoverTheme.Current = PopoverTheme.FromName(_settings.SelectedTheme);
        WalkerCharacter.SoundsEnabled = _settings.SoundsEnabled;

        foreach (var character in _characters)
        {
            var visible = _settings.GetCharacterVisible(character.Name, true);
            character.SetManuallyVisible(visible);
        }

        RefreshDisplays(force: true);
        ConfigureTray();
        StartRenderLoop();

        if (!_settings.HasCompletedOnboarding && _characters.Count > 0)
        {
            TriggerOnboarding();
        }
    }

    private void ConfigureTray()
    {
        _trayManager.Initialize(_characters, _settings.SoundsEnabled, _displays, _pinnedDisplayIndex);
        _trayManager.OnQuitClicked += () => Application.Current.Shutdown();
        _trayManager.OnThemeChanged += theme =>
        {
            PopoverTheme.Current = theme;
            _settings.SelectedTheme = theme.Name;
            _settings.Save();
        };
        _trayManager.OnSoundToggled += enabled =>
        {
            WalkerCharacter.SoundsEnabled = enabled;
            _settings.SoundsEnabled = enabled;
            _settings.Save();
        };
        _trayManager.OnCharacterVisibilityChanged += (characterName, visible) =>
        {
            var character = FindCharacter(characterName);
            if (character == null) return;
            character.SetManuallyVisible(visible);
            _settings.SetCharacterVisible(characterName, visible);
            _settings.Save();
        };
        _trayManager.OnCharacterProviderChanged += (characterName, provider) =>
        {
            var character = FindCharacter(characterName);
            if (character == null) return;
            character.SetProvider(provider);
            _settings.SetProvider(characterName, provider);
            _settings.Save();
        };
        _trayManager.OnCharacterSizeChanged += (characterName, size) =>
        {
            var character = FindCharacter(characterName);
            if (character == null) return;
            character.SetSize(size);
            _settings.SetSize(characterName, size);
            _settings.Save();
        };
        _trayManager.OnPinnedDisplayChanged += displayIndex =>
        {
            _pinnedDisplayIndex = displayIndex;
            _settings.PinnedDisplayIndex = displayIndex;
            _settings.Save();
        };
        _trayManager.OnCheckForUpdatesClicked += OpenUpdatesPage;
        _trayManager.OnAboutClicked += ShowAbout;
    }

    private WalkerCharacter? FindCharacter(string name)
    {
        return _characters.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private void StartRenderLoop()
    {
        _isRunning = true;
        CompositionTarget.Rendering += OnRenderFrame;
    }

    private void StopRenderLoop()
    {
        _isRunning = false;
        CompositionTarget.Rendering -= OnRenderFrame;
    }

    private void TriggerOnboarding()
    {
        var bruce = _characters[0];
        bruce.IsOnboarding = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            bruce.ShowCompletionBubble("hi!", 600);
            bruce.PlayCompletionSound();
        };
        timer.Start();
    }

    public void CompleteOnboarding()
    {
        _settings.HasCompletedOnboarding = true;
        _settings.Save();
        foreach (var character in _characters)
        {
            character.IsOnboarding = false;
        }
    }

    public void CloseOtherPopovers(WalkerCharacter source)
    {
        foreach (var character in _characters)
        {
            if (!ReferenceEquals(character, source) && character.IsIdleForPopover)
            {
                character.ClosePopover();
            }
        }
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }

        RefreshDisplays();

        var pinnedDisplay = _pinnedDisplayIndex >= 0
            ? _displays.FirstOrDefault(d => d.Index == _pinnedDisplayIndex)
            : null;
        var usePinnedDisplay = pinnedDisplay != null;

        TaskbarDetector.TaskbarInfo? taskbar = null;
        if (!usePinnedDisplay)
        {
            try
            {
                taskbar = TaskbarDetector.GetTaskbarInfo();
            }
            catch
            {
                return;
            }
        }

        var shouldShow = usePinnedDisplay || TaskbarDetector.ShouldShowCharacters();
        if (shouldShow != !_isHiddenForEnvironment)
        {
            _isHiddenForEnvironment = !shouldShow;
            if (shouldShow)
            {
                foreach (var character in _characters)
                {
                    character.ShowForEnvironmentIfNeeded();
                }
            }
            else
            {
                foreach (var character in _characters)
                {
                    character.HideForEnvironment();
                }
            }
        }

        if (!shouldShow)
        {
            return;
        }

        double dockX;
        double dockWidth;
        double dockTopY;

        if (usePinnedDisplay)
        {
            var bounds = pinnedDisplay!.Bounds;
            dockX = bounds.Left;
            dockWidth = bounds.Width;
            dockTopY = bounds.Bottom;
        }
        else
        {
            dockX = taskbar!.WalkAreaLeft;
            dockWidth = taskbar.WalkAreaRight - taskbar.WalkAreaLeft;
            dockTopY = taskbar.CharacterWalkY;
        }

        var activeCharacters = _characters
            .Where(c => c.IsVisible && c.IsManuallyVisible)
            .ToList();

        var anyWalking = activeCharacters.Any(c => c.IsWalking);
        if (anyWalking)
        {
            foreach (var character in activeCharacters)
            {
                character.DelayPauseIfReady(5.0, 10.0);
            }
        }

        foreach (var character in activeCharacters)
        {
            character.Update(dockX, dockWidth, dockTopY);
        }

        foreach (var character in activeCharacters.OrderBy(c => c.PositionProgress))
        {
            character.RaiseWindow();
        }

        foreach (var character in activeCharacters)
        {
            character.RaiseOverlayWindows();
        }
    }

    private void RefreshDisplays(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastDisplayRefreshUtc).TotalSeconds < 2.0)
        {
            return;
        }

        _lastDisplayRefreshUtc = now;

        IReadOnlyList<TaskbarDetector.DisplayInfo> latestDisplays;
        try
        {
            latestDisplays = TaskbarDetector.GetDisplays();
        }
        catch
        {
            return;
        }

        var changed = !AreDisplaysEquivalent(_displays, latestDisplays);
        _displays = latestDisplays;

        var validPinned = _pinnedDisplayIndex >= 0 && _displays.Any(d => d.Index == _pinnedDisplayIndex);
        if (!validPinned && _pinnedDisplayIndex >= 0)
        {
            _pinnedDisplayIndex = -1;
            _settings.PinnedDisplayIndex = -1;
            _settings.Save();
            changed = true;
        }

        if (changed)
        {
            _trayManager.UpdateDisplays(_displays, _pinnedDisplayIndex);
        }
    }

    private static bool AreDisplaysEquivalent(
        IReadOnlyList<TaskbarDetector.DisplayInfo> left,
        IReadOnlyList<TaskbarDetector.DisplayInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.Index != b.Index || a.Name != b.Name || a.IsPrimary != b.IsPrimary || a.Bounds != b.Bounds)
            {
                return false;
            }
        }

        return true;
    }

    private static void ShowAbout()
    {
        MessageBox.Show(
            "Lil Agents for Windows\n\n" +
            "Animated AI assistant characters that live above your taskbar.\n\n" +
            "Click Bruce or Jazz to chat with your CLI agent.\n" +
            "Use the tray menu for themes, providers, size, and sound.",
            "About Lil Agents",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }

    private static void OpenUpdatesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://lilagents.xyz",
                UseShellExecute = true
            });
        }
        catch
        {
            MessageBox.Show(
                "Could not open the updates page.",
                "Lil Agents",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void Dispose()
    {
        StopRenderLoop();
        foreach (var character in _characters)
        {
            character.Dispose();
        }
        _trayManager.Dispose();
    }
}
