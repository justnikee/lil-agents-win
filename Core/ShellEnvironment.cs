using System.Diagnostics;
using System.IO;

namespace LilAgents.Windows.Core;

/// <summary>
/// Utility for discovering CLI binary paths and setting up process environments.
/// Ported from ShellEnvironment.swift — adapted for Windows.
/// </summary>
public static class ShellEnvironment
{
    private static readonly string[] AdditionalSearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
        @"C:\Program Files\Git\cmd",
        @"C:\Program Files\Git\bin",
    ];

    /// <summary>
    /// Finds a binary by name, searching PATH and common install locations.
    /// Returns the full path or null if not found.
    /// </summary>
    public static string? FindBinary(string name)
    {
        // Add .exe if no extension
        var exeName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";

        // 1. Check PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var paths = pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in paths)
        {
            var fullPath = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(fullPath)) return fullPath;

            // Also check .cmd and .bat (npm installs these)
            var cmdPath = Path.ChangeExtension(fullPath, ".cmd");
            if (File.Exists(cmdPath)) return cmdPath;
        }

        // 2. Check additional known locations
        foreach (var dir in AdditionalSearchPaths)
        {
            if (!Directory.Exists(dir)) continue;
            var fullPath = Path.Combine(dir, exeName);
            if (File.Exists(fullPath)) return fullPath;

            var cmdPath = Path.ChangeExtension(fullPath, ".cmd");
            if (File.Exists(cmdPath)) return cmdPath;
        }

        // 3. Try `where.exe` as last resort
        try
        {
            var psi = new ProcessStartInfo("where.exe", exeName)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadLine();
                proc.WaitForExit(2000);
                if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
                    return output;
            }
        }
        catch { /* ignore */ }

        return null;
    }

    /// <summary>
    /// Builds a clean environment dictionary for launching CLI processes.
    /// Removes CLAUDECODE environment variables to prevent nested session detection.
    /// </summary>
    public static Dictionary<string, string> GetProcessEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString() ?? "";
            var value = entry.Value?.ToString() ?? "";

            // Filter out Claude Code nesting variables
            if (key.StartsWith("CLAUDE_CODE", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CLAUDECODE", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("CLAUDE_CODE_ENTRYPOINT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            env[key] = value;
        }

        // Ensure PATH includes common tool locations
        if (env.TryGetValue("PATH", out var currentPath))
        {
            var extraPaths = string.Join(";", AdditionalSearchPaths.Where(Directory.Exists));
            if (!string.IsNullOrEmpty(extraPaths))
            {
                env["PATH"] = currentPath + ";" + extraPaths;
            }
        }

        return env;
    }

    /// <summary>
    /// Gets the current working directory — defaults to user profile.
    /// </summary>
    public static string GetWorkingDirectory()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
