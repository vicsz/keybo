namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;

    // Starting the macOS tools the plugin leans on: open, afplay, lsappinfo, ps, sqlite3, security and the Claude CLI.
    // What a command prints is never logged: the Usage key reads its sign-in token this way.

    public static class Shell
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        public record Result(Int32 ExitCode, String Output, String Error);

        // Runs a command to the end. One that runs past the timeout is stopped and gets exit code -1.
        // Throws only if it can't start at all.
        public static Result Run(String command, params String[] arguments) => Run(command, DefaultTimeout, arguments);

        public static Result Run(String command, TimeSpan timeout, params String[] arguments)
        {
            var start = Info(command, arguments);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;

            using var process = Process.Start(start);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It finished just now.
                }

                return new Result(-1, String.Empty, "timed out");
            }

            return new Result(process.ExitCode, output.Result, error.Result);
        }

        // Starts a command without waiting for it. Logs, rather than throws, if it can't start.
        public static void Start(String command, params String[] arguments)
        {
            try
            {
                Process.Start(Info(command, arguments))?.Dispose();
            }
            catch (Win32Exception ex)
            {
                PluginLog.Warning(ex, $"Couldn't start {Path.GetFileName(command)}");
            }
        }

        // Plays a built-in macOS sound without waiting for it to finish.
        public static void PlaySound(String name) => Start("/usr/bin/afplay", $"/System/Library/Sounds/{name}.aiff");

        // Homebrew's folders are on PATH so the Claude CLI finds node, and commands run in the temp folder.
        private static ProcessStartInfo Info(String command, String[] arguments)
        {
            var start = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            };

            start.Environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin";

            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            return start;
        }
    }
}
