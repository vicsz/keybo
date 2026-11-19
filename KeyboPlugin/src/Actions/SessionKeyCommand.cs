namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Threading;

    // A key you lock to any session. Tap an empty key to lock the session on screen, tap a locked key to open its
    // session. Hold to choose, on the key itself: Pin here (the session on screen), Unpin (a locked key only), or
    // Never mind. All six keys are one action with a parameter each, so one menu serves whichever key is held.

    public class SessionKeyCommand : PluginDynamicCommand
    {
        private const Int32 KeyCount = 6;
        private const Int32 DotCount = 3;
        private const Int32 DotMilliseconds = 500;

        private SessionKeyLocks _locks;
        private Timer _dots;
        private Int32 _litDot;
        private HoldMenu _menu;
        private Int32 _menuKey;
        private readonly TapOrHold _buttons;

        public SessionKeyCommand()
            : base()
        {
            this.IsWidget = true;
            this._buttons = new TapOrHold(this.Tap, this.Hold, _ => this._menu.Release());

            for (var key = 0; key < KeyCount; key++)
            {
                this.AddParameter(key.ToString(), $"Session key {key + 1}", Groups.Agents);
            }
        }

        private ClaudeSessionStore Sessions => ((KeyboPlugin)this.Plugin).Sessions;

        protected override Boolean OnLoad()
        {
            this._locks = new SessionKeyLocks(this.Plugin.GetPluginDataDirectory(), KeyCount);
            this.Sessions.Changed += this.OnSessionsChanged;
            this._menu = new HoldMenu(() => this.ActionImageChanged(this._menuKey.ToString()));
            this._dots = new Timer(_ => this.AdvanceDots(), null, DotMilliseconds, DotMilliseconds);
            return true;
        }

        protected override Boolean OnUnload()
        {
            this.Sessions.Changed -= this.OnSessionsChanged;
            this._menu?.Dispose();
            this._dots?.Dispose();
            return true;
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent) =>
            this._buttons.Handle(actionParameter, buttonEvent);

        protected override void RunCommand(String actionParameter) => this._buttons.RunCommand(actionParameter);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (!Int32.TryParse(actionParameter, out var key))
            {
                return KeyArt.Empty(imageSize);
            }

            if (key == this._menuKey && this._menu?.Current is HoldMenu.Choice choice)
            {
                return KeyArt.HoldChoice(imageSize, choice);
            }

            var session = this.GetSession(key);
            return session == null ? KeyArt.EmptySessionKey(imageSize) : KeyArt.Session(imageSize, session, this._litDot);
        }

        private SessionInfo GetSession(Int32 key) => this.Sessions.Get(this._locks?.Get(key));

        // A locked key opens its session; an empty one locks the session on screen.
        private void Tap(String actionParameter)
        {
            if (!Int32.TryParse(actionParameter, out var key))
            {
                return;
            }

            if (this.GetSession(key) is SessionInfo session)
            {
                this.Sessions.Open(session);
            }
            else
            {
                this.LockCurrent(key);
            }
        }

        private void Hold(String actionParameter)
        {
            if (!Int32.TryParse(actionParameter, out var key))
            {
                return;
            }

            var pin = new HoldMenu.Choice(KeyArt.PinHereIcon, "Pin here", () => this.LockCurrent(key));
            this._menuKey = key;

            if (this.GetSession(key) == null)
            {
                this._menu.Open(pin);
            }
            else
            {
                this._menu.Open(pin, new HoldMenu.Choice(KeyArt.UnpinIcon, "Unpin", () => this.Clear(key)));
            }
        }

        private void LockCurrent(Int32 key)
        {
            var current = this.Sessions.FocusedNow();

            if (current != null)
            {
                this._locks.Lock(key, current.Id);
                this.RedrawAll();
            }
        }

        private void Clear(Int32 key)
        {
            this._locks.Clear(key);
            this.RedrawAll();
        }

        private void OnSessionsChanged()
        {
            this.StartFromPins();
            this.ClearGoneSessions();
            this.RedrawAll();
        }

        // On first use, the keys start with your Claude pins. After that they are yours.
        private void StartFromPins()
        {
            if (!this._locks.IsNew)
            {
                return;
            }

            for (var key = 0; key < KeyCount; key++)
            {
                var pin = this.Sessions.GetPin(key);

                if (pin != null)
                {
                    this._locks.Lock(key, pin.Id);
                }
            }

            this._locks.Save();
        }

        private void ClearGoneSessions()
        {
            for (var key = 0; key < KeyCount; key++)
            {
                if (this._locks.Get(key) is String id && this.Sessions.IsGone(id))
                {
                    this._locks.Clear(key);
                }
            }
        }

        private void RedrawAll()
        {
            for (var key = 0; key < KeyCount; key++)
            {
                this.ActionImageChanged(key.ToString());
            }
        }

        private void AdvanceDots()
        {
            this._litDot = (this._litDot + 1) % DotCount;

            for (var key = 0; key < KeyCount; key++)
            {
                var session = this.GetSession(key);

                if (session?.Status is SessionStatus.Busy or SessionStatus.Background)
                {
                    this.ActionImageChanged(key.ToString());
                }
            }
        }
    }
}
