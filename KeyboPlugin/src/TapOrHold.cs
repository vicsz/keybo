namespace Loupedeck.KeyboPlugin
{
    using System;

    // Tells a tap from a hold, the same way on every key that has both. The keypad reports press, hold (LongPress, at
    // about 0.5 s) and release through ProcessButtonEvent2, so a tap acts on release and a hold never taps as well.
    // A key that only ever gets RunCommand (no button events) treats it as a tap. A key with parameters (the session
    // keys) passes its parameter through; only one key is held at a time.

    public sealed class TapOrHold
    {
        private readonly Action<String> _tap;
        private readonly Action<String> _hold;
        private readonly Action<String> _letGo;
        private Boolean _getsButtonEvents;
        private Boolean _held;

        // tap runs on release after a short press, hold when the hold starts, and letGo on release after a hold.
        public TapOrHold(Action<String> tap, Action<String> hold, Action<String> letGo = null)
        {
            this._tap = tap;
            this._hold = hold;
            this._letGo = letGo;
        }

        // Call from ProcessButtonEvent2.
        public Boolean Handle(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            this._getsButtonEvents = true;

            switch (buttonEvent.EventType)
            {
                case DeviceButtonEventType.Press:
                    this._held = false;
                    break;

                case DeviceButtonEventType.LongPress:
                    if (!this._held)
                    {
                        this._held = true;
                        this._hold(actionParameter);
                    }

                    break;

                case DeviceButtonEventType.Release:
                    if (this._held)
                    {
                        this._letGo?.Invoke(actionParameter);
                    }
                    else
                    {
                        this._tap(actionParameter);
                    }

                    break;
            }

            return true;
        }

        // Call from RunCommand: a tap, unless the key gets button events, when the release has already tapped.
        public void RunCommand(String actionParameter)
        {
            if (!this._getsButtonEvents)
            {
                this._tap(actionParameter);
            }
        }
    }
}
