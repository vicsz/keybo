namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    // Keybo, the robot that sums up the Mac and your agents on one key. PetMood decides what he shows; this key plays
    // it, reacts to taps and holds, and adds the moments in between. The frames and timings live in Pet/ (see PetArt).
    // Tap: deal with the sign and set it aside (a session opens, the memory walls open, anything else is eaten), or,
    // with nothing showing, a random gag. Quick taps after it only react: the 2nd gets a head pat, the 3rd a side eye,
    // the 5th a huff, and the 8th a tantrum (1 time in 4 a censored finger instead) and 30 seconds of sulking, when
    // taps do nothing unless a sign is up.
    // Hold: he squishes (a pancake at 2 s, a pressure gauge at 4 s); letting go springs him back, or launches him.
    // In between, never over a red sign: the headband comes and goes with a Pomodoro, the recording booth with the mic,
    // the cable with the connection, a paper plane flies when Return sends (once the booth has closed), and a few clips
    // mark events (Mic Drop when a session finishes, Pomodoro Rebellion when a Pomodoro ends, Coffee Overflow when Keep
    // awake goes on, Low Battery when he wakes from a doze, Jump Start when a laptop under 20% gets power).
    // Gags also play on their own, rarely: only after 15 calm minutes, while you've paused (2 to 10 minutes without
    // input), at most one every 10 to 20 minutes.
    // (The class keeps its first name so keys already on the keypad stay put.)

    public class PetCommand : PluginDynamicCommand
    {
        // How soon to look again while a last frame is held, or while a squish is held.
        private const Int32 LastFrameRecheckMs = 1000;
        private const Int32 SquishRecheckMs = 100;

        // When a gag may play on its own.
        private static readonly TimeSpan Calm = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan Paused = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan GagGap = TimeSpan.FromMinutes(10);

        // Taps closer together than this count as one burst. A sulk lasts this long.
        private static readonly TimeSpan TapWindow = TimeSpan.FromSeconds(1.2);
        private static readonly TimeSpan Sulk = TimeSpan.FromSeconds(30);

        // How long a hold takes to flatten him, then to max out the gauge. LongPress arrives at about 0.5 s.
        private static readonly TimeSpan LongPress = TimeSpan.FromSeconds(0.5);
        private static readonly TimeSpan Pancake = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan Pressure = TimeSpan.FromSeconds(4);

        // A paper plane only flies if it can start this soon after Return.
        private static readonly TimeSpan PlaneWindow = TimeSpan.FromSeconds(2);

        // An unchanged picture is sent again only this often, which also keeps the monitor running under a held frame.
        private static readonly TimeSpan Resend = TimeSpan.FromSeconds(2);

        private readonly Object _lock = new Object();
        private readonly Random _random = new Random();
        private readonly TapOrHold _buttons;
        private PetMood _mood;
        private Timer _timer;

        // What's showing: the animation, its frame and the sign, and whether it's a one-off playing over the mood.
        // Then what was last sent to the keypad, and when.
        private String _state = "idle";
        private Int32 _frame;
        private Sign _sign;
        private Boolean _playingOnce;
        private (String State, Int32 Frame, Sign Sign) _sent;
        private DateTime _sentAt;

        // Gags on their own.
        private DateTime _lastSignAt = DateTime.UtcNow;
        private DateTime _nextGagAt = DateTime.UtcNow;
        private String _lastGag;

        // Events: a gag waiting to play, the sessions done at the last look, whether he was dozing, whether the Mac was on
        // power at the last battery reading (null before the first, and on a Mac without a battery), and when Return sent.
        private String _eventGag;
        private HashSet<String> _doneIds;
        private Boolean _wasSleepy;
        private Boolean? _onPower;
        private DateTime? _planeAt;

        // Taps, holds and sulks.
        private Int32 _tapCount;
        private DateTime _lastTapAt;
        private DateTime? _holdingSince;
        private DateTime? _sulkUntil;

        // Whether the headband or the unplugged cable is showing, so each comes and goes with its own animation.
        private Boolean _focusShown;
        private Boolean _offlineShown;

        public PetCommand()
            : base("Keybo", "Keybo, the robot that keeps an eye on your Mac and your agents. Tap to open or acknowledge what he's showing", Groups.Pets)
        {
            this.IsWidget = true;
            this._buttons = new TapOrHold(_ => this.Tap(), _ => this.StartHold(), _ => this.EndHold());
        }

        private KeyboPlugin Keybo => (KeyboPlugin)this.Plugin;

        protected override Boolean OnLoad()
        {
            this._mood = new PetMood(this.Keybo);
            this.Keybo.Pomodoro.Finished += this.OnPomodoroFinished;
            this.Keybo.Awake.Changed += this.OnAwakeChanged;
            this.Keybo.MessageSent += this.OnMessageSent;
            this._timer = new Timer(_ => this.Advance(), null, 0, Timeout.Infinite);
            return true;
        }

        protected override Boolean OnUnload()
        {
            this.Keybo.Pomodoro.Finished -= this.OnPomodoroFinished;
            this.Keybo.Awake.Changed -= this.OnAwakeChanged;
            this.Keybo.MessageSent -= this.OnMessageSent;

            lock (this._lock)
            {
                this._timer?.Dispose();
                this._timer = null;
            }

            return true;
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent) =>
            this._buttons.Handle(actionParameter, buttonEvent);

        protected override void RunCommand(String actionParameter) => this._buttons.RunCommand(actionParameter);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            this.Keybo.Monitor.Use();

            PetArt.Frame frame;
            Sign sign;

            lock (this._lock)
            {
                var frames = PetArt.States[this._state].Frames;
                frame = frames[Math.Min(this._frame, frames.Length - 1)];
                sign = this._sign;
            }

            return KeyArt.Pet(imageSize, frame, sign);
        }

        private void Tap()
        {
            SessionInfo open = null;

            lock (this._lock)
            {
                var now = DateTime.UtcNow;
                this._tapCount = now - this._lastTapAt <= TapWindow ? this._tapCount + 1 : 1;
                this._lastTapAt = now;
                this._wasSleepy = false;

                var mood = this._mood.Read();

                // Sulking: he ignores you, unless there's something on the sign.
                if (!mood.HasSign && this._sulkUntil > now)
                {
                    return;
                }

                if (this._tapCount > 1)
                {
                    var reaction = this._tapCount switch { 2 => "head_pat", 3 => "side_eye", 5 => "huff", 8 => "tantrum", _ => null };

                    if (reaction == null)
                    {
                        return;
                    }

                    // Both start from idle and end on the sulk's first frame.
                    if (reaction == "tantrum")
                    {
                        this._sulkUntil = now + Sulk;
                        reaction = this._random.Next(4) == 0 ? "middle_finger" : "tantrum";
                    }

                    PluginLog.Info($"Pet: {reaction} after {this._tapCount} taps");
                    this.Play(reaction, null);
                }
                else if (mood.HasSign)
                {
                    this._mood.SetAside(mood);
                    PluginLog.Info($"Pet: acknowledged {mood.Problem ?? mood.Session.Id}");
                    open = mood.Session;
                    this.Play(mood.State switch { "done" => "happy", "walls" => "walls_end", _ => "chomp" }, mood.Sign);
                }
                else
                {
                    this.PlayGag();
                }

                this.Restart();
            }

            this.ActionImageChanged();

            if (open != null)
            {
                this.Keybo.Sessions.Open(open);
            }
        }

        // The hold started about LongPress ago.
        private void StartHold()
        {
            lock (this._lock)
            {
                this._holdingSince = DateTime.UtcNow - LongPress;
                this._playingOnce = false;
                this._sign = null;
                this._state = "squish_1";
                this._frame = 0;
                this.Restart();
            }

            this.ActionImageChanged();
        }

        // Still only squished: a small spring. Flattened: he launches.
        private void EndHold()
        {
            lock (this._lock)
            {
                if (this._holdingSince == null)
                {
                    return;
                }

                this._holdingSince = null;
                this.Play(this._state == "squish_1" ? "spring_small" : "spring_big", null);
                this.Restart();
            }

            this.ActionImageChanged();
        }

        // Steps the animation one frame and schedules the next. Each step also re-reads the mood, so a change shows
        // within one frame (at most a few seconds).
        private void Advance()
        {
            Boolean send;

            lock (this._lock)
            {
                if (this._timer == null)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                var animation = PetArt.States[this._state];
                var last = this._frame >= animation.Frames.Length - 1;
                var delay = 0;

                if (this._holdingSince is DateTime since)
                {
                    var held = now - since;
                    var target = held >= Pressure ? "squish_3" : held >= Pancake ? "squish_2" : "squish_1";
                    delay = this.Step(target, last, animation.Loops, SquishRecheckMs);
                }
                else if (this._playingOnce && !last)
                {
                    this._frame++;
                }
                else
                {
                    this._playingOnce = false;

                    var mood = this._mood.Read();

                    if (mood.HasSign)
                    {
                        this._lastSignAt = now;
                        this._sulkUntil = null;
                    }

                    var interlude = this.Interlude(mood, now);
                    var sulking = this._sulkUntil > now;

                    if (interlude != null)
                    {
                        this.Play(interlude, null);
                        PluginLog.Info($"Pet: {interlude}");
                    }
                    else if (!sulking && this.GagDue(mood, now))
                    {
                        this.PlayGag();
                        this._nextGagAt = now + GagGap + TimeSpan.FromMinutes(this._random.NextDouble() * 10);
                        PluginLog.Info($"Pet: gag {this._state}");
                    }
                    else
                    {
                        delay = this.Step(sulking ? "sulk" : mood.State, last, animation.Loops, LastFrameRecheckMs);
                        this._sign = sulking ? null : mood.Sign;
                    }
                }

                var frames = PetArt.States[this._state].Frames;
                this._timer.Change(Math.Max(delay, frames[this._frame].Ms), Timeout.Infinite);

                var picture = (this._state, this._frame, this._sign);
                send = picture != this._sent || now - this._sentAt >= Resend;

                if (send)
                {
                    this._sent = picture;
                    this._sentAt = now;
                }
            }

            if (send)
            {
                this.ActionImageChanged();
            }
        }

        // Moves to target, or one frame on within it. Returns how long to wait on a held last frame.
        private Int32 Step(String target, Boolean last, Boolean loops, Int32 recheckMs)
        {
            if (target != this._state)
            {
                this._state = target;
                this._frame = 0;
            }
            else if (!last)
            {
                this._frame++;
            }
            else if (loops)
            {
                this._frame = 0;
            }
            else
            {
                return recheckMs;
            }

            return 0;
        }

        // A one-off to play before settling on the mood: the end of a sulk, the walls opening, the booth, headband or
        // cable coming or going, an event, or a paper plane. The walls and the booth go by what's on the key: they only
        // open or close while showing, and the booth comes back in after anything that covered it.
        private String Interlude(Mood mood, DateTime now)
        {
            if (this._sulkUntil is DateTime until && now >= until)
            {
                this._sulkUntil = null;
                return "sulk_end";
            }

            if (this._sulkUntil != null)
            {
                return null;
            }

            var offline = this._mood.IsOffline;
            var live = mood.State == "live";
            var focusing = this.Keybo.Pomodoro.IsRunning && !live;

            // Memory has recovered: the walls open, unless another sign needs the key.
            if (this._state == "walls" && mood.State != "walls" && !mood.HasSign)
            {
                return "walls_end";
            }

            if (!live && this._state is ("live" or "live_start"))
            {
                return "live_end";
            }

            if (this._offlineShown && !offline)
            {
                this._offlineShown = false;
                return "online";
            }

            if (this._focusShown && !focusing)
            {
                this._focusShown = false;
                return "focus_end";
            }

            if (!mood.HasSign && offline && !this._offlineShown)
            {
                this._offlineShown = true;
                this._focusShown = false;
                return "unplugged";
            }

            if (!mood.HasSign && focusing && !offline && !this._focusShown)
            {
                this._focusShown = true;
                return "focus_start";
            }

            if (live && this._state is not ("live" or "live_start"))
            {
                return "live_start";
            }

            var gag = this.EventGag(mood);

            if (gag != null)
            {
                return gag;
            }

            // A plane waits for the booth to close, and still flies if that's soon enough.
            if (live)
            {
                return null;
            }

            var plane = this._planeAt is DateTime sent && now - sent < PlaneWindow && !mood.HasSign;
            this._planeAt = null;
            return plane ? "paper_plane" : null;
        }

        // The event to play now, if any. Sessions count as newly done only after the first look, so a restart doesn't
        // replay old ones, and power counts as plugged in only after a reading without it.
        private String EventGag(Mood mood)
        {
            var done = this.Keybo.Sessions.All.Where(s => s.Status == SessionStatus.Done).Select(s => s.Id).ToHashSet();
            var newlyDone = this._doneIds != null && done.Any(id => !this._doneIds.Contains(id));
            var woke = this._wasSleepy && mood.State != "sleepy";
            var battery = this.Keybo.Monitor.Battery;
            var pluggedInLow = this._onPower == false && battery is { OnPower: true, Percent: < 20 };
            var gag = this._eventGag ?? (pluggedInLow ? "jump_start" : newlyDone ? "gag:mic_drop" : woke ? "gag:low_battery" : null);

            this._doneIds = done;
            this._wasSleepy = mood.State == "sleepy";
            this._onPower = battery?.OnPower;
            this._eventGag = null;

            return mood.State is "warning" or "walls" ? null : gag;
        }

        private void OnPomodoroFinished() => this.QueueGag("pomodoro_rebellion");

        private void OnAwakeChanged()
        {
            if (this.Keybo.Awake.IsOn)
            {
                this.QueueGag("coffee_overflow");
            }
        }

        private void OnMessageSent()
        {
            lock (this._lock)
            {
                this._planeAt = DateTime.UtcNow;
            }
        }

        private void QueueGag(String name)
        {
            lock (this._lock)
            {
                this._eventGag = "gag:" + name;
            }
        }

        private Boolean GagDue(Mood mood, DateTime now)
        {
            if (mood.State != "idle" || mood.HasSign || now - this._lastSignAt < Calm || now < this._nextGagAt)
            {
                return false;
            }

            var idle = TimeSpan.FromSeconds(Mac.IdleSeconds());
            return idle >= Paused && idle < PetMood.Doze;
        }

        // Any gag but the last one.
        private void PlayGag()
        {
            var choices = PetArt.Gags.Where(g => g != this._lastGag).ToArray();
            this._lastGag = choices[this._random.Next(choices.Length)];
            this.Play(this._lastGag, null);
        }

        private void Play(String state, Sign sign)
        {
            this._state = state;
            this._sign = sign;
            this._frame = 0;
            this._playingOnce = true;
        }

        // Shows the current frame for its full time, from now.
        private void Restart() => this._timer?.Change(PetArt.States[this._state].Frames[this._frame].Ms, Timeout.Infinite);
    }
}
