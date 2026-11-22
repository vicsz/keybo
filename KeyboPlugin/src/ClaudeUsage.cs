namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;

    // Claude's limits, from the same server call /usage makes, using the Claude CLI's sign-in. All Claude usage knowledge
    // stays here. The sign-in is read from the Keychain each time, kept only in memory, and never logged. When it has
    // expired, running `claude -p /usage` makes the CLI refresh it (free, no model call).
    // Plans have a 5-hour and a weekly limit. Spend-limit accounts have the month only.

    public sealed class ClaudeUsage : UsageFeed
    {
        private const String UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private static readonly TimeSpan CliRefreshCooldown = TimeSpan.FromMinutes(30);
        private static readonly String Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private static readonly String[] CliPlaces =
        {
            "/opt/homebrew/bin/claude",
            "/usr/local/bin/claude",
            Path.Combine(Home, ".local", "bin", "claude"),
            Path.Combine(Home, ".claude", "local", "claude"),
            Path.Combine(Home, ".npm-global", "bin", "claude"),
        };

        private DateTime _lastCliRefresh = DateTime.MinValue;

        public ClaudeUsage(String dataDirectory)
            : base(dataDirectory, "usage")
        {
        }

        public override String Name => "Claude";

        public override String App => ClaudeApp.BundleId;

        protected override (UsageReading Session, UsageReading Long) Fetch()
        {
            var signIn = ReadSignIn();

            if (signIn == null)
            {
                this.Problem = "Sign in to CLI";
                this.LogOnce("No Claude CLI sign-in in the Keychain. Run `claude auth login`.");
                return (null, null);
            }

            if (signIn.Value.ExpiresAt <= DateTime.UtcNow && this.RefreshThroughCli())
            {
                signIn = ReadSignIn() ?? signIn;
            }

            var (status, body) = Call(signIn.Value.Token);

            if (status == HttpStatusCode.Unauthorized && this.RefreshThroughCli())
            {
                signIn = ReadSignIn();
                (status, body) = signIn == null ? (status, body) : Call(signIn.Value.Token);
            }

            if (status != HttpStatusCode.OK)
            {
                this.Problem = status != HttpStatusCode.Unauthorized ? "No usage"
                    : this.Problem == "No Claude CLI" ? this.Problem : "Sign in to CLI";
                this.LogOnce($"Claude usage call returned {(Int32)status}");
                return (null, null);
            }

            var reading = Parse(body);

            if (reading.Session == null && reading.Long == null)
            {
                this.Problem = "No usage";
                this.LogOnce("Claude usage had no spend limit or plan limit the key understands");
            }

            return reading;
        }

        private static (HttpStatusCode Status, String Body) Call(String token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("claude-keys");

            using var response = Http.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            return (response.StatusCode, reader.ReadToEnd());
        }

        // Accounts with a spend limit (like enterprise seats) get dollars for the calendar month, which resets
        // at midnight UTC on the 1st. Plans get their 5-hour limit and their weekly limit for all models.
        private static (UsageReading Session, UsageReading Long) Parse(String body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var now = DateTime.UtcNow;

            if (root.TryGetProperty("spend", out var spend) && spend.ValueKind == JsonValueKind.Object &&
                Money(spend, "limit") is Double limit && limit > 0)
            {
                var used = Money(spend, "used") ?? 0;
                var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                return (null, new UsageReading(true, used, limit, start, start.AddMonths(1), Text(spend, "severity"), "month", now));
            }

            if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            {
                return (null, null);
            }

            var all = limits.EnumerateArray().ToList();
            var fiveHours = TimeSpan.FromHours(5);
            var week = Limit(all, "weekly_all", TimeSpan.FromDays(7), "week", now);

            // Once a 5-hour window ends with no new use there's no reset time, so it shows as an unused window from now.
            var session = Limit(all, "session", fiveHours, "5 hours", now)
                ?? (week == null ? null : new UsageReading(false, 0, 100, now, now + fiveHours, null, "5 hours", now));

            return (session, week);
        }

        private static UsageReading Limit(List<JsonElement> limits, String kind, TimeSpan length, String label, DateTime now)
        {
            var limit = limits.FirstOrDefault(l => Text(l, "kind") == kind);

            if (limit.ValueKind != JsonValueKind.Object ||
                !DateTime.TryParse(Text(limit, "resets_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var end))
            {
                return null;
            }

            var percent = limit.TryGetProperty("percent", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
            return new UsageReading(false, percent, 100, end - length, end, Text(limit, "severity"), label, now);
        }

        // Amounts come as minor units with an exponent: 7518 with exponent 2 is $75.18.
        private static Double? Money(JsonElement parent, String name)
        {
            if (!parent.TryGetProperty(name, out var money) || money.ValueKind != JsonValueKind.Object ||
                !money.TryGetProperty("amount_minor", out var amount) || amount.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            var exponent = money.TryGetProperty("exponent", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 2;
            return amount.GetDouble() / Math.Pow(10, exponent);
        }

        private static String Text(JsonElement parent, String name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static (String Token, DateTime ExpiresAt)? ReadSignIn()
        {
            var json = Run("/usr/bin/security", TimeSpan.FromSeconds(10), "find-generic-password", "-s", "Claude Code-credentials", "-w");

            if (String.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var oauth = document.RootElement.GetProperty("claudeAiOauth");
                var token = oauth.GetProperty("accessToken").GetString();
                var expires = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64()).UtcDateTime
                    : DateTime.MaxValue;
                return String.IsNullOrEmpty(token) ? null : (token, expires);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // What the command printed, or null if it failed or timed out.
        private static String Run(String command, TimeSpan timeout, params String[] arguments) =>
            Shell.Run(command, timeout, arguments) is { ExitCode: 0 } result ? result.Output : null;

        // `claude -p /usage` refreshes the CLI's own sign-in without calling a model. Limited to once per half hour.
        private Boolean RefreshThroughCli()
        {
            if (DateTime.UtcNow - this._lastCliRefresh < CliRefreshCooldown)
            {
                return false;
            }

            this._lastCliRefresh = DateTime.UtcNow;
            var cli = CliPlaces.FirstOrDefault(File.Exists);

            if (cli == null)
            {
                this.Problem = "No Claude CLI";
                this.LogOnce("Claude usage needs the Claude CLI to refresh its sign-in, and it wasn't found");
                return false;
            }

            Run(cli, TimeSpan.FromSeconds(60), "-p", "/usage");
            PluginLog.Info("Refreshed the Claude CLI sign-in for usage");
            return true;
        }
    }
}
