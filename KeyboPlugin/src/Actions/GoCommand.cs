namespace Loupedeck.KeyboPlugin
{
    using System;

    // Tap presses Return in whatever app is in front: it sends, or finishes dictation. While Claude or ChatGPT is
    // recording, a soft red dot pulses in the corner, in step with Dictate's arcs, since a tap will end it.

    public class GoCommand : ShortcutCommand
    {
        public GoCommand()
            : base("Return", "Presses Return in the app in front: sends, or finishes dictation. A red dot shows while dictating", KeyArt.ReturnIcon)
        {
        }

        private Dictation Dictation => ((KeyboPlugin)this.Plugin).Dictation;

        protected override Boolean OnLoad()
        {
            this.Dictation.Changed += this.OnDictation;
            return true;
        }

        protected override Boolean OnUnload()
        {
            this.Dictation.Changed -= this.OnDictation;
            return true;
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            this.Dictation.IsListening
                ? KeyArt.ControlWithDot(imageSize, KeyArt.ReturnIcon, "Return", this.Dictation.Frame)
                : base.GetCommandImage(actionParameter, imageSize);

        private void OnDictation() => this.ActionImageChanged();

        // While ChatGPT is dictating, Return stops it instead, like Return finishing dictation in Claude.
        protected override void SendShortcut()
        {
            if (!DictateCommand.StopIfDictating(this.Plugin.ClientApplication))
            {
                this.Plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.Return);
                ((KeyboPlugin)this.Plugin).OnMessageSent();
            }
        }
    }
}
