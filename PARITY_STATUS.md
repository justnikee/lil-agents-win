# Windows Parity Status

## Automated checks completed

- Project builds successfully.
- Sprite resources are bundled to output and load at runtime.
- Sound resources are bundled to output and load at runtime.
- App launches and remains running during smoke test.

## Feature parity completed

- Character movement timing and easing match `WalkerCharacter.swift`.
- Per-pixel click hit-testing with transparency and facing flip support.
- Onboarding bubble and onboarding popover flow.
- Thinking/completion bubble behavior and sound effects.
- Provider switching (tray + popover), session reset, slash commands.
- Theme switching and dynamic theme propagation.
- Character visibility toggle, size toggle, and persistence.
- Display pinning menu (`Auto (Main Display)` + per-display options) and persistence.

## Remaining gaps for true 100% parity

- Exact visual alpha parity for character render:
  - Windows currently uses keyed PNG extraction from `.mov`.
  - Source videos decode as `yuv420p` (no alpha channel), so transparency is approximate.
  - To reach exact parity, export alpha-correct frame assets from the mac source pipeline and use them directly.

- Sparkle-equivalent update system:
  - Windows now has `Check for Updates...` in tray, but it opens the project website.
  - Full parity would require a real Windows update mechanism (version feed + installer update flow).

- Pinned-display behavior on uncommon taskbar layouts:
  - Current pinned mode assumes a bottom walk baseline on selected display.
  - For full parity on top/left/right taskbars per monitor, taskbar geometry should be resolved per selected display.

## Manual QA checklist

1. Launch app and confirm Bruce/Jazz appear above taskbar.
2. Click transparent space around character -> click should pass through.
3. Click opaque sprite pixels -> popover opens.
4. Close popover, wait for walk cycle, confirm flip direction and click accuracy both directions.
5. Trigger agent response -> thinking bubble appears when popover is closed.
6. On completion -> completion bubble and sound play.
7. Tray `Provider` change -> both characters use new provider.
8. Tray `Size` change -> both characters resize without broken positioning.
9. Tray `Display` pinning -> switching displays repositions walk region and persists after restart.
10. Tray `Style` change -> open popover and bubble both update theme.
11. Tray `Sounds` toggle -> completion sound respects toggle.
12. Popover commands:
   - `/help`
   - `/copy`
   - `/clear`
13. `Check for Updates...` opens browser.
14. Restart app and verify settings persistence (provider, size, style, sounds, display).
