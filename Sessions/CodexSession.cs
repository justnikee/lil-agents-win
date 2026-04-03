using System.Diagnostics;
using System.Text;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace LilAgents.Windows.Sessions;

/// <summary>
/// Codex CLI session — one-shot execution with JSON event streaming.
/// </summary>
public class CodexSession : IAgentSession
{
    private const int MaxErrorLinesPerTurn = 80;

    private Process? _process;
    private readonly string _workingDirectory;
    private bool _disposed;
    private bool _isBusy;
    private bool _authHintEmitted;
    private int _remainingErrorLines = MaxErrorLinesPerTurn;
    private bool _suppressedErrorNoticeEmitted;
    private string _currentResponseText = string.Empty;
    private readonly List<AgentMessage> _history = [];

    public AgentProvider Provider => AgentProvider.Codex;
    public bool IsRunning => true;
    public bool HasActiveProcess => _process != null && !_process.HasExited;
    public bool IsBusy => _isBusy;
    public IReadOnlyList<AgentMessage> History => _history;

    public event Action<string>? OnText;
    public event Action<string>? OnError;
    public event Action<string, string?>? OnToolUse;
    public event Action<string, bool>? OnToolResult;
    public event Action? OnSessionReady;
    public event Action? OnTurnComplete;
    public event Action<int>? OnProcessExit;

    public CodexSession(string workingDirectory) => _workingDirectory = workingDirectory;

    public async Task StartAsync(string? initialPrompt = null)
    {
        OnSessionReady?.Invoke();
        if (!string.IsNullOrWhiteSpace(initialPrompt))
        {
            await SendMessageAsync(initialPrompt);
        }
    }

    public Task SendMessageAsync(string message)
    {
        var binaryPath = Provider.FindProviderBinary();
        if (binaryPath == null)
        {
            EmitError($"Codex CLI not found.\n\n{Provider.InstallInstructions()}");
            return Task.CompletedTask;
        }

        Stop();
        ResetErrorBudget();
        _isBusy = true;
        _currentResponseText = string.Empty;
        _history.Add(new AgentMessage(AgentMessageRole.User, message));

        var prompt = BuildPromptFromHistory(message);
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--full-auto");
        psi.ArgumentList.Add("--skip-git-repo-check");
        psi.ArgumentList.Add(prompt);

        var env = Core.ShellEnvironment.GetProcessEnvironment();
        foreach (var kv in env)
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        try
        {
            _process = Process.Start(psi);
            if (_process == null)
            {
                EmitError("Failed to start Codex");
                _isBusy = false;
                return Task.CompletedTask;
            }

            _ = Task.Run(() => ReadOutputAsync(_process));
            _ = Task.Run(() => ReadErrorAsync(_process));
        }
        catch (Exception ex)
        {
            EmitError($"Failed to start Codex: {ex.Message}");
            _isBusy = false;
        }

        return Task.CompletedTask;
    }

    private async Task ReadOutputAsync(Process process)
    {
        try
        {
            using var reader = process.StandardOutput;
            while (!process.HasExited || !reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                Application.Current?.Dispatcher.InvokeAsync(() => ParseLine(line));
            }
        }
        catch
        {
            // Process ended or stream interrupted.
        }
        finally
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CompleteTurn();
                OnProcessExit?.Invoke(process.ExitCode);
            });
        }
    }

    private async Task ReadErrorAsync(Process process)
    {
        try
        {
            using var reader = process.StandardError;
            while (!process.HasExited || !reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                Application.Current?.Dispatcher.InvokeAsync(() => HandleStderrLine(line));
            }
        }
        catch
        {
            // Process ended or stream interrupted.
        }
    }

    private void HandleStderrLine(string rawLine)
    {
        if (!SessionOutputSanitizer.TrySanitizeLine(rawLine, out var line))
        {
            return;
        }

        if (ShouldFilterStderr(line))
        {
            return;
        }

        if (TryEmitAuthHint(line))
        {
            return;
        }

        EmitStderrWithinBudget(line);
    }

    private void ParseLine(string line)
    {
        try
        {
            var json = JObject.Parse(line);
            var type = json["type"]?.ToString() ?? string.Empty;

            switch (type)
            {
                case "item.started":
                    if (json["item"]?["type"]?.ToString() == "command_execution")
                    {
                        var command = json["item"]?["command"]?.ToString() ?? string.Empty;
                        OnToolUse?.Invoke("Bash", command);
                        _history.Add(new AgentMessage(AgentMessageRole.ToolUse, $"Bash: {command}"));
                    }
                    break;

                case "item.completed":
                    HandleCompletedItem(json["item"] as JObject);
                    break;

                case "turn.completed":
                    CompleteTurn();
                    break;

                case "turn.failed":
                    EmitError(json["message"]?.ToString() ?? "Turn failed");
                    CompleteTurn();
                    break;

                case "error":
                    EmitError(json["message"]?.ToString() ?? json["error"]?.ToString() ?? "Unknown error");
                    break;

                default:
                    var fallback = json["text"]?.ToString() ?? json["content"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        AppendAssistantText(fallback);
                    }
                    break;
            }
        }
        catch
        {
            if (SessionOutputSanitizer.TrySanitizeLine(line, out var plain))
            {
                AppendAssistantText(plain);
            }
        }
    }

    private void HandleCompletedItem(JObject? item)
    {
        if (item == null) return;

        var itemType = item["type"]?.ToString() ?? string.Empty;
        switch (itemType)
        {
            case "agent_message":
                var text = item["text"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    AppendAssistantText(text);
                }
                break;

            case "command_execution":
                var status = item["status"]?.ToString() ?? string.Empty;
                var command = item["command"]?.ToString() ?? status;
                var isError = status.Equals("failed", StringComparison.OrdinalIgnoreCase);
                OnToolResult?.Invoke(command, !isError);
                _history.Add(new AgentMessage(
                    AgentMessageRole.ToolResult,
                    isError ? $"ERROR: {command}" : command));
                break;

            case "file_change":
                var path = item["file"]?.ToString() ?? item["path"]?.ToString() ?? "file";
                OnToolUse?.Invoke("FileChange", path);
                OnToolResult?.Invoke(path, true);
                _history.Add(new AgentMessage(AgentMessageRole.ToolUse, $"FileChange: {path}"));
                _history.Add(new AgentMessage(AgentMessageRole.ToolResult, path));
                break;
        }
    }

    private string BuildPromptFromHistory(string latestUserMessage)
    {
        if (_history.Count <= 1)
        {
            return latestUserMessage;
        }

        var previous = _history.Take(_history.Count - 1);
        var sb = new StringBuilder();
        foreach (var msg in previous)
        {
            var role = msg.Role switch
            {
                AgentMessageRole.User => "User",
                AgentMessageRole.Assistant => "Assistant",
                AgentMessageRole.ToolUse => "Tool",
                AgentMessageRole.ToolResult => "Tool result",
                AgentMessageRole.Error => "Error",
                _ => "Message",
            };
            sb.AppendLine($"{role}: {msg.Text}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append($"User (follow-up): {latestUserMessage}");
        return sb.ToString();
    }

    private void AppendAssistantText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _currentResponseText += text;
        OnText?.Invoke(text);
    }

    private void EmitError(string message)
    {
        _history.Add(new AgentMessage(AgentMessageRole.Error, message));
        OnError?.Invoke(message);
    }

    private void CompleteTurn()
    {
        if (!string.IsNullOrWhiteSpace(_currentResponseText))
        {
            _history.Add(new AgentMessage(AgentMessageRole.Assistant, _currentResponseText));
        }

        _currentResponseText = string.Empty;

        if (_isBusy)
        {
            _isBusy = false;
            OnTurnComplete?.Invoke();
        }
    }

    private bool TryEmitAuthHint(string line)
    {
        if (_authHintEmitted || !ContainsAuthHint(line))
        {
            return false;
        }

        _authHintEmitted = true;
        EmitError("Codex is not authenticated. Run `codex login` in a terminal, then retry.");
        return true;
    }

    private static bool ContainsAuthHint(string text)
    {
        return text.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldFilterStderr(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            return true;
        }

        if (SessionOutputSanitizer.IsLikelyHtmlNoise(line) || SessionOutputSanitizer.IsLikelyStackTrace(line))
        {
            return true;
        }

        return line.Contains("ExperimentalWarning", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("punycode", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Error code 403", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("shell_snapshot", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("startup_sync", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("plugins", StringComparison.OrdinalIgnoreCase);
    }

    private void ResetErrorBudget()
    {
        _remainingErrorLines = MaxErrorLinesPerTurn;
        _suppressedErrorNoticeEmitted = false;
    }

    private void EmitStderrWithinBudget(string line)
    {
        if (_remainingErrorLines <= 0)
        {
            if (!_suppressedErrorNoticeEmitted)
            {
                _suppressedErrorNoticeEmitted = true;
                EmitError("Additional diagnostic output suppressed. Run the provider in a terminal for full logs.");
            }
            return;
        }

        _remainingErrorLines--;
        EmitError(line);
    }

    public void Stop()
    {
        try
        {
            if (_process?.HasExited == false)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore kill errors.
        }
        finally
        {
            _isBusy = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _process?.Dispose();
    }
}
