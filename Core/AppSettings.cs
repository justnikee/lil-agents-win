using System.Text.Json;
using System.IO;
using LilAgents.Windows.Characters;
using LilAgents.Windows.Sessions;

namespace LilAgents.Windows.Core;

internal sealed class AppSettingsData
{
    public bool HasCompletedOnboarding { get; set; }
    public string SelectedTheme { get; set; } = "Peach";
    public bool SoundsEnabled { get; set; } = true;
    public int PinnedDisplayIndex { get; set; } = -1;
    public Dictionary<string, bool> CharacterVisibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CharacterProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CharacterSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private AppSettingsData _data;

    private AppSettings(string filePath, AppSettingsData data)
    {
        _filePath = filePath;
        _data = data;
    }

    public bool HasCompletedOnboarding
    {
        get => _data.HasCompletedOnboarding;
        set => _data.HasCompletedOnboarding = value;
    }

    public string SelectedTheme
    {
        get => _data.SelectedTheme;
        set => _data.SelectedTheme = value;
    }

    public bool SoundsEnabled
    {
        get => _data.SoundsEnabled;
        set => _data.SoundsEnabled = value;
    }

    public int PinnedDisplayIndex
    {
        get => _data.PinnedDisplayIndex;
        set => _data.PinnedDisplayIndex = value;
    }

    public AgentProvider GetProvider(string characterName, AgentProvider fallback)
    {
        if (_data.CharacterProviders.TryGetValue(characterName, out var raw) &&
            Enum.TryParse<AgentProvider>(raw, true, out var provider))
        {
            return provider;
        }

        return fallback;
    }

    public void SetProvider(string characterName, AgentProvider provider)
    {
        _data.CharacterProviders[characterName] = provider.ToString();
    }

    public CharacterSize GetSize(string characterName, CharacterSize fallback)
    {
        if (_data.CharacterSizes.TryGetValue(characterName, out var raw) &&
            Enum.TryParse<CharacterSize>(raw, true, out var size))
        {
            return size;
        }

        return fallback;
    }

    public void SetSize(string characterName, CharacterSize size)
    {
        _data.CharacterSizes[characterName] = size.ToString();
    }

    public bool GetCharacterVisible(string characterName, bool fallback)
    {
        if (_data.CharacterVisibility.TryGetValue(characterName, out var value))
        {
            return value;
        }

        return fallback;
    }

    public void SetCharacterVisible(string characterName, bool visible)
    {
        _data.CharacterVisibility[characterName] = visible;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_data, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    public static AppSettings Load()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LilAgents");
        var filePath = Path.Combine(root, "settings.json");

        AppSettingsData data;
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                data = JsonSerializer.Deserialize<AppSettingsData>(json, JsonOptions) ?? new AppSettingsData();
            }
            catch
            {
                data = new AppSettingsData();
            }
        }
        else
        {
            data = new AppSettingsData();
        }

        return new AppSettings(filePath, data);
    }
}
