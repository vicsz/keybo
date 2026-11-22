namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    // Codex's limits on your ChatGPT plan, from Codex's own app server: the Codex CLI inside the ChatGPT app (or one from
    // Homebrew) answers `account/rateLimits/read` over stdin and stdout. Codex handles its own sign-in, so Keybo never
    // sees it. All Codex usage knowledge stays here. Plans have a 5-hour and a weekly limit, like Claude's.
    // The app server is marked experimental, so a ChatGPT update can change it.

    public sealed class CodexUsage : UsageFeed
    {
        private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(20);

        private static readonly String[] CliPlaces =
        {
            "/Applications/ChatGPT.app/Contents/Resources/codex-cli/bin/codex",
            "/opt/homebrew/bin/codex",
            "/usr/local/bin/codex",
        };

        // It answers only while its input stays open, so these go in together and the reply to id 1 is read back.
        private static readonly String[] Requests =
        {
            "{\"method\":\"initialize\",\"id\":0,\"params\":{\"clientInfo\":{\"name\":\"keybo\",\"title\":\"Keybo\",\"version\":\"1\"}}}",
            "{\"method\":\"initialized\"}",
            "{\"method\":\"account/rateLimits/read\",\"id\":1}",
        };

        public CodexUsage(String dataDirectory)
            : base(dataDirectory, "codex-usage")
        {
        }

        public override String Name => "Codex";

        // ChatGPT's icon stands for Codex, as on session keys.
        public override String App => CodexThreads.BundleId;

        // Without a Codex CLI there's nothing to show and nothing wrong: the key just leaves Codex out.
        protected override (UsageReading Session, UsageReading Long) Fetch()
        {
            var cli = CliPlaces.FirstOrDefault(File.Exists);

            if (cli == null)
            {
                this.LogOnce("No Codex CLI, so Usage shows Claude only");
                return (null, null);
            }

            var answer = Shell.Ask(cli, AnswerWithin, Requests, IsReplyToRead, "app-server");

            if (answer == null)
            {
                this.Problem = "No usage";
                this.LogOnce("Codex's app server didn't answer about usage");
                return (null, null);
            }

            using var document = JsonDocument.Parse(answer);
            var root = document.RootElement;

            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("rateLimits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            {
                var code = root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var c) ? c.ToString() : "none";
                this.Problem = "No usage";
                this.LogOnce($"Codex usage call failed (error {code}). Is ChatGPT signed in?");
                return (null, null);
            }

            // Two windows, each the share used, its length and when it resets: 5 hours and a week on Plus.
            var now = DateTime.UtcNow;
            var windows = new[] { Window(limits, "primary", now), Window(limits, "secondary", now) }.Where(w => w != null).ToList();
            var session = windows.FirstOrDefault(w => w.PeriodEnd - w.PeriodStart <= TimeSpan.FromDays(1));
            var week = windows.FirstOrDefault(w => w.PeriodEnd - w.PeriodStart > TimeSpan.FromDays(1));

            if (session == null && week == null)
            {
                this.Problem = "No usage";
                this.LogOnce("Codex usage had no limit the key understands");
            }

            return (session, week);
        }

        // The reply to the rate-limits request, not a notification or a request from the server.
        private static Boolean IsReplyToRead(String line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                return root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("method", out _) &&
                    root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 1;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // `usedPercent`, `windowDurationMins` and `resetsAt` (Unix seconds). An unused window may have no reset time,
        // so it counts from now, like Claude's.
        private static UsageReading Window(JsonElement limits, String name, DateTime now)
        {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number ||
                !window.TryGetProperty("windowDurationMins", out var minutes) || minutes.ValueKind != JsonValueKind.Number ||
                minutes.GetInt64() <= 0)
            {
                return null;
            }

            var length = TimeSpan.FromMinutes(minutes.GetInt64());
            var end = window.TryGetProperty("resetsAt", out var resets) && resets.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(resets.GetInt64()).UtcDateTime
                : now + length;
            var label = length >= TimeSpan.FromDays(7) ? "week" : length > TimeSpan.FromDays(1) ? $"{length.TotalDays:0} days" : $"{length.TotalHours:0} hours";
            return new UsageReading(false, used.GetDouble(), 100, end - length, end, null, label, now);
        }
    }
}
