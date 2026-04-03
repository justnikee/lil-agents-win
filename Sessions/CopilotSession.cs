using System.Diagnostics;
using System.Text;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace LilAgents.Windows.Sessions;

/// <summary>
/// Copilot CLI session with JSON streaming and plain-text fallback.
/// </summary>
public class CopilotSession : IAgentSession
{
    private const int MaxErrorLinesPerTurn = 80;

    private Process? _process;
    private readonly string _workingDirectory;
    private bool _disposed;
    private bool _isRunning;
    private bool _isBusy;
    private bool _isFirstTurn = true;
    private bool _useJsonOutput = true;
    private bool _authHintEmitted;
    private int _remainingErrorLines = MaxErrorLinesPerTurn;
    private bool _suppressedErrorNoticeEmitted;
    private string _lineBuffer = string.Empty;
    private string _currentResponseText = string.Empty;
    private readonly List<AgentMessage> _history = [];

    public AgentProvider Provider => AgentProvider.Copilot;
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

    public CopilotSession(string workingDirectory) => _workingDirectory = workingDirectory;

    public async Task StartAsync(string? initialPrompt = null)
    {
        var binaryPath = Provider.FindProviderBinary();
        if (binaryPath == null)
        {
            EmitError($"Copilot CLI not found.\n\n{Provider.InstallInstructions()}");
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
            EmitError($"Copilot CLI not found.\n\n{Provider.InstallInstructions()}");
            return Task.CompletedTask;
        }

        Stop();
        _isRunning = true;
        _isBusy = true;
        ResetErrorBudget();
        _lineBuffer = string.Empty;
        _currentResponseText = string.Empty;
        _history.Add(new AgentMessage(AgentMessageRole.User, message));

        var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
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

        var args = new List<string>();
        if (!_isFirstTurn)
        {
            args.Add("--continue");
        }
        args.Add("-p");
        args.Add(message);
        if (_useJsonOutput)
        {
            args.Add("--output-format");
            args.Add("json");
        }
        else
        {
            args.Add("-s");
        }
        args.Add("--allow-all");
        proc.StartInfo.Arguments = string.Join(" ", args.Select(QuoteArg));

        var env = Core.ShellEnvironment.GetProcessEnvironment();
        foreach (var kv in env)
        {
            proc.StartInfo.Environment[kv.Key] = kv.Value;
        }

        try
        {
            proc.Start();
            _process = proc;
            _isFirstTurn = false;

            _ = Task.Run(() => ReadOutputAsync(proc));
            _ = Task.Run(() => ReadErrorAsync(proc));
        }
        catch (Exception ex)
        {
            EmitError($"Failed to start Copilot: {ex.Message}");
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

                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (_useJsonOutput)
                    {
                        ProcessOutput(line + "\n");
                    }
                    else
                    {
                        if (SessionOutputSanitizer.TrySanitizeLine(line, out var plain))
                        {
                            AppendAssistantText(plain + Environment.NewLine);
                        }
                    }
                });
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
                if (_useJsonOutput)
                {
                    FlushLineBuffer();
                }

                CompleteTurn();
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

    private void ProcessOutput(string text)
    {
        _lineBuffer += text;
        while (true)
        {
            var newlineIndex = _lineBuffer.IndexOf('\n');
            if (newlineIndex < 0)
            {
                break;
            }

            var line = _lineBuffer[..newlineIndex];
            _lineBuffer = _lineBuffer[(newlineIndex + 1)..];
            if (!string.IsNullOrWhiteSpace(line))
            {
                ParseLine(line);
            }
        }
    }

    private void FlushLineBuffer()
    {
        if (!string.IsNullOrWhiteSpace(_lineBuffer))
        {
            ParseLine(_lineBuffer);
            _lineBuffer = string.Empty;
        }
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
            // If this build does not support JSON output, fall back.
            if (_history.Count <= 1)
            {
                _useJsonOutput = false;
            }

            if (SessionOutputSanitizer.TrySanitizeLine(line, out var plain))
            {
                AppendAssistantText(plain + Environment.NewLine);
            }
            return;
        }

        if (json["ephemeral"]?.ToObject<bool>() == true)
        {
            if (json["type"]?.ToString() == "assistant.message_delta")
            {
                var delta = json["data"]?["deltaContent"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(delta))
                {
                    AppendAssistantText(delta);
                }
            }
            return;
        }

        var type = json["type"]?.ToString() ?? string.Empty;
        var data = json["data"] as JObject;

        switch (type)
        {
            case "assistant.message":
                var content = data?["content"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(_currentResponseText) && !string.IsNullOrWhiteSpace(content))
                {
                    AppendAssistantText(content);
                }
                break;

            case "assistant.turn_end":
            case "result":
                CompleteTurn();
                break;

            case "assistant.tool_call":
                var toolName = data?["name"]?.ToString() ?? data?["tool"]?.ToString() ?? "Tool";
                var input = data?["input"]?.ToString() ?? data?["arguments"]?.ToString();
                OnToolUse?.Invoke(toolName, input);
                _history.Add(new AgentMessage(AgentMessageRole.ToolUse, $"{toolName}: {input}"));
                break;

            case "assistant.tool_result":
                var output = data?["output"]?.ToString() ?? data?["result"]?.ToString() ?? string.Empty;
                var isError = data?["is_error"]?.ToObject<bool>() == true ||
                              string.Equals(data?["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase);
                OnToolResult?.Invoke(output, !isError);
                _history.Add(new AgentMessage(
                    AgentMessageRole.ToolResult,
                    isError ? $"ERROR: {output}" : output));
                break;

            case "error":
                EmitError(data?["message"]?.ToString() ?? data?["error"]?.ToString() ?? "Unknown error");
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
        EmitError("Copilot is not authenticated. Run `copilot login` in a terminal, then retry.");
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
