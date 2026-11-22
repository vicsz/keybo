namespace Loupedeck.KeyboPlugin
{
    using System;

    // The default microphone: grey "Ready" while it's free, red "Live" while an app records from it, with that app's
    // icon, and slashed "Muted". Muted while an app is still listening glows amber with the app's icon, since that app
    // isn't hearing you. A tap mutes or unmutes it.

    public class MicCommand : StatCommand
    {
        public MicCommand()
            : base("Mic", "The default microphone: red while an app records from it, amber if you're muted while it does. Tap to mute or unmute", Groups.Tools)
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            var mic = this.Monitor.Mic;

            if (mic is { Exists: true, CanMute: true })
            {
                this.Monitor.SetMicMuted(!mic.Muted);
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

            return KeyArt.Key(imageSize, "Mic", shapes, KeyArt.CornerIcon(listening ? this.Monitor.MicAppIcon : null),
                new Line(value, Layout.Value, 28, valueColor, true));
        }
    }
}
