namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Linq;
    using System.Threading;

    // Hold to choose, on the held key itself: while the key is held it steps through a few choices, a second each,
    // and letting go picks the one showing. The last choice is always Never mind, so a hold you didn't mean costs
    // nothing. The keypad sends LongPress at about 0.5 s, which opens the menu on its first choice.
    // If the release never arrives (the Mac locked mid-hold, say), the menu closes by itself on Never mind.

    public sealed class HoldMenu : IDisposable
    {
        private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(5);
        private static readonly Choice NeverMind = new Choice(KeyArt.CancelIcon, "Never mind", null);

        private readonly Object _lock = new Object();
        private readonly Action _redraw;
        private readonly Timer _timer;
        private Choice[] _choices;
        private DateTime _openedAt;

        public HoldMenu(Action redraw)
        {
            this._redraw = redraw;
            this._timer = new Timer(_ => this.Tick());
        }

        public record Choice(String Icon, String Label, Action Run);

        // The choice showing now, or null while the menu is closed.
        public Choice Current
        {
            get
            {
                lock (this._lock)
                {
                    return this._choices == null ? null : this._choices[this.Index()];
                }
            }
        }

        public void Open(params Choice[] choices)
        {
            lock (this._lock)
            {
                this._choices = choices.Append(NeverMind).ToArray();
                this._openedAt = DateTime.UtcNow;
                this._timer.Change(Step, Step);
            }

            this._redraw();
        }

        // On release: closes the menu and runs the choice showing. False if no menu was open.
        public Boolean Release()
        {
            Choice choice;

            lock (this._lock)
            {
                if (this._choices == null)
                {
                    return false;
                }

                choice = this._choices[this.Index()];
                this.Close();
            }

            this._redraw();

            try
            {
                choice.Run?.Invoke();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"Hold choice {choice.Label} failed");
            }

            return true;
        }

        public void Dispose() => this._timer.Dispose();

        private Int32 Index() => Math.Min((Int32)((DateTime.UtcNow - this._openedAt) / Step), this._choices.Length - 1);

        private void Close()
        {
            this._choices = null;
            this._timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void Tick()
        {
            lock (this._lock)
            {
                if (this._choices == null)
                {
                    return;
                }

                if (DateTime.UtcNow - this._openedAt > (Step * (this._choices.Length - 1)) + GiveUp)
                {
                    this.Close();
                }
            }

            this._redraw();
        }
    }
}
