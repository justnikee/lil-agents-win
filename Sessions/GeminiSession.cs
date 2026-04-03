using System.Diagnostics;
using System.Text;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace LilAgents.Windows.Sessions;

/// <summary>
/// Gemini CLI session with JSON streaming and plain-text fallback.
/// </summary>
public class GeminiSession : IAgentSession
{
    private const int MaxErrorLinesPerTurn = 80;

    private Process? _process;
    private readonly string _workingDirectory;
    private bool _disposed;
    private bool _isRunning;
    private bool _isBusy;
    private bool _isFirstTurn = true;
    private bool _useJsonOutput;
    private bool _authHintEmitted;
    private bool _quotaHintEmitted;
    private int _remainingErrorLines = MaxErrorLinesPerTurn;
    private bool _suppressedErrorNoticeEmitted;
    private string _lineBuffer = string.Empty;
    private string _currentResponseText = string.Empty;
    private readonly List<AgentMessage> _history = [];

    public AgentProvider Provider => AgentProvider.Gemini;
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

    public GeminiSession(string workingDirectory) => _workingDirectory = workingDirectory;

    public async Task StartAsync(string? initialPrompt = null)
    {
        var binaryPath = Provider.FindProviderBinary();
        if (binaryPath == null)
        {
            EmitError($"Gemini CLI not found.\n\n{Provider.InstallInstructions()}");
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
            EmitError($"Gemini CLI not found.\n\n{Provider.InstallInstructions()}");
            return Task.CompletedTask;
        }

        Stop();
        _isRunning = true;
        _isBusy = true;
        ResetErrorBudget();
        _lineBuffer = string.Empty;
        _currentResponseText = string.Empty;
        _history.Add(new AgentMessage(AgentMessageRole.User, message));

        var args = new List<string> { "--yolo" };
        if (!_isFirstTurn)
        {
            args.Add("--continue");
        }
        args.Add("-p");
        args.Add(message);
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = string.Join(" ", args.Select(QuoteArg)),
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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
                EmitError("Failed to start Gemini");
                _isBusy = false;
                return Task.CompletedTask;
            }

            _isFirstTurn = false;
            _ = Task.Run(() => ReadOutputAsync(_process));
            _ = Task.Run(() => ReadErrorAsync(_process));
        }
        catch (Exception ex)
        {
            EmitError($"Failed to start Gemini: {ex.Message}");
            _isBusy = false;
        }

        return Task.CompletedTask;
    }

    private async Task ReadOutputAsync(Process process)
    {
        var rawOutput = new StringBuilder();
        try
        {
            using var reader = process.StandardOutput;
            while (!process.HasExited || !reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                rawOutput.AppendLine(line);
                Application.Current?.Dispatcher.InvokeAsync(() => ParseLine(line));
            }
        }
        catch
        {
            // Ignore stream errors on shutdown.
        }
        finally
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (_useJsonOutput && rawOutput.Length > 0 && !rawOutput.ToString().TrimStart().StartsWith("{"))
                {
                    _useJsonOutput = false;
                }

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
            // Ignore stream errors on shutdown.
        }
    }

    private void ParseLine(string line)
    {
        if (!SessionOutputSanitizer.TrySanitizeLine(line, out var sanitized))
        {
            return;
        }

        try
        {
            var json = JObject.Parse(sanitized);
            HandleJsonEvent(json);
        }
        catch
        {
            HandleNonJsonOutputLine(sanitized);
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

        if (TryEmitAuthHint(line) || TryEmitQuotaHint(line))
        {
            return;
        }

        EmitStderrWithinBudget(line);
    }

    private void HandleNonJsonOutputLine(string line)
    {
        if (ShouldFilterStderr(line))
        {
            return;
        }

        if (TryEmitAuthHint(line) || TryEmitQuotaHint(line))
        {
            return;
        }

        if (LooksLikeRuntimeError(line))
        {
            EmitStderrWithinBudget(line);
            return;
        }

        AppendAssistantText(line + Environment.NewLine);
    }

    private void HandleJsonEvent(JObject json)
    {
        var type = json["type"]?.ToString() ?? string.Empty;
        switch (type)
        {
            case "content":
                var content = json["content"]?.ToString();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    AppendAssistantText(content);
                }
                break;

            case "tool_call":
                var toolName = json["tool_name"]?.ToString() ?? json["name"]?.ToString() ?? "tool";
                var arguments = json["arguments"]?.ToString() ?? json["input"]?.ToString();
                OnToolUse?.Invoke(toolName, arguments);
                _history.Add(new AgentMessage(AgentMessageRole.ToolUse, $"{toolName}: {arguments}"));
                break;

            case "tool_result":
                var output = json["output"]?.ToString() ?? json["result"]?.ToString() ?? string.Empty;
                var isError = json["error"] != null;
                OnToolResult?.Invoke(output, !isError);
                _history.Add(new AgentMessage(
                    AgentMessageRole.ToolResult,
                    isError ? $"ERROR: {output}" : output
                ));
                break;

            case "turn_complete":
            case "done":
                CompleteTurn();
                break;

            case "error":
                EmitError(json["message"]?.ToString() ?? json["error"]?.ToString() ?? "Unknown error");
                break;

            default:
                var fallback = json["text"]?.ToString() ??
                               json["content"]?.ToString() ??
                               json["message"]?.ToString();
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    AppendAssistantText(fallback);
                }
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
        if (_authHintEmitted)
        {
            return true;
        }

        if (!ContainsAuthHint(line))
        {
            return false;
        }

        _authHintEmitted = true;
        EmitError("Gemini is not authenticated. Run `gemini auth` in a terminal, then retry.");
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

        return line.Contains("keytar", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("spinner", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("[MCP STDERR", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("ExperimentalWarning", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("⠋") || line.Contains("⠙") || line.Contains("⠹") ||
               line.Contains("⠸") || line.Contains("⠼") || line.Contains("⠴") ||
               line.Contains("⠦") || line.Contains("⠧") || line.Contains("⠇") ||
               line.Contains("⠏");
    }

    private static bool LooksLikeRuntimeError(string line)
    {
        return line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("status 4", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("status 5", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("gaxios", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryEmitQuotaHint(string line)
    {
        if (_quotaHintEmitted)
        {
            return true;
        }

        if (!line.Contains("resource_exhausted", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("exhausted your capacity", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("status 429", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _quotaHintEmitted = true;
        EmitError("Gemini quota exceeded (429 / RESOURCE_EXHAUSTED). Wait for reset, switch model, or use another provider.");
        return true;
    }

    private void ResetErrorBudget()
    {
        _remainingErrorLines = MaxErrorLinesPerTurn;
        _suppressedErrorNoticeEmitted = false;
        _quotaHintEmitted = false;
        _authHintEmitted = false;
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

    private static string QuoteArg(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
        {
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }
        return arg;
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
