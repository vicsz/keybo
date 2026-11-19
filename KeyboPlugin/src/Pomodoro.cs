namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Threading;

    // The 25-minute Pomodoro: ready, running, paused or done. The Pomodoro key draws it and Keybo watches it.
    // It ticks once a second, only while running. When the time is up a sound plays and it stays done until tapped.

    public sealed class Pomodoro : IDisposable
    {
        public static readonly TimeSpan Length = TimeSpan.FromMinutes(25);

        private readonly Object _lock = new Object();
        private Timer _tick;

        // Running: when it ends. Paused: how much was left. Neither: ready, or done.
        private DateTime? _endsAt;
        private TimeSpan? _pausedLeft;
        private Boolean _done;

        // On every change, and every second while running.
        public event Action Changed;

        // Once, when the time is up.
        public event Action Finished;

        public Boolean IsDone
        {
            get
            {
                lock (this._lock)
                {
                    return this._done;
                }
            }
        }

        public Boolean IsRunning
        {
            get
            {
                lock (this._lock)
                {
                    return this._endsAt != null;
                }
            }
        }

        // Done, running, and the time left: the full 25 minutes when ready, never below zero.
        public (Boolean Done, Boolean Running, TimeSpan Left) State
        {
            get
            {
                lock (this._lock)
                {
                    var left = this._endsAt is DateTime end ? end - DateTime.UtcNow : this._pausedLeft ?? Length;
                    return (this._done, this._endsAt != null, left < TimeSpan.Zero ? TimeSpan.Zero : left);
                }
            }
        }

        // Ready starts, running pauses, paused carries on, done goes back to ready.
        public void Tap()
        {
            lock (this._lock)
            {
                if (this._done)
                {
                    this._done = false;
                }
                else if (this._endsAt is DateTime end)
                {
                    this._pausedLeft = end - DateTime.UtcNow;
                    this._endsAt = null;
                    this.StopTicking();
                }
                else
                {
                    this._endsAt = DateTime.UtcNow + (this._pausedLeft ?? Length);
                    this._pausedLeft = null;
                    this._tick = new Timer(_ => this.Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }
            }

            this.Changed?.Invoke();
        }

        public void Reset()
        {
            lock (this._lock)
            {
                this.StopTicking();
                this._endsAt = null;
                this._pausedLeft = null;
                this._done = false;
            }

            PluginLog.Info("Pomodoro reset");
            this.Changed?.Invoke();
        }

        public void Dispose()
        {
            lock (this._lock)
            {
                this.StopTicking();
            }
        }

        private void Tick()
        {
            var finished = false;

            lock (this._lock)
            {
                if (this._endsAt is DateTime end && DateTime.UtcNow >= end)
                {
                    this.StopTicking();
                    this._endsAt = null;
                    this._done = true;
                    finished = true;
                }
            }

            if (finished)
            {
                PluginLog.Info("Pomodoro done");
                Shell.PlaySound("Glass");
                this.Finished?.Invoke();
            }

            this.Changed?.Invoke();
        }

        private void StopTicking()
        {
            this._tick?.Dispose();
            this._tick = null;
        }
    }
}
