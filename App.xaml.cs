using System.Windows;

namespace LilAgents.Windows;

/// <summary>
/// Application entry point — runs as a tray-only app (no main window).
/// Ported from LilAgentsApp.swift.
/// </summary>
public partial class App : Application
{
    private LilAgentsController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // No main window — we run from the system tray
        _controller = new LilAgentsController();

        try
        {
            _controller.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to initialize Lil Agents:\n\n{ex.Message}",
                "Lil Agents Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}
