namespace Loupedeck.KeyboPlugin
{
    using System;

    // The mic an app is recording from, else the default, named on the label: grey "Ready" while it's free, red "Live"
    // while an app records from it, with that app's icon, and slashed "Muted". Muted while an app is still listening glows
    // amber, since that app isn't hearing you. A tap mutes or unmutes that mic; one with neither a mute switch nor a volume
    // (an iPhone's) says "No mute" for a moment instead.

    public class MicCommand : StatCommand
    {
        private static readonly TimeSpan NoMuteFor = TimeSpan.FromSeconds(2);

        private DateTime _noMuteUntil;

        public MicCommand()
            : base("Mic", "The mic in use, by name: red while an app records from it, amber if you're muted while it does. Tap to mute or unmute", Groups.Tools)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            var mic = this.Monitor.Mic;

            if (mic is { Exists: true, CanMute: true })
            {
                this.Monitor.SetMicMuted(!mic.Muted);
            }
            else if (mic is { Exists: true })
            {
                this._noMuteUntil = DateTime.UtcNow + NoMuteFor;
                this.ActionImageChanged();
            }
        }

        protected override BitmapImage Draw(PluginImageSize imageSize)
        {
            var mic = this.Monitor.Mic;
            var listening = mic is { Exists: true, InUse: true };
            var (value, valueColor, iconColor, muted) = mic switch
            {
                null or { Exists: false } => ("None", KeyArt.Muted, KeyArt.Dim, false),
                { Muted: true, InUse: true } => ("Muted", KeyArt.Amber, KeyArt.Amber, true),
                { Muted: true } => ("Muted", KeyArt.Muted, KeyArt.Dim, true),
                { InUse: true } => ("Live", KeyArt.Red, KeyArt.Red, false),
                _ => ("Ready", KeyArt.Muted, KeyArt.Muted, false),
            };
            var shapes = (listening && muted ? KeyArt.Glow(KeyArt.Amber) : String.Empty) + KeyArt.Microphone(58, Layout.ZoneMiddle, iconColor, muted);
            var icon = KeyArt.CornerIcon(listening ? this.Monitor.MicAppIcon : null);
            var line = DateTime.UtcNow < this._noMuteUntil
                ? new Line("No mute", Layout.SmallValue, 26, KeyArt.Amber, true)
                : new Line(value, Layout.Value, 28, valueColor, true);

            return KeyArt.Key(imageSize, null, shapes, icon, KeyArt.DeviceLabel(mic?.Name, "Mic", icon != null), line);
        }
    }
}
