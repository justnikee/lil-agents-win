using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LilAgents.Windows.Sessions;
using LilAgents.Windows.Themes;


namespace LilAgents.Windows.UI;

/// <summary>
/// Interactive terminal with markdown rendering and slash commands.
/// </summary>
public partial class TerminalControl : UserControl
{
    private Paragraph? _currentStreamParagraph;
    private Run? _currentStreamRun;
    private bool _isStreaming;
    private bool _showingSessionMessage;
    private string _currentAssistantText = string.Empty;
    private string _lastAssistantText = string.Empty;
    private AgentProvider _provider = AgentProvider.Claude;
    private DispatcherTimer? _thinkingTimer;
    private int _thinkingDotStep;


    public event Action<string>? OnMessageSubmitted;
    public event Action? OnClearRequested;

    public PopoverTheme Theme { get; private set; } = PopoverTheme.Current;

    public TerminalControl()
    {
        InitializeComponent();
        ApplyTheme(Theme);
        SetProvider(_provider);
        PopoverTheme.ThemeChanged += ApplyTheme;
        Unloaded += OnUnloaded;
    }

    public void ApplyTheme(PopoverTheme theme)
    {
        Theme = theme;
        OutputBox.Background = PopoverTheme.Brush(theme.TerminalBackground);
        OutputBox.Foreground = PopoverTheme.Brush(theme.TerminalTextColor);
        OutputBox.FontFamily = theme.TerminalFont;
        OutputBox.FontSize = theme.TerminalFontSize;
        OutputBox.SelectionBrush = PopoverTheme.Brush(theme.TerminalSelectionColor);

        InputBorder.Background = PopoverTheme.Brush(theme.InputBackground);
        InputBorder.BorderBrush = PopoverTheme.Brush(theme.InputBorderColor);
        InputBorder.CornerRadius = new CornerRadius(theme.InputCornerRadius);

        InputBox.Background = Brushes.Transparent;
        InputBox.Foreground = PopoverTheme.Brush(theme.InputTextColor);
        InputBox.FontFamily = theme.InputFont;
        InputBox.FontSize = theme.InputFontSize;
        InputBox.CaretBrush = PopoverTheme.Brush(theme.InputTextColor);

        PlaceholderText.Foreground = PopoverTheme.Brush(theme.InputPlaceholderColor);
        PlaceholderText.FontFamily = theme.InputFont;
        PlaceholderText.FontSize = theme.InputFontSize;

        var dotBrush = PopoverTheme.Brush(theme.TerminalTextColor);
        ThinkingLabel.Foreground = dotBrush;
        Dot1.Fill = dotBrush;
        Dot2.Fill = dotBrush;
        Dot3.Fill = dotBrush;

        OutputBox.Resources[SystemColors.ControlBrushKey] = PopoverTheme.Brush(theme.ScrollbarTrackColor);
        UpdatePlaceholderVisibility();
    }

    public void SetProvider(AgentProvider provider)
    {
        _provider = provider;
        PlaceholderText.Text = _provider.InputPlaceholder();
        UpdatePlaceholderVisibility();
    }

    public void SetInputEnabled(bool enabled)
    {
        InputBox.IsEnabled = enabled;
        if (enabled)
        {
            FocusInput();
        }
        UpdatePlaceholderVisibility();
    }

    public void ShowThinkingIndicator()
    {
        ThinkingLabel.Text = "thinking";
        ThinkingBorder.Visibility = Visibility.Visible;

        _thinkingDotStep = 0;
        Dot1.Opacity = 0.2;
        Dot2.Opacity = 0.2;
        Dot3.Opacity = 0.2;

        if (_thinkingTimer == null)
        {
            _thinkingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _thinkingTimer.Tick += (_, _) =>
            {
                _thinkingDotStep = (_thinkingDotStep + 1) % 4;
                Dot1.Opacity = _thinkingDotStep >= 1 ? 0.85 : 0.15;
                Dot2.Opacity = _thinkingDotStep >= 2 ? 0.85 : 0.15;
                Dot3.Opacity = _thinkingDotStep >= 3 ? 0.85 : 0.15;
            };
        }
        _thinkingTimer.Start();
    }

    public void HideThinkingIndicator()
    {
        _thinkingTimer?.Stop();
        ThinkingBorder.Visibility = Visibility.Collapsed;
    }

    public void ResetState()
    {
        _isStreaming = false;
        _showingSessionMessage = false;
        _currentAssistantText = string.Empty;
        _lastAssistantText = string.Empty;
        _currentStreamParagraph = null;
        _currentStreamRun = null;
        OutputDocument.Blocks.Clear();
    }

    public void ShowSessionMessage()
    {
        OutputDocument.Blocks.Clear();
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 4),
            Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor),
            FontFamily = Theme.TerminalFont,
            FontSize = Theme.TerminalFontSize
        };
        paragraph.Inlines.Add(new Run("  ✦ new session"));
        OutputDocument.Blocks.Add(paragraph);
        _showingSessionMessage = true;
        ScrollToEnd();
    }

    public void ReplayHistory(IReadOnlyList<AgentMessage> history)
    {
        ResetState();
        foreach (var message in history)
        {
            switch (message.Role)
            {
                case AgentMessageRole.User:
                    AppendUser(message.Text);
                    break;
                case AgentMessageRole.Assistant:
                    AppendMarkdown(message.Text + Environment.NewLine);
                    _lastAssistantText = message.Text;
                    break;
                case AgentMessageRole.Error:
                    AppendError(message.Text);
                    break;
                case AgentMessageRole.ToolUse:
                    AppendToolUseHistory(message.Text);
                    break;
                case AgentMessageRole.ToolResult:
                    AppendToolResult(message.Text, !message.Text.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
                    break;
            }
        }
    }

    public void AppendUser(string text)
    {
        EnsureTrailingNewline();
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 4),
            FontFamily = Theme.TerminalFont,
            FontSize = Theme.TerminalFontSize,
        };
        paragraph.Inlines.Add(new Run("> ")
        {
            Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor),
            FontWeight = FontWeights.SemiBold
        });
        paragraph.Inlines.Add(new Run(text)
        {
            Foreground = PopoverTheme.Brush(Theme.TerminalTextColor),
            FontWeight = FontWeights.SemiBold
        });
        OutputDocument.Blocks.Add(paragraph);
        ScrollToEnd();
    }

    public void AppendMarkdown(string markdown)
    {
        EndStreaming();
        var paragraph = RenderMarkdownParagraph(markdown);
        OutputDocument.Blocks.Add(paragraph);
        ScrollToEnd();
    }

    public void AppendStreamingText(string text)
    {
        if (!_isStreaming)
        {
            _isStreaming = true;
            _currentAssistantText = string.Empty;
            _currentStreamParagraph = new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 4),
                Foreground = PopoverTheme.Brush(Theme.TerminalTextColor),
                FontFamily = Theme.TerminalFont,
                FontSize = Theme.TerminalFontSize,
            };
            _currentStreamRun = new Run();
            _currentStreamParagraph.Inlines.Add(_currentStreamRun);
            OutputDocument.Blocks.Add(_currentStreamParagraph);
        }

        if (_currentAssistantText.Length == 0)
        {
            text = Regex.Replace(text, @"^\n+", string.Empty);
        }

        _currentAssistantText += text;
        if (_currentStreamRun != null)
        {
            _currentStreamRun.Text += text;
        }
        ScrollToEnd();
    }

    public void EndStreaming()
    {
        if (!_isStreaming) return;
        _isStreaming = false;

        if (_currentStreamRun != null && _currentStreamParagraph != null)
        {
            var fullText = _currentStreamRun.Text;
            OutputDocument.Blocks.Remove(_currentStreamParagraph);
            if (!string.IsNullOrWhiteSpace(fullText))
            {
                OutputDocument.Blocks.Add(RenderMarkdownParagraph(fullText));
                _lastAssistantText = _currentAssistantText;
            }
        }

        _currentAssistantText = string.Empty;
        _currentStreamParagraph = null;
        _currentStreamRun = null;
        ScrollToEnd();
    }

    public void AppendToolCall(string toolName, string? input)
    {
        EndStreaming();
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 2, 0, 2),
            FontFamily = Theme.TerminalFont,
            FontSize = Theme.TerminalFontSize
        };
        paragraph.Inlines.Add(new Run($"  {toolName.ToUpperInvariant()} ")
        {
            FontWeight = FontWeights.Bold,
            Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor)
        });
        if (!string.IsNullOrWhiteSpace(input))
        {
            var summary = input.Length > 280 ? input[..280] + "..." : input;
            paragraph.Inlines.Add(new Run(summary)
            {
                Foreground = PopoverTheme.Brush(Theme.TerminalSecondaryTextColor)
            });
        }
        OutputDocument.Blocks.Add(paragraph);
        ScrollToEnd();
    }

    public void AppendToolResult(string result, bool success)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 2, 0, 4),
            FontFamily = Theme.TerminalMonoFont,
            FontSize = Theme.TerminalCodeFontSize,
        };
        var prefix = success ? "  DONE " : "  FAIL ";
        var text = result.Length > 300 ? result[..300] + "..." : result;
        paragraph.Inlines.Add(new Run(prefix)
        {
            FontWeight = FontWeights.Bold,
            Foreground = PopoverTheme.Brush(success ? Theme.TerminalSuccessColor : Theme.TerminalErrorColor)
        });
        paragraph.Inlines.Add(new Run(text)
        {
            Foreground = PopoverTheme.Brush(Theme.TerminalSecondaryTextColor)
        });
        OutputDocument.Blocks.Add(paragraph);
        ScrollToEnd();
    }

    public void AppendError(string error)
    {
        if (!SessionOutputSanitizer.TrySanitizeLine(error, out var sanitizedError, 800))
        {
            return;
        }

        EndStreaming();
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 4),
            Foreground = PopoverTheme.Brush(Theme.TerminalErrorColor),
            FontFamily = Theme.TerminalFont,
            FontSize = Theme.TerminalFontSize,
        };
        paragraph.Inlines.Add(new Run(sanitizedError));
        OutputDocument.Blocks.Add(paragraph);
        ScrollToEnd();
    }

    public void AppendSystemMessage(string message)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 4),
            Foreground = PopoverTheme.Brush(Theme.TerminalSecondaryTextColor),
            FontFamily = Theme.TerminalFont,
            FontSize = Theme.TerminalFontSize - 1,
            FontStyle = FontStyles.Italic,
        };
        paragraph.Inlines.Add(new Run("  " + message));
        OutputDocument.Blocks.Add(paragraph);
        ScrollToEnd();
    }

    public void Clear()
    {
        EndStreaming();
        OutputDocument.Blocks.Clear();
    }

    public string GetAllText()
    {
        var range = new TextRange(OutputDocument.ContentStart, OutputDocument.ContentEnd);
        return range.Text;
    }

    public void CopyLastAssistantToClipboard()
    {
        var toCopy = string.IsNullOrWhiteSpace(_lastAssistantText) ? "nothing to copy yet" : _lastAssistantText;
        Clipboard.SetText(toCopy);
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 4),
            FontFamily = Theme.TerminalFont,
            FontSize = Theme.TerminalFontSize,
            Foreground = PopoverTheme.Brush(Theme.TerminalSuccessColor),
        };
        paragraph.Inlines.Add(new Run("  ✓ copied to clipboard"));
        OutputDocument.Blocks.Add(paragraph);
        ScrollToEnd();
    }

    public void FocusInput() => InputBox.Focus();

    private void SubmitInput()
    {
        var message = InputBox.Text.Trim();
        InputBox.Clear();

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (HandleSlashCommand(message))
        {
            return;
        }

        if (_showingSessionMessage)
        {
            OutputDocument.Blocks.Clear();
            _showingSessionMessage = false;
        }

        AppendUser(message);
        OnMessageSubmitted?.Invoke(message);
    }

    private bool HandleSlashCommand(string command)
    {
        var cmd = command.Trim().ToLowerInvariant();
        if (!cmd.StartsWith('/'))
        {
            return false;
        }

        switch (cmd)
        {
            case "/clear":
                ResetState();
                OnClearRequested?.Invoke();
                return true;

            case "/copy":
                var toCopy = string.IsNullOrWhiteSpace(_lastAssistantText) ? "nothing to copy yet" : _lastAssistantText;
                Clipboard.SetText(toCopy);
                AppendSystemMessage("✓ copied to clipboard");
                return true;

            case "/help":
                AppendSlashHelp();
                return true;

            case "/login":
                AppendSystemMessage(_provider.LoginHint());
                return true;

            default:
                AppendError($"unknown command: {command} (try /help)");
                return true;
        }
    }

    private Paragraph RenderMarkdownParagraph(string markdown)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 4),
            FontFamily = Theme.TerminalFont,
            FontSize = Theme.TerminalFontSize,
            Foreground = PopoverTheme.Brush(Theme.TerminalTextColor),
        };

        var lines = markdown.Split('\n');
        var inCodeBlock = false;
        var codeBlockContent = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    inCodeBlock = false;
                    paragraph.Inlines.Add(new InlineUIContainer(CreateCodeBlock(codeBlockContent.ToString().TrimEnd())));
                    paragraph.Inlines.Add(new LineBreak());
                    codeBlockContent.Clear();
                }
                else
                {
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeBlockContent.AppendLine(line);
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                paragraph.Inlines.Add(new Bold(new Run(line[4..])
                {
                    FontSize = Theme.TerminalFontSize + 1,
                    Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor)
                }));
                paragraph.Inlines.Add(new LineBreak());
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                paragraph.Inlines.Add(new Bold(new Run(line[3..])
                {
                    FontSize = Theme.TerminalFontSize + 2,
                    Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor)
                }));
                paragraph.Inlines.Add(new LineBreak());
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                paragraph.Inlines.Add(new Bold(new Run(line[2..])
                {
                    FontSize = Theme.TerminalFontSize + 3,
                    Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor)
                }));
                paragraph.Inlines.Add(new LineBreak());
                continue;
            }
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("* ", StringComparison.Ordinal))
            {
                var content = line.TrimStart()[2..];
                paragraph.Inlines.Add(new Run("  • ")
                {
                    Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor)
                });
                AddInlineMarkdown(paragraph, content);
                paragraph.Inlines.Add(new LineBreak());
                continue;
            }

            AddInlineMarkdown(paragraph, line);
            paragraph.Inlines.Add(new LineBreak());
        }

        return paragraph;
    }

    private void AddInlineMarkdown(Paragraph paragraph, string text)
    {
        const string pattern = @"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)|(\[(.+?)\]\((.+?)\))|(https?://[^\s>)]+)";
        var matches = Regex.Matches(text, pattern);
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                paragraph.Inlines.Add(new Run(text[lastIndex..match.Index]));
            }

            if (match.Groups[2].Success)
            {
                paragraph.Inlines.Add(new Bold(new Run(match.Groups[2].Value)));
            }
            else if (match.Groups[4].Success)
            {
                paragraph.Inlines.Add(new Italic(new Run(match.Groups[4].Value)));
            }
            else if (match.Groups[6].Success)
            {
                paragraph.Inlines.Add(new Run(match.Groups[6].Value)
                {
                    FontFamily = Theme.TerminalMonoFont,
                    FontSize = Theme.TerminalCodeFontSize,
                    Foreground = PopoverTheme.Brush(Theme.TerminalCodeTextColor),
                    Background = PopoverTheme.Brush(Theme.TerminalCodeBackground),
                });
            }
            else if (match.Groups[8].Success && match.Groups[9].Success)
            {
                var hyperlink = new Hyperlink(new Run(match.Groups[8].Value))
                {
                    Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor),
                    TextDecorations = null,
                };
                try
                {
                    hyperlink.NavigateUri = new Uri(match.Groups[9].Value);
                    hyperlink.RequestNavigate += (_, e) =>
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = e.Uri.AbsoluteUri,
                            UseShellExecute = true,
                        });
                    };
                }
                catch
                {
                    // Ignore invalid URIs.
                }
                paragraph.Inlines.Add(hyperlink);
            }
            else if (match.Groups[10].Success)
            {
                var hyperlink = new Hyperlink(new Run(match.Groups[10].Value))
                {
                    Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor),
                    TextDecorations = null,
                };
                try
                {
                    hyperlink.NavigateUri = new Uri(match.Groups[10].Value);
                    hyperlink.RequestNavigate += (_, e) =>
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = e.Uri.AbsoluteUri,
                            UseShellExecute = true,
                        });
                    };
                }
                catch
                {
                    // Ignore invalid URIs.
                }
                paragraph.Inlines.Add(hyperlink);
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[lastIndex..]));
        }
    }

    private Border CreateCodeBlock(string code)
    {
        var textBlock = new TextBlock
        {
            Text = code,
            FontFamily = Theme.TerminalMonoFont,
            FontSize = Theme.TerminalCodeFontSize,
            Foreground = PopoverTheme.Brush(Theme.TerminalCodeTextColor),
            TextWrapping = TextWrapping.Wrap,
        };

        return new Border
        {
            Background = PopoverTheme.Brush(Theme.TerminalCodeBackground),
            BorderBrush = PopoverTheme.Brush(Theme.TerminalToolCallBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 4),
            Child = textBlock,
        };
    }

    private void ScrollToEnd() => OutputBox.ScrollToEnd();

    private void EnsureTrailingNewline()
    {
        var lastBlock = OutputDocument.Blocks.LastBlock as Paragraph;
        if (lastBlock == null || lastBlock.Inlines.LastInline is LineBreak)
        {
            return;
        }

        if (OutputDocument.Blocks.Count > 0)
        {
            OutputDocument.Blocks.Add(new Paragraph
            {
                Margin = new Thickness(0),
            });
        }
    }

    private void AppendToolUseHistory(string entry)
    {
        var separator = entry.IndexOf(':');
        if (separator <= 0)
        {
            AppendToolCall("Tool", entry);
            return;
        }

        var tool = entry[..separator].Trim();
        var summary = entry[(separator + 1)..].Trim();
        AppendToolCall(string.IsNullOrWhiteSpace(tool) ? "Tool" : tool, summary);
    }

    private void AppendSlashHelp()
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 4),
            FontFamily = Theme.TerminalFont,
            FontSize = Theme.TerminalFontSize,
        };
        paragraph.Inlines.Add(new Run("  lil agents - slash commands\n")
        {
            FontWeight = FontWeights.Bold,
            Foreground = PopoverTheme.Brush(Theme.TerminalLinkColor)
        });
        paragraph.Inlines.Add(new Run("  /clear  ")
        {
            FontWeight = FontWeights.Bold,
            Foreground = PopoverTheme.Brush(Theme.TerminalTextColor)
        });
        paragraph.Inlines.Add(new Run("clear chat history\n")
        {
            Foreground = PopoverTheme.Brush(Theme.TerminalSecondaryTextColor)
        });
        paragraph.Inlines.Add(new Run("  /copy   ")
        {
            FontWeight = FontWeights.Bold,
            Foreground = PopoverTheme.Brush(Theme.TerminalTextColor)
        });
        paragraph.Inlines.Add(new Run("copy last response\n")
        {
            Foreground = PopoverTheme.Brush(Theme.TerminalSecondaryTextColor)
        });
        paragraph.Inlines.Add(new Run("  /help   ")
        {
            FontWeight = FontWeights.Bold,
            Foreground = PopoverTheme.Brush(Theme.TerminalTextColor)
        });
        paragraph.Inlines.Add(new Run("show this message")
        {
            Foreground = PopoverTheme.Brush(Theme.TerminalSecondaryTextColor)
        });
        paragraph.Inlines.Add(new Run("\n  /login  ")
        {
            FontWeight = FontWeights.Bold,
            Foreground = PopoverTheme.Brush(Theme.TerminalTextColor)
        });
        paragraph.Inlines.Add(new Run("show provider login hint")
        {
            Foreground = PopoverTheme.Brush(Theme.TerminalSecondaryTextColor)
        });
        OutputDocument.Blocks.Add(paragraph);
        ScrollToEnd();
    }

    private void UpdatePlaceholderVisibility()
    {
        PlaceholderText.Visibility =
            InputBox.IsEnabled && string.IsNullOrEmpty(InputBox.Text) && !InputBox.IsKeyboardFocused
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // allow newline
                return;
            }

            SubmitInput();
            e.Handled = true;
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePlaceholderVisibility();

    private void InputBox_FocusChanged(object sender, RoutedEventArgs e) => UpdatePlaceholderVisibility();

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;
        PopoverTheme.ThemeChanged -= ApplyTheme;
    }
}
