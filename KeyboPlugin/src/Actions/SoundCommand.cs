namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Threading;

    // The default sound output: its volume, grey while nothing plays and blue with rippling waves while an app plays
    // through it, and slashed "Muted". Muted while something is playing glows amber, since you're missing it. A tap mutes
    // or unmutes it. An output with no volume or mute to set, as on many displays, shows "Fixed".

    public class SoundCommand : StatCommand
    {
        private const Int32 FrameMs = 200;
        private static readonly TimeSpan HiddenAfter = TimeSpan.FromSeconds(2);

        private readonly Object _lock = new Object();
        private Timer _ripple;
        private Int32 _frame;
        private DateTime _lastDraw;

        public SoundCommand()
            : base("Sound", "The default sound output and its volume: blue while something plays, amber if you're muted while it does. Tap to mute or unmute", Groups.Tools)
        {
        }

        protected override Boolean OnUnload()
        {
            this.Ripple(false);
            return base.OnUnload();
        }

        protected override void RunCommand(String actionParameter)
        {
            var sound = this.Monitor.Sound;

            if (sound is { Exists: true, CanMute: true })
            {
                this.Monitor.SetSoundMuted(!sound.Muted);
            }
        }

        protected override BitmapImage Draw(PluginImageSize imageSize)
        {
            var sound = this.Monitor.Sound;
            var playing = sound is { Exists: true, InUse: true, Muted: false };
            var level = sound?.Volume is Single volume ? FormattableString.Invariant($"{volume * 100:0}%") : sound?.CanMute == true ? "On" : "Fixed";
            var (value, valueColor, color, waveColor) = sound switch
            {
                null or { Exists: false } => ("None", KeyArt.Muted, KeyArt.Dim, KeyArt.Dim),
                { Muted: true, InUse: true } => ("Muted", KeyArt.Amber, KeyArt.Amber, KeyArt.Dim),
                { Muted: true } => ("Muted", KeyArt.Muted, KeyArt.Dim, KeyArt.Dim),
                { InUse: true } => (level, KeyArt.Text, KeyArt.Blue, KeyArt.Blue),
                _ => (level, KeyArt.Text, KeyArt.Muted, KeyArt.Muted),
            };
            var missed = sound is { Exists: true, InUse: true, Muted: true };

            this._lastDraw = DateTime.UtcNow;
            this.Ripple(playing);

            var shapes = (missed ? KeyArt.Glow(KeyArt.Amber) : String.Empty) +
                KeyArt.Speaker(58, Layout.ZoneMiddle, color, waveColor, sound?.Muted == true, playing ? this._frame : null);
            return KeyArt.Key(imageSize, "Sound", shapes, new Line(value, Layout.Value, 28, valueColor, true));
        }

        // Steps the waves a few times a second while something plays, and stops once the key isn't being drawn.
        private void Ripple(Boolean on)
        {
            lock (this._lock)
            {
                if (on && this._ripple == null)
                {
                    this._ripple = new Timer(_ => this.Step(), null, FrameMs, FrameMs);
                }
                else if (!on && this._ripple != null)
                {
                    this._ripple.Dispose();
                    this._ripple = null;
                }
            }
        }

        private void Step()
        {
            if (DateTime.UtcNow - this._lastDraw > HiddenAfter)
            {
                this.Ripple(false);
                return;
            }

            Interlocked.Increment(ref this._frame);
            this.ActionImageChanged();
        }
    }
}
