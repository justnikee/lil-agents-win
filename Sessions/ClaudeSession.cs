using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace LilAgents.Windows.Sessions;

/// <summary>
/// Claude CLI session — ported from ClaudeSession.swift.
/// Uses stream-json input/output format for structured NDJSON communication.
/// </summary>
public class ClaudeSession : IAgentSession
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

    public AgentProvider Provider => AgentProvider.Claude;
    public bool IsRunning => _process is { HasExited: false };
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

    public ClaudeSession(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public async Task StartAsync(string? initialPrompt = null)
    {
        var binaryPath = Provider.FindProviderBinary();
        if (binaryPath == null)
        {
            EmitError($"Claude CLI not found.\n\n{Provider.InstallInstructions()}");
            return;
        }

        var args = "-p --output-format stream-json --input-format stream-json --verbose --dangerously-skip-permissions";

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = args,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Set environment
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
                OnError?.Invoke("Failed to start Claude process");
                return;
            }

            // Start reading output on a background thread
            _ = Task.Run(() => ReadOutputAsync(_process));
            _ = Task.Run(() => ReadErrorAsync(_process));

            if (!string.IsNullOrEmpty(initialPrompt))
            {
                await SendMessageAsync(initialPrompt);
            }
        }
        catch (Exception ex)
        {
            EmitError($"Failed to start Claude: {ex.Message}");
        }
    }

    public async Task SendMessageAsync(string message)
    {
        if (_process?.HasExited != false) return;

        try
        {
            ResetErrorBudget();
            _isBusy = true;
            _currentResponseText = string.Empty;
            _history.Add(new AgentMessage(AgentMessageRole.User, message));

            var jsonMessage = new JObject
            {
                ["type"] = "user",
                ["message"] = new JObject
                {
                    ["role"] = "user",
                    ["content"] = message
                }
            };

            var line = jsonMessage.ToString(Newtonsoft.Json.Formatting.None);
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            EmitError($"Failed to send message: {ex.Message}");
        }
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
        catch (Exception) { /* Process ended */ }
        finally
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (_isBusy)
                {
                    CompleteTurn();
                }

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
        catch { /* Process ended */ }
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
            var type = json["type"]?.ToString() ?? "";

            switch (type)
            {
                case "system":
                    if (string.Equals(json["subtype"]?.ToString(), "init", StringComparison.OrdinalIgnoreCase))
                    {
                        OnSessionReady?.Invoke();
                    }
                    break;

                case "assistant":
                    var content = json["message"]?["content"];
                    if (content is JArray contentArray)
                    {
                        foreach (var block in contentArray)
                        {
                            var blockType = block["type"]?.ToString();
                            if (blockType == "text")
                            {
                                AppendAssistantText(block["text"]?.ToString() ?? string.Empty);
                            }
                            else if (blockType == "tool_use")
                            {
                                var toolName = block["name"]?.ToString() ?? "Tool";
                                var inputObj = block["input"] as JObject;
                                var summary = FormatToolSummary(toolName, inputObj);
                                OnToolUse?.Invoke(toolName, summary);
                                _history.Add(new AgentMessage(AgentMessageRole.ToolUse, $"{toolName}: {summary}"));
                            }
                        }
                    }
                    break;

                case "user":
                    var messageContent = json["message"]?["content"] as JArray;
                    if (messageContent == null)
                    {
                        break;
                    }

                    foreach (var block in messageContent.OfType<JObject>())
                    {
                        if (!string.Equals(block["type"]?.ToString(), "tool_result", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var isError = block["is_error"]?.ToObject<bool>() ?? false;
                        var summary = ExtractToolResultSummary(json, block);
                        OnToolResult?.Invoke(summary, !isError);
                        _history.Add(new AgentMessage(
                            AgentMessageRole.ToolResult,
                            isError ? $"ERROR: {summary}" : summary
                        ));
                    }
                    break;

                case "result":
                    CompleteTurn(json["result"]?.ToString());
                    break;

                case "error":
                    EmitError(
                        json["error"]?["message"]?.ToString() ??
                        json["message"]?.ToString() ??
                        "Unknown error");
                    break;
            }
        }
        catch
        {
            // Ignore malformed lines in stream-json mode.
        }
    }

    private void AppendAssistantText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _isBusy = true;
        _currentResponseText += text;
        OnText?.Invoke(text);
    }

    private void EmitError(string message)
    {
        _history.Add(new AgentMessage(AgentMessageRole.Error, message));
        OnError?.Invoke(message);
    }

    private void CompleteTurn(string? resultText = null)
    {
        if (!_isBusy && string.IsNullOrWhiteSpace(_currentResponseText) && string.IsNullOrWhiteSpace(resultText))
        {
            return;
        }

        var finalText = !string.IsNullOrWhiteSpace(resultText)
            ? resultText!.TrimEnd()
            : _currentResponseText;

        if (!string.IsNullOrWhiteSpace(finalText))
        {
            _history.Add(new AgentMessage(AgentMessageRole.Assistant, finalText));
        }

        _currentResponseText = string.Empty;
        _isBusy = false;
        OnTurnComplete?.Invoke();
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

    private bool TryEmitAuthHint(string line)
    {
        if (_authHintEmitted)
        {
            return false;
        }

        if (!ContainsAuthHint(line))
        {
            return false;
        }

        _authHintEmitted = true;
        EmitError("Claude is not authenticated. Run `claude` in a terminal and complete `/login`, then retry.");
        return true;
    }

    private static bool ContainsAuthHint(string text)
    {
        return text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("authenticate", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatToolSummary(string toolName, JObject? input)
    {
        if (input == null)
        {
            return string.Empty;
        }

        return toolName switch
        {
            "Bash" => input["command"]?.ToString() ?? string.Empty,
            "Read" => input["file_path"]?.ToString() ?? string.Empty,
            "Edit" => input["file_path"]?.ToString() ?? string.Empty,
            "Write" => input["file_path"]?.ToString() ?? string.Empty,
            "Glob" => input["pattern"]?.ToString() ?? string.Empty,
            "Grep" => input["pattern"]?.ToString() ?? string.Empty,
            _ => string.Join(", ", input.Properties().Select(p => p.Name).Take(3))
        };
    }

    private static string ExtractToolResultSummary(JObject envelope, JObject block)
    {
        var resultToken = envelope["tool_use_result"];
        if (resultToken is JObject resultObj)
        {
            if (string.Equals(resultObj["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
            {
                var file = resultObj["file"] as JObject;
                if (file != null)
                {
                    var path = file["filePath"]?.ToString();
                    var lines = file["totalLines"]?.ToObject<int>() ?? 0;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return $"{path} ({lines} lines)";
                    }
                }
            }

            var text = resultObj["text"]?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return Clip(text, 120);
            }
        }
        else if (resultToken != null)
        {
            var text = resultToken.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return Clip(text, 120);
            }
        }

        var blockContent = block["content"]?.ToString();
        if (!string.IsNullOrWhiteSpace(blockContent))
        {
            return Clip(blockContent, 120);
        }

        return string.Empty;
    }

    private static string Clip(string text, int maxChars)
    {
        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxChars ? normalized : normalized[..maxChars] + "...";
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
        catch { }
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
