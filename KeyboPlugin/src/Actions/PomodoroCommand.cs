namespace Loupedeck.KeyboPlugin
{
    using System;

    // The Pomodoro key. Tap to start, tap to pause, tap to carry on; hold to reset. The ring around the tomato fills as
    // time runs, amber for the last 2 minutes. When it's full the key glows green and stays Done until tapped.
    // The timer itself lives in Pomodoro, on the plugin, so Keybo can watch it too.

    public class PomodoroCommand : PluginDynamicCommand
    {
        private static readonly TimeSpan Warning = TimeSpan.FromMinutes(2);

        private readonly TapOrHold _buttons;

        public PomodoroCommand()
            : base("Pomodoro", "A 25-minute timer. Tap to start or pause, hold to reset", Groups.Tools)
        {
            this.IsWidget = true;
            this._buttons = new TapOrHold(_ => this.Pomodoro.Tap(), _ => this.Pomodoro.Reset());
        }

        private Pomodoro Pomodoro => ((KeyboPlugin)this.Plugin).Pomodoro;

        protected override Boolean OnLoad()
        {
            this.Pomodoro.Changed += this.Redraw;
            return true;
        }

        protected override Boolean OnUnload()
        {
            this.Pomodoro.Changed -= this.Redraw;
            return true;
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent) =>
            this._buttons.Handle(actionParameter, buttonEvent);

        protected override void RunCommand(String actionParameter) => this._buttons.RunCommand(actionParameter);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var (done, running, left) = this.Pomodoro.State;
            var y = Layout.ZoneMiddle;

            if (done)
            {
                return KeyArt.Key(imageSize, "Pomodoro", KeyArt.Glow(KeyArt.Green) + KeyArt.Ring(58, y, 19, 1, KeyArt.Green) + KeyArt.Tomato(58, y, KeyArt.Red, KeyArt.Green),
                    new Line("Done", Layout.Value, 28, KeyArt.Green, true));
            }

            var started = running || left < Pomodoro.Length;
            var filled = 1 - (left / Pomodoro.Length);

            // Ready is all grey; paused keeps its ring but greys it; running is green, amber near the end.
            var (ringColor, textColor) = !running ? (KeyArt.Muted, KeyArt.Muted) : left <= Warning ? (KeyArt.Amber, KeyArt.Amber) : (KeyArt.Green, KeyArt.Text);
            var tomato = started ? KeyArt.Tomato(58, y, KeyArt.Red, KeyArt.Green) : KeyArt.Tomato(58, y, KeyArt.Dim, KeyArt.Muted);

            return KeyArt.Key(imageSize, "Pomodoro", KeyArt.Ring(58, y, 19, filled, ringColor) + tomato,
                new Line(Clock(left), Layout.Value, 28, textColor, true));
        }

        private void Redraw() => this.ActionImageChanged();

        // Minutes and seconds, rounded up so the timer shows 25:00 at the start and 0:01 just before the end.
        private static String Clock(TimeSpan time)
        {
            var seconds = (Int32)Math.Ceiling(time.TotalSeconds);
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }
}
