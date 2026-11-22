namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Globalization;

    // Shows how much of your Claude and Codex limits is left and whether you're on track. The ring starts full and
    // drains, the white tick is where an even pace would leave you today, and the colour is the forecast. In the red,
    // the footer says when you'd run out. The app's icon sits in the top-right corner. Tap to step through the limits
    // that have a reading. It checks every 5 minutes; tapping never asks the apps.

    public class UsageCommand : PluginDynamicCommand
    {
        public UsageCommand()
            : base(displayName: "Usage", description: "Your Claude and Codex usage and whether you're on track to stay within it. Tap to step through the 5-hour and weekly limits", groupName: Groups.Agents)
        {
            this.IsWidget = true;
        }

        private Usage Usage => ((KeyboPlugin)this.Plugin).Usage;

        protected override Boolean OnLoad()
        {
            this.Usage.Changed += this.Redraw;
            return true;
        }

        protected override Boolean OnUnload()
        {
            this.Usage.Changed -= this.Redraw;
            return true;
        }

        protected override void RunCommand(String actionParameter) => this.Usage.Next();

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            this.Usage.EnsureStarted();

            if (this.Usage.Shown is not (var feed, var reading))
            {
                return KeyArt.UsageMessage(imageSize, this.Usage.Problem ?? "Usage…");
            }

            var pace = feed.GetPace(reading);
            var left = Math.Max(0, reading.Limit - reading.Used);
            var amount = reading.IsDollars ? Dollars(left) : $"{left:0}%";
            var runsOut = RunsOut(reading, pace);
            var footer = runsOut ?? (pace.Tone == UsageTone.Stale ? Age(reading.FetchedAt)
                : reading.IsDollars ? $"left of {Dollars(reading.Limit)}" : $"left · {reading.Label}");

            return KeyArt.Usage(imageSize, left / reading.Limit, 1 - pace.TimeGone, pace.Tone, amount, footer, footerWarns: runsOut != null, app: feed.App);
        }

        // In the red, when you'd run out. It takes the footer's place, and its format still says which limit it is:
        // a time for 5 hours, a day for the week, a date for a month.
        private static String RunsOut(UsageReading reading, UsagePace pace) =>
            pace.Tone == UsageTone.Bad && pace.RunsOut is DateTime runsOut ? "out ~" + When(runsOut, reading.PeriodEnd - reading.PeriodStart) : null;

        // A month shows the date, a week the day and hour, five hours the time.
        private static String When(DateTime utc, TimeSpan period)
        {
            var local = utc.ToLocalTime();
            var format = period > TimeSpan.FromDays(8) ? "MMM d" : period > TimeSpan.FromDays(1) ? "ddd htt" : "h:mmtt";
            return local.ToString(format, CultureInfo.InvariantCulture).Replace("AM", "am").Replace("PM", "pm");
        }

        private static String Age(DateTime utc)
        {
            var age = DateTime.UtcNow - utc;
            return age.TotalHours >= 1 ? $"{(Int32)age.TotalHours}h old" : $"{(Int32)age.TotalMinutes}m old";
        }

        private static String Dollars(Double amount) => "$" + Math.Round(amount).ToString("0", CultureInfo.InvariantCulture);

        private void Redraw() => this.ActionImageChanged();
    }
}
