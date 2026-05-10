using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Exclr8.Terminal;
using Exclr8.Terminal.Pty;
using Porta.Pty;

// The whole sample, top to bottom: build an Avalonia app, drop a
// TerminalControl in a window, spawn the user's shell through the
// PtyTerminalAdapter, wire window-close to dispose. ~50 lines.

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<SampleApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
}

internal sealed class SampleApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var terminal = new TerminalControl();
            var window = new Window
            {
                Title   = "Exclr8.Terminal — SimpleTerminal sample",
                Width   = 900,
                Height  = 540,
                Content = terminal,
            };

            var adapter = new PtyTerminalAdapter(terminal);

            // Wait for the first Resized so we know the cell grid the
            // shell should think it has — spawning before that means the
            // shell paints into an 80x24 default and gets a SIGWINCH on
            // first paint, which some prompts don't redraw cleanly.
            void OnFirstSize(object? _, (int Cols, int Rows) size)
            {
                terminal.Resized -= OnFirstSize;
                _ = adapter.StartAsync(BuildOptions(size.Cols, size.Rows));
            }
            terminal.Resized += OnFirstSize;

            adapter.ProcessExited += (_, e) => Console.WriteLine($"[shell exited code={e.ExitCode}]");

            window.Closing += async (_, _) => await adapter.DisposeAsync();

            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static PtyOptions BuildOptions(int cols, int rows)
    {
        var isWindows = OperatingSystem.IsWindows();
        var shell = isWindows
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : Environment.GetEnvironmentVariable("SHELL")   ?? "/bin/sh";
        var cwd = Environment.GetEnvironmentVariable(isWindows ? "USERPROFILE" : "HOME") ?? "/";
        return new PtyOptions
        {
            Name        = "xterm-256color",
            App         = shell,
            Cwd         = cwd,
            Cols        = cols,
            Rows        = rows,
            CommandLine = Array.Empty<string>(),
            Environment = new Dictionary<string, string>
            {
                ["TERM"]     = "xterm-256color",
                ["COLORTERM"] = "truecolor",
            },
        };
    }
}
