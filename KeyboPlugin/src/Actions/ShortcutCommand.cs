namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Threading.Tasks;

    // A key that presses a keyboard shortcut in whatever app is in front: Dictate, Return and Cancel. Presses closer
    // together than 300 ms count once, and the shortcut is sent off the keypad's thread.

    public abstract class ShortcutCommand : PluginDynamicCommand
    {
        private static readonly TimeSpan RepeatPressWindow = TimeSpan.FromMilliseconds(300);

        private readonly String _label;
        private readonly String _icon;
        private DateTime _lastPress = DateTime.MinValue;

        protected ShortcutCommand(String label, String description, String icon)
            : base(displayName: label, description: description, groupName: Groups.Agents)
        {
            this._label = label;
            this._icon = icon;
            this.IsWidget = true;
        }

        protected abstract void SendShortcut();

        protected override void RunCommand(String actionParameter)
        {
            var now = DateTime.UtcNow;

            if (now - this._lastPress < RepeatPressWindow)
            {
                return;
            }

            this._lastPress = now;
            Task.Run(this.Send);
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            KeyArt.Control(imageSize, this._icon, this._label);

        private void Send()
        {
            try
            {
                this.SendShortcut();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"Could not send {this._label}");
            }
        }
    }
}
