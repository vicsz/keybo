namespace Loupedeck.KeyboPlugin
{
    using System;

    // Tap presses Return in whatever app is in front: it sends, or finishes dictation.

    public class GoCommand : ShortcutCommand
    {
        public GoCommand()
            : base("Return", "Presses Return in the app in front: sends, or finishes dictation", KeyArt.ReturnIcon)
        {
        }

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
