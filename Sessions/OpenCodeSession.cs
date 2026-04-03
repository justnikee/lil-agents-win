using System.Diagnostics;
using System.Text;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace LilAgents.Windows.Sessions;

/// <summary>
/// OpenCode CLI session with JSONL parsing.
/// </summary>
public class OpenCodeSession : IAgentSession
{
    private const int MaxErrorLinesPerTurn = 80;

    private Process? _process;
    private readonly string _workingDirectory;
    private bool _disposed;
    private bool _isRunning;
    private bool _isBusy;
    private bool _authHintEmitted;
    private int _remainingErrorLines = MaxErrorLinesPerTurn;
    private bool _suppressedErrorNoticeEmitted;
    private string _lineBuffer = string.Empty;
    private string _currentResponseText = string.Empty;
    private readonly List<AgentMessage> _history = [];

    public AgentProvider Provider => AgentProvider.OpenCode;
    public bool IsRunning => _isRunning;
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

    public OpenCodeSession(string workingDirectory) => _workingDirectory = workingDirectory;

    public async Task StartAsync(string? initialPrompt = null)
    {
        var binaryPath = Provider.FindProviderBinary();
        if (binaryPath == null)
        {
            EmitError($"OpenCode CLI not found.\n\n{Provider.InstallInstructions()}");
            return;
        }

        _isRunning = true;
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
            EmitError($"OpenCode CLI not found.\n\n{Provider.InstallInstructions()}");
            return Task.CompletedTask;
        }

        Stop();
        _isRunning = true;
        _isBusy = true;
        ResetErrorBudget();
        _lineBuffer = string.Empty;
        _currentResponseText = string.Empty;
        _history.Add(new AgentMessage(AgentMessageRole.User, message));

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
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(message);
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("json");

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
                EmitError("Failed to start OpenCode");
                _isBusy = false;
                return Task.CompletedTask;
            }

            _ = Task.Run(() => ReadOutputAsync(_process));
            _ = Task.Run(() => ReadErrorAsync(_process));
        }
        catch (Exception ex)
        {
            EmitError($"Failed to start OpenCode: {ex.Message}");
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
            // Ignore stream errors on process shutdown.
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
            // Ignore stream errors on process shutdown.
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
        JObject? json = null;
        try
        {
            json = JObject.Parse(line);
        }
        catch
        {
            return;
        }

        var type = json["type"]?.ToString() ?? string.Empty;

        switch (type)
        {
            case "text":
                var textPart = json["part"]?["text"]?.ToString() ?? json["content"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(textPart))
                {
                    AppendAssistantText(textPart);
                }
                break;

            case "assistant.tool_call":
            case "tool_call":
            case "tool_use":
                var toolName = json["part"]?["name"]?.ToString() ??
                               json["tool_name"]?.ToString() ??
                               json["name"]?.ToString() ?? "tool";
                var input = json["part"]?["arguments"]?.ToString() ??
                            json["arguments"]?.ToString() ??
                            json["input"]?.ToString();
                OnToolUse?.Invoke(toolName, input);
                _history.Add(new AgentMessage(AgentMessageRole.ToolUse, $"{toolName}: {input}"));
                break;

            case "assistant.tool_result":
            case "tool_result":
                var output = json["part"]?["result"]?.ToString() ??
                             json["output"]?.ToString() ??
                             json["result"]?.ToString() ?? string.Empty;
                var isError = string.Equals(json["part"]?["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase) ||
                              json["error"] != null;
                OnToolResult?.Invoke(output, !isError);
                _history.Add(new AgentMessage(
                    AgentMessageRole.ToolResult,
                    isError ? $"ERROR: {output}" : output
                ));
                break;

            case "result":
            case "turn_complete":
            case "done":
                CompleteTurn();
                break;

            case "error":
                EmitError(json["message"]?.ToString() ?? json["error"]?.ToString() ?? "Unknown error");
                break;
        }
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

    private void CompleteTurn()
    {
        if (!string.IsNullOrWhiteSpace(_currentResponseText))
        {
            _history.Add(new AgentMessage(AgentMessageRole.Assistant, _currentResponseText));
            _currentResponseText = string.Empty;
        }

        if (_isBusy)
        {
            _isBusy = false;
            OnTurnComplete?.Invoke();
        }
    }

    private void EmitError(string message)
    {
        _history.Add(new AgentMessage(AgentMessageRole.Error, message));
        OnError?.Invoke(message);
    }

    private bool TryEmitAuthHint(string line)
    {
        if (_authHintEmitted || !ContainsAuthHint(line))
        {
            return false;
        }

        _authHintEmitted = true;
        EmitError("OpenCode is not authenticated. Run `opencode` in a terminal and complete login, then retry.");
        return true;
    }

    private static bool ContainsAuthHint(string text)
    {
        return text.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldFilterStderr(string line)
    {
        if (SessionOutputSanitizer.IsLikelyHtmlNoise(line) || SessionOutputSanitizer.IsLikelyStackTrace(line))
        {
            return true;
        }

        return line.Contains("ExperimentalWarning", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("punycode", StringComparison.OrdinalIgnoreCase) ||
               line.TrimStart().StartsWith("(") ||
               line.TrimStart().StartsWith("node:");
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
