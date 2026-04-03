# lil agents for Windows

![lil agents](hero-thumbnail.png)

Tiny AI companions that live on your Windows taskbar.

**Bruce** and **Jazz** walk back and forth above your taskbar. Click one to open an AI terminal. They walk, they think, they vibe.

Supports **Claude Code**, **OpenAI Codex**, **GitHub Copilot**, and **Google Gemini** CLIs — switch between them from the system tray.

## features

- Animated characters rendered from transparent sprite sheets
- Click a character to chat with AI in a themed popover terminal
- Switch between Claude, Codex, Copilot, and Gemini from the system tray menu
- Four visual themes: Peach, Midnight, Cloud, Moss
- Slash commands: `/clear`, `/copy`, `/help` in the chat input
- Copy last response button in the title bar
- Thinking bubbles with playful phrases while your agent works
- Sound effects on completion
- First-run onboarding with a friendly welcome

## requirements

- Windows 10 or Windows 11
- .NET 8.0 SDK (for building and running)
- At least one supported CLI installed:
  - [Claude Code](https://claude.ai/download) — `npm install -g @anthropic-ai/claude-code`
  - [OpenAI Codex](https://github.com/openai/codex) — `npm install -g @openai/codex`
  - [GitHub Copilot](https://github.com/github/copilot-cli) — `npm install -g @githubnext/github-copilot-cli`
  - [Google Gemini CLI](https://github.com/google-gemini/gemini-cli) — `npm install -g @google/gemini-cli`

## building

Open the `LilAgents.Windows.sln` in Visual Studio or run from the command line using the .NET SDK:

```bash
cd LilAgents.Windows
dotnet run
```

## privacy

lil agents runs entirely on your PC and sends no personal data anywhere.

- **Your data stays local.** The app plays bundled animations and calculates your taskbar size to position the characters. No project data, file paths, or personal information is collected or transmitted.
- **AI providers.** Conversations are handled entirely by the CLI process you choose (Claude, Codex, Copilot, or Gemini) running locally. lil agents does not intercept, store, or transmit your chat content. Any data sent to the provider is governed by their respective terms and privacy policies.
- **No accounts.** No login, no user database, no analytics in the app.

## license

MIT License. See [LICENSE](LICENSE) for details.
