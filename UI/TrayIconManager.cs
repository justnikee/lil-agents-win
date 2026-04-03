using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.IO;
using Hardcodet.Wpf.TaskbarNotification;
using LilAgents.Windows.Characters;
using LilAgents.Windows.Core;
using LilAgents.Windows.Sessions;
using LilAgents.Windows.Themes;

namespace LilAgents.Windows.UI;

/// <summary>
/// System tray icon and menu manager.
/// </summary>
public class TrayIconManager : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private IReadOnlyList<WalkerCharacter> _characters = [];
    private IReadOnlyList<TaskbarDetector.DisplayInfo> _displays = [];
    private int _selectedDisplayIndex = -1;
    private bool _soundsEnabled;

    public event Action? OnQuitClicked;
    public event Action<PopoverTheme>? OnThemeChanged;
    public event Action<bool>? OnSoundToggled;
    public event Action<string, bool>? OnCharacterVisibilityChanged;
    public event Action<string, AgentProvider>? OnCharacterProviderChanged;
    public event Action<string, CharacterSize>? OnCharacterSizeChanged;
    public event Action<int>? OnPinnedDisplayChanged;
    public event Action? OnCheckForUpdatesClicked;
    public event Action? OnAboutClicked;

    public void Initialize(
        IReadOnlyList<WalkerCharacter> characters,
        bool soundsEnabled,
        IReadOnlyList<TaskbarDetector.DisplayInfo> displays,
        int selectedDisplayIndex)
    {
        _characters = characters;
        _soundsEnabled = soundsEnabled;
        _displays = displays;
        _selectedDisplayIndex = selectedDisplayIndex;
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Lil Agents",
            ContextMenu = BuildContextMenu(),
            Icon = GenerateTrayIcon(),
        };
    }

    public void UpdateDisplays(IReadOnlyList<TaskbarDetector.DisplayInfo> displays, int selectedDisplayIndex)
    {
        _displays = displays;
        _selectedDisplayIndex = selectedDisplayIndex;
        if (_trayIcon != null)
        {
            _trayIcon.ContextMenu = BuildContextMenu();
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        foreach (var character in _characters)
        {
            var characterItem = new MenuItem
            {
                Header = character.Name,
                IsCheckable = true,
                IsChecked = character.IsManuallyVisible
            };
            var localCharacter = character;
            characterItem.Click += (_, _) =>
            {
                OnCharacterVisibilityChanged?.Invoke(localCharacter.Name, characterItem.IsChecked);
            };
            menu.Items.Add(characterItem);
        }

        menu.Items.Add(new Separator());

        var soundsItem = new MenuItem
        {
            Header = "Sounds",
            IsCheckable = true,
            IsChecked = _soundsEnabled
        };
        soundsItem.Click += (_, _) =>
        {
            _soundsEnabled = soundsItem.IsChecked;
            OnSoundToggled?.Invoke(soundsItem.IsChecked);
        };
        menu.Items.Add(soundsItem);

        var providerMenu = new MenuItem { Header = "Provider" };
        var initialProvider = _characters.FirstOrDefault()?.Provider ?? AgentProvider.Claude;
        foreach (var provider in Enum.GetValues<AgentProvider>())
        {
            var providerItem = new MenuItem
            {
                Header = provider.DisplayName(),
                IsCheckable = true,
                IsChecked = provider == initialProvider,
                IsEnabled = provider.IsAvailable(),
            };

            var selectedProvider = provider;
            providerItem.Click += (_, _) =>
            {
                foreach (MenuItem item in providerMenu.Items)
                {
                    item.IsChecked = ReferenceEquals(item, providerItem);
                }

                foreach (var character in _characters)
                {
                    OnCharacterProviderChanged?.Invoke(character.Name, selectedProvider);
                }
            };

            providerMenu.Items.Add(providerItem);
        }
        menu.Items.Add(providerMenu);

        var sizeMenu = new MenuItem { Header = "Size" };
        var currentSize = _characters.FirstOrDefault()?.Size ?? CharacterSize.Large;
        foreach (var size in Enum.GetValues<CharacterSize>())
        {
            var sizeItem = new MenuItem
            {
                Header = size.DisplayName(),
                IsCheckable = true,
                IsChecked = size == currentSize
            };
            var selectedSize = size;
            sizeItem.Click += (_, _) =>
            {
                foreach (MenuItem item in sizeMenu.Items)
                {
                    item.IsChecked = ReferenceEquals(item, sizeItem);
                }

                foreach (var character in _characters)
                {
                    OnCharacterSizeChanged?.Invoke(character.Name, selectedSize);
                }
            };
            sizeMenu.Items.Add(sizeItem);
        }
        menu.Items.Add(sizeMenu);

        var themeMenu = new MenuItem { Header = "Style" };
        foreach (var theme in PopoverTheme.AllThemes)
        {
            var themeItem = new MenuItem
            {
                Header = theme.Name,
                IsCheckable = true,
                IsChecked = theme.Name.Equals(PopoverTheme.Current.Name, StringComparison.OrdinalIgnoreCase)
            };
            var selectedTheme = theme;
            themeItem.Click += (_, _) =>
            {
                foreach (MenuItem item in themeMenu.Items)
                {
                    item.IsChecked = ReferenceEquals(item, themeItem);
                }
                OnThemeChanged?.Invoke(selectedTheme);
            };
            themeMenu.Items.Add(themeItem);
        }
        menu.Items.Add(themeMenu);

        var displayMenu = new MenuItem { Header = "Display" };

        var autoItem = new MenuItem
        {
            Header = "Auto (Main Display)",
            IsCheckable = true,
            IsChecked = _selectedDisplayIndex < 0
        };
        autoItem.Click += (_, _) =>
        {
            foreach (MenuItem item in displayMenu.Items)
            {
                item.IsChecked = ReferenceEquals(item, autoItem);
            }

            _selectedDisplayIndex = -1;
            OnPinnedDisplayChanged?.Invoke(-1);
        };
        displayMenu.Items.Add(autoItem);

        if (_displays.Count > 0)
        {
            displayMenu.Items.Add(new Separator());
        }

        foreach (var display in _displays)
        {
            var displayIndex = display.Index;
            var item = new MenuItem
            {
                Header = display.Name,
                IsCheckable = true,
                IsChecked = _selectedDisplayIndex == displayIndex
            };
            item.Click += (_, _) =>
            {
                foreach (MenuItem menuItem in displayMenu.Items)
                {
                    menuItem.IsChecked = ReferenceEquals(menuItem, item);
                }

                _selectedDisplayIndex = displayIndex;
                OnPinnedDisplayChanged?.Invoke(displayIndex);
            };
            displayMenu.Items.Add(item);
        }

        menu.Items.Add(displayMenu);

        menu.Items.Add(new Separator());

        var updatesItem = new MenuItem { Header = "Check for Updates..." };
        updatesItem.Click += (_, _) => OnCheckForUpdatesClicked?.Invoke();
        menu.Items.Add(updatesItem);

        menu.Items.Add(new Separator());

        var aboutItem = new MenuItem { Header = "About" };
        aboutItem.Click += (_, _) => OnAboutClicked?.Invoke();
        menu.Items.Add(aboutItem);

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => OnQuitClicked?.Invoke();
        menu.Items.Add(quitItem);

        return menu;
    }

    private static System.Drawing.Icon GenerateTrayIcon()
    {
        var iconPathCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "Icons", "menuicon.png"),
            Path.Combine(AppContext.BaseDirectory, "Icons", "menuicon.png"),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "LilAgents.Windows", "Resources", "Icons", "menuicon.png")),
        };

        foreach (var path in iconPathCandidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var source = new System.Drawing.Bitmap(path);
                using var resized = new System.Drawing.Bitmap(source, new System.Drawing.Size(16, 16));
                return CreateIconFromBitmap(resized);
            }
            catch
            {
                // Fallback to generated icon below.
            }
        }

        using var bitmap = new System.Drawing.Bitmap(16, 16);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);

        using var bodyBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(224, 122, 95));
        g.FillEllipse(bodyBrush, 2, 2, 12, 12);

        using var eyeBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
        g.FillEllipse(eyeBrush, 4, 4, 3, 3);
        g.FillEllipse(eyeBrush, 9, 4, 3, 3);

        using var pupilBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(40, 40, 40));
        g.FillEllipse(pupilBrush, 5, 5, 2, 2);
        g.FillEllipse(pupilBrush, 10, 5, 2, 2);

        return CreateIconFromBitmap(bitmap);
    }

    private static System.Drawing.Icon CreateIconFromBitmap(System.Drawing.Bitmap bitmap)
    {
        var hIcon = bitmap.GetHicon();
        try
        {
            // Clone so we can release the unmanaged HICON immediately.
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
