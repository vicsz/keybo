namespace Loupedeck.KeyboPlugin
{
    using System;

    // Starts or stops dictation in the app in front, without switching.
    // In Claude it's Claude's mic (Cmd+D). Anywhere else it's ChatGPT's single-tap dictation shortcut,
    // which has to be set to Ctrl+Opt+Cmd+D in ChatGPT's settings.

    // While Claude or ChatGPT is recording (`Dictation`), faint red sound arcs ripple either side of the mic.

    public class DictateCommand : ShortcutCommand
    {
        private const String ChatGptDictation = Dictation.ChatGptProcess;

        public DictateCommand()
            : base("Dictate", "Starts or stops dictation: Claude's mic in Claude, ChatGPT's dictation anywhere else. The mic ripples while either is recording", KeyArt.MicIcon)
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
            this.Dictation.IsListening ? KeyArt.Listening(imageSize, "Dictate", this.Dictation.Frame) : base.GetCommandImage(actionParameter, imageSize);

        private void OnDictation() => this.ActionImageChanged();

        // Claude plays its own dictation sounds, but ChatGPT's dictation is silent, so the plugin plays them.

        // Stops ChatGPT's dictation if it's recording. Return uses this too, because Codex ignores Return while dictating.
        public static Boolean StopIfDictating(ClientApplication client)
        {
            if (!Audio.IsRecording(ChatGptDictation))
            {
                return false;
            }

            PressChatGptShortcut(client);
            Shell.PlaySound("Bottle");
            return true;
        }

        // For keys that end dictation as a side effect, like Escape: presses the key, then plays the stop sound if it was recording.
        public static void PressAndSoundIfStopping(Action press)
        {
            var stopping = Audio.IsRecording(ChatGptDictation);
            press();

            if (stopping)
            {
                Shell.PlaySound("Bottle");
            }
        }

        protected override void SendShortcut()
        {
            var client = this.Plugin.ClientApplication;

            if (ClaudeApp.IsFront(ClaudeApp.BundleId))
            {
                client.SendKeyboardShortcut(VirtualKeyCode.KeyD, ModifierKey.Command);
            }
            else if (!StopIfDictating(client))
            {
                // The start sound plays before the mic opens, so it never ends up in the text.
                Shell.PlaySound("Pop");
                PressChatGptShortcut(client);
            }
        }

        private static void PressChatGptShortcut(ClientApplication client) =>
            client.SendKeyboardShortcut(VirtualKeyCode.KeyD, ModifierKey.Control | ModifierKey.Alt | ModifierKey.Command);
    }
}
