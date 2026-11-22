namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;

    // The Usage key's shared part: each app's readings and pace, and which limit the key shows. Where each app's numbers
    // come from stays in ClaudeUsage.cs and CodexUsage.cs. Nothing starts until a Usage key is first drawn, so people who
    // don't place one never have a sign-in read; then it asks each app every 5 minutes. A tap steps through every limit
    // that has a reading, Claude's then Codex's. A longer limit heading for red takes over the key, whichever app it's
    // from, until a tap moves past it.

    public enum UsageTone { Good, Warn, Bad, Stale }

    // Used and Limit are dollars on accounts with a spend limit, otherwise percent of the plan limit (Limit = 100).
    public record UsageReading(Boolean IsDollars, Double Used, Double Limit, DateTime PeriodStart, DateTime PeriodEnd, String Severity, String Label, DateTime FetchedAt);

    // Where you stand against an even pace through the period: how much of it has gone, when you'd run out at this rate
    // (only if that's before it resets), and the forecast.
    public record UsagePace(Double TimeGone, DateTime? RunsOut, UsageTone Tone);

    public sealed class Usage : IDisposable
    {
        private static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(5);

        private readonly UsageFeed[] _feeds;
        private readonly Object _gate = new Object();
        private Timer _timer;
        private Int32 _polling;
        private View _pick;

        public Usage(String dataDirectory)
        {
            this._feeds = new UsageFeed[] { new ClaudeUsage(dataDirectory), new CodexUsage(dataDirectory) };
            this._pick = new View(this._feeds[0], false);
        }

        public event Action Changed;

        // A short reason the key can show when no app has a reading, like "Sign in to CLI".
        public String Problem => this._feeds.Select(f => f.Problem).FirstOrDefault(p => p != null);

        // What the key shows: a longer limit heading for red that no tap has moved past, otherwise the one you picked.
        // Null while nothing has a reading.
        public (UsageFeed Feed, UsageReading Reading)? Shown
        {
            get
            {
                var (views, pick, red) = this.Look();
                var shown = red ?? pick;
                return views.Count == 0 ? null : (shown.Feed, shown.Reading);
            }
        }

        public void EnsureStarted()
        {
            lock (this._gate)
            {
                if (this._timer != null)
                {
                    return;
                }

                foreach (var feed in this._feeds)
                {
                    feed.LoadSaved();
                }

                this._timer = new Timer(_ => this.Poll(), null, TimeSpan.Zero, PollEvery);
            }
        }

        // A tap. A red limit that took over steps aside for the one you picked; otherwise the key moves on to the next
        // limit. A red limit a tap moves past stays quiet until its next period. Tapping never asks the apps.
        public void Next()
        {
            var (views, pick, red) = this.Look();

            if (red is View taken)
            {
                taken.Feed.QuietRed = taken.Reading.PeriodStart;
            }
            else if (views.Count > 0)
            {
                if (pick.TakesOver)
                {
                    pick.Feed.QuietRed = pick.Reading.PeriodStart;
                }

                this._pick = views[(views.IndexOf(pick) + 1) % views.Count];
            }

            this.Changed?.Invoke();
        }

        public void Dispose() => this._timer?.Dispose();

        // Every limit with a reading, in order (each app's 5 hours, then its longer limit); the one you picked, or the
        // first there is if yours has none; and a red limit taking over from it.
        private (List<View> Views, View Pick, View? Red) Look()
        {
            var views = this._feeds.SelectMany(f => new[] { new View(f, false), new View(f, true) }).Where(v => v.Reading != null).ToList();
            var pick = views.Contains(this._pick) ? this._pick : views.FirstOrDefault();
            var red = views.Where(v => v != pick && v.TakesOver).Select(v => (View?)v).FirstOrDefault();
            return (views, pick, red);
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref this._polling, 1) == 1)
            {
                return;
            }

            try
            {
                foreach (var feed in this._feeds)
                {
                    feed.Poll();
                    this.Changed?.Invoke();
                }
            }
            finally
            {
                Interlocked.Exchange(ref this._polling, 0);
            }
        }

        // One limit the key can show: an app's 5 hours, or its longer limit.
        private readonly record struct View(UsageFeed Feed, Boolean Longer)
        {
            public UsageReading Reading => this.Longer ? this.Feed.Long : this.Feed.Session;

            public Boolean TakesOver => this.Longer && this.Reading is { } reading &&
                this.Feed.GetPace(reading).Tone == UsageTone.Bad && this.Feed.QuietRed != reading.PeriodStart;
        }
    }

    // One app's limits: its latest readings, saved so a reload shows them, and a month of history so the pace can use
    // your recent rate rather than the period's average.
    public abstract class UsageFeed
    {
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan KeepHistory = TimeSpan.FromDays(31);

        private readonly String _dataDirectory;
        private readonly String _historyPath;
        private readonly String _latestPath;
        private String _lastLogged;
        private List<(DateTime At, Double Used, DateTime PeriodStart)> _history = new List<(DateTime, Double, DateTime)>();

        protected UsageFeed(String dataDirectory, String fileName)
        {
            this._dataDirectory = dataDirectory;
            this._historyPath = Path.Combine(dataDirectory, fileName + "-history.json");
            this._latestPath = Path.Combine(dataDirectory, fileName + "-latest.json");
        }

        // The app's name for the log, and its bundle ID for the icon on the key.
        public abstract String Name { get; }

        public abstract String App { get; }

        // The 5-hour limit, and the longer one: the week, or the month on spend-limit accounts. Null when there's none.
        public UsageReading Session { get; private set; }

        public UsageReading Long { get; private set; }

        // A short reason the key can show when no app has a reading, like "Sign in to CLI".
        public String Problem { get; protected set; }

        // The period of a longer limit whose red a tap has moved past, so it doesn't take over again until the next one.
        public DateTime QuietRed { get; set; }

        // The last good readings and the history, so a reload shows them (greyed out once old) instead of nothing.
        public void LoadSaved()
        {
            this._history = this.LoadHistory();

            try
            {
                if (File.Exists(this._latestPath))
                {
                    var saved = JsonSerializer.Deserialize<UsageReading[]>(File.ReadAllText(this._latestPath));
                    this.Session = saved?.ElementAtOrDefault(0);
                    this.Long = saved?.ElementAtOrDefault(1);
                }
            }
            catch (Exception ex)
            {
                this.LogOnce($"Could not read the last {this.Name} usage: " + ex.Message);
            }
        }

        // Asks the app once. When it can't answer, the last readings stay, greyed out once old.
        public void Poll()
        {
            try
            {
                var (session, longer) = this.Fetch();

                if (session == null && longer == null)
                {
                    return;
                }

                this.Session = session;
                this.Long = longer;
                this.Problem = null;

                if (longer != null)
                {
                    this.Remember(longer);
                }

                this.SaveLatest();
                this.LogOnce($"{this.Name} usage: {Describe(longer)}{(session != null ? ", " + Describe(session) : String.Empty)}");
            }
            catch (Exception ex)
            {
                this.Problem = "No usage";
                this.LogOnce($"Could not read {this.Name} usage: " + ex.Message);
            }
        }

        public UsagePace GetPace(UsageReading reading)
        {
            var now = DateTime.UtcNow;
            var period = reading.PeriodEnd - reading.PeriodStart;
            var timeGone = Math.Clamp((now - reading.PeriodStart) / period, 0, 1);
            var ahead = reading.Used - (reading.Limit * timeGone);
            var rate = this.RatePerHour(reading, now);
            DateTime? runsOut = reading.Used >= reading.Limit ? now
                : rate > 0 ? now.AddHours((reading.Limit - reading.Used) / rate) : null;

            UsageTone tone;

            if (now - reading.FetchedAt > StaleAfter)
            {
                tone = UsageTone.Stale;
            }
            else if (runsOut != null && runsOut < reading.PeriodEnd - (period * 0.1))
            {
                tone = UsageTone.Bad;
            }
            else if (ahead > 0 && timeGone > 0.03)
            {
                tone = UsageTone.Warn;
            }
            else
            {
                tone = UsageTone.Good;
            }

            // Never show calmer than the app does.
            if (tone != UsageTone.Stale && reading.Severity is not (null or "normal"))
            {
                var floor = reading.Severity == "warning" ? UsageTone.Warn : UsageTone.Bad;
                tone = (UsageTone)Math.Max((Int32)tone, (Int32)floor);
            }

            return new UsagePace(timeGone, runsOut < reading.PeriodEnd ? runsOut : null, tone);
        }

        // The app's own call: its 5-hour and longer readings. With neither, it sets Problem when that's worth showing.
        protected abstract (UsageReading Session, UsageReading Long) Fetch();

        protected void LogOnce(String message)
        {
            if (message != this._lastLogged)
            {
                this._lastLogged = message;
                PluginLog.Info(message);
            }
        }

        private static String Describe(UsageReading reading) =>
            reading == null ? "none" : $"{reading.Used:0.##} of {reading.Limit:0.##} ({reading.Label})";

        // The recent rate: the last 7 days of this period when there's enough history, otherwise the average so far.
        // Five hours is too short for history to help, so it always uses the average.
        private Double RatePerHour(UsageReading reading, DateTime now)
        {
            var hours = (now - reading.PeriodStart).TotalHours;

            if (reading.PeriodEnd - reading.PeriodStart <= TimeSpan.FromDays(1))
            {
                return hours > 0 ? reading.Used / hours : 0;
            }

            var since = now - TimeSpan.FromDays(7);
            var minimumSpan = (reading.PeriodEnd - reading.PeriodStart) * 0.1;
            var oldest = this._history
                .Where(h => h.PeriodStart == reading.PeriodStart && h.At >= since)
                .OrderBy(h => h.At)
                .FirstOrDefault();

            if (oldest.At != default && now - oldest.At >= minimumSpan)
            {
                return Math.Max(0, reading.Used - oldest.Used) / (now - oldest.At).TotalHours;
            }

            return hours > 0 ? reading.Used / hours : 0;
        }

        // One amount every poll, kept for a month. The list is replaced rather than changed, so a key drawing meanwhile
        // never sees it half-written.
        private void Remember(UsageReading reading)
        {
            var cutoff = DateTime.UtcNow - KeepHistory;
            this._history = this._history.Where(h => h.At >= cutoff).Append((reading.FetchedAt, reading.Used, reading.PeriodStart)).ToList();

            try
            {
                Directory.CreateDirectory(this._dataDirectory);
                var rows = this._history.Select(h => new[] { new DateTimeOffset(h.At).ToUnixTimeSeconds(), h.Used, new DateTimeOffset(h.PeriodStart).ToUnixTimeSeconds() });
                var temporary = this._historyPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(rows));
                File.Move(temporary, this._historyPath, overwrite: true);
            }
            catch (IOException ex)
            {
                this.LogOnce($"Could not save {this.Name} usage history: " + ex.Message);
            }
        }

        private void SaveLatest()
        {
            try
            {
                Directory.CreateDirectory(this._dataDirectory);
                var temporary = this._latestPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new[] { this.Session, this.Long }));
                File.Move(temporary, this._latestPath, overwrite: true);
            }
            catch (Exception ex)
            {
                this.LogOnce($"Could not save the last {this.Name} usage: " + ex.Message);
            }
        }

        private List<(DateTime At, Double Used, DateTime PeriodStart)> LoadHistory()
        {
            try
            {
                var rows = JsonSerializer.Deserialize<Double[][]>(File.ReadAllText(this._historyPath));
                return rows.Where(r => r.Length == 3)
                    .Select(r => (DateTimeOffset.FromUnixTimeSeconds((Int64)r[0]).UtcDateTime, r[1], DateTimeOffset.FromUnixTimeSeconds((Int64)r[2]).UtcDateTime))
                    .ToList();
            }
            catch (Exception)
            {
                return new List<(DateTime, Double, DateTime)>();
            }
        }
    }
}
