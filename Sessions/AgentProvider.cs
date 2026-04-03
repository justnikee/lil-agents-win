namespace LilAgents.Windows.Sessions;

public enum AgentProvider
{
    Claude,
    Codex,
    Copilot,
    Gemini,
    OpenCode
}

public enum TitleFormat
{
    Uppercase,
    LowercaseTilde,
    Capitalized
}

public enum AgentMessageRole
{
    User,
    Assistant,
    Error,
    ToolUse,
    ToolResult
}

public record AgentMessage(AgentMessageRole Role, string Text);

/// <summary>
/// Interface for all agent session implementations.
/// </summary>
public interface IAgentSession : IDisposable
{
    AgentProvider Provider { get; }
    bool IsRunning { get; }
    bool HasActiveProcess { get; }
    bool IsBusy { get; }
    IReadOnlyList<AgentMessage> History { get; }

    event Action<string>? OnText;
    event Action<string>? OnError;
    event Action<string, string?>? OnToolUse;
    event Action<string, bool>? OnToolResult;
    event Action? OnSessionReady;
    event Action? OnTurnComplete;
    event Action<int>? OnProcessExit;

    Task SendMessageAsync(string message);
    Task StartAsync(string? initialPrompt = null);
    void Stop();
}

public static class AgentProviderExtensions
{
    private static readonly Dictionary<AgentProvider, bool> Availability = new();

    public static string DisplayName(this AgentProvider p) => p switch
    {
        AgentProvider.Claude => "Claude",
        AgentProvider.Codex => "Codex",
        AgentProvider.Copilot => "Copilot",
        AgentProvider.Gemini => "Gemini",
        AgentProvider.OpenCode => "OpenCode",
        _ => p.ToString()
    };

    public static string InputPlaceholder(this AgentProvider p) => $"Ask {p.DisplayName()}...";

    public static string TitleString(this AgentProvider p, TitleFormat format) => format switch
    {
        TitleFormat.Uppercase => p.DisplayName().ToUpperInvariant(),
        TitleFormat.LowercaseTilde => p.DisplayName().ToLowerInvariant() + " ~",
        _ => p.DisplayName()
    };

    public static string BinaryName(this AgentProvider p) => p switch
    {
        AgentProvider.Claude => "claude",
        AgentProvider.Codex => "codex",
        AgentProvider.Copilot => "copilot",
        AgentProvider.Gemini => "gemini",
        AgentProvider.OpenCode => "opencode",
        _ => string.Empty
    };

    public static string LoginHint(this AgentProvider p) => p switch
    {
        AgentProvider.Claude =>
            "Run `claude auth login` (or run `claude` and execute `/login`) in a terminal, then retry here.",
        AgentProvider.Codex =>
            "Run `codex login` in a terminal, then retry here.",
        AgentProvider.Copilot =>
            "Run `copilot login` in a terminal, then retry here.",
        AgentProvider.Gemini =>
            "Run `gemini` in a terminal and complete sign-in there, then retry here.",
        AgentProvider.OpenCode =>
            "Run `opencode` in a terminal and complete login there, then retry here.",
        _ => "Run the provider CLI in a terminal, sign in, then retry here."
    };

    public static string InstallInstructions(this AgentProvider p) => p switch
    {
        AgentProvider.Claude =>
            "Install Claude CLI:\n  curl -fsSL https://claude.ai/install.sh | sh",
        AgentProvider.Codex =>
            "Install Codex CLI:\n  npm install -g @openai/codex",
        AgentProvider.Copilot =>
            "Install Copilot CLI:\n  npm install -g @github/copilot",
        AgentProvider.Gemini =>
            "Install Gemini CLI:\n  npm install -g @google/gemini-cli",
        AgentProvider.OpenCode =>
            "Install OpenCode CLI:\n  curl -fsSL https://opencode.ai/install | bash",
        _ => "Install provider CLI and make sure it is on PATH."
    };

    public static bool IsAvailable(this AgentProvider p)
    {
        if (Availability.TryGetValue(p, out var isAvailable))
        {
            return isAvailable;
        }

        return false;
    }

    public static AgentProvider FirstAvailable()
    {
        return Enum
            .GetValues<AgentProvider>()
            .FirstOrDefault(provider => provider.IsAvailable(), AgentProvider.Claude);
    }

    /// <summary>
    /// Detects which providers have their CLI binaries on PATH.
    /// </summary>
    public static List<AgentProvider> DetectAvailableProviders()
    {
        var available = new List<AgentProvider>();

        foreach (var provider in Enum.GetValues<AgentProvider>())
        {
            var found = FindProviderBinary(provider) != null;
            Availability[provider] = found;
            if (found)
            {
                available.Add(provider);
            }
        }

        return available;
    }

    public static string? FindProviderBinary(this AgentProvider provider)
    {
        return provider switch
        {
            AgentProvider.Copilot =>
                Core.ShellEnvironment.FindBinary("copilot") ??
                Core.ShellEnvironment.FindBinary("github-copilot"),
            _ => Core.ShellEnvironment.FindBinary(provider.BinaryName())
        };
    }

    public static IAgentSession CreateSession(this AgentProvider p, string workingDirectory)
    {
        return p switch
        {
            AgentProvider.Claude => new ClaudeSession(workingDirectory),
            AgentProvider.Gemini => new GeminiSession(workingDirectory),
            AgentProvider.Codex => new CodexSession(workingDirectory),
            AgentProvider.Copilot => new CopilotSession(workingDirectory),
            AgentProvider.OpenCode => new OpenCodeSession(workingDirectory),
            _ => throw new NotSupportedException($"Provider {p} is not supported")
        };
    }
}
