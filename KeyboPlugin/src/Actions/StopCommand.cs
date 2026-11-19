namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Threading.Tasks;

    // Tap presses Escape in whatever app is in front: it stops dictation or a running turn.
    // Hold to choose: Archive (the Claude session on screen, Cmd+Opt+A) or Never mind.

    public class StopCommand : ShortcutCommand
    {
        private readonly TapOrHold _buttons;
        private HoldMenu _menu;

        public StopCommand()
            : base("Cancel", "Tap to press Escape in the app in front: stops dictation or a running turn. Hold to archive the Claude session", KeyArt.CancelIcon)
        {
            this._buttons = new TapOrHold(
                tap: p => base.RunCommand(p),
                hold: _ => this._menu.Open(new HoldMenu.Choice(KeyArt.ArchiveIcon, "Archive", this.Archive)),
                letGo: _ => this._menu.Release());
        }

        protected override Boolean OnLoad()
        {
            this._menu = new HoldMenu(() => this.ActionImageChanged());
            return true;
        }

        protected override Boolean OnUnload()
        {
            this._menu?.Dispose();
            return true;
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent) =>
            this._buttons.Handle(actionParameter, buttonEvent);

        protected override void RunCommand(String actionParameter) => this._buttons.RunCommand(actionParameter);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            this._menu?.Current is HoldMenu.Choice choice ? KeyArt.HoldChoice(imageSize, choice) : base.GetCommandImage(actionParameter, imageSize);

        protected override void SendShortcut() =>
            DictateCommand.PressAndSoundIfStopping(() => this.Plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.Escape));

        // Cmd+Opt+A archives the session on screen, sent only once Claude is in front.
        private void Archive() => Task.Run(() =>
        {
            if (ClaudeApp.BringToFront())
            {
                this.Plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.KeyA, ModifierKey.Command | ModifierKey.Alt);
            }
        });
    }
}
