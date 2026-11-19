namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    // What a sign says: plain text (a Mac problem, the Pomodoro), or a session's mark with ! (waiting) or ✓ (done)
    // and how many sessions are like it.
    public record Sign(String Text, SessionInfo Session = null, Int32 Count = 1);

    // What Keybo shows: an animation state and maybe a sign. Problem names a Mac problem or the Pomodoro, so a tap can
    // set it aside; a session's sign carries the session. Ambient moods (offline, focus, sleepy, idle) have no sign.
    public record Mood(String State, Sign Sign = null, String Problem = null)
    {
        public Boolean HasSign => this.Sign != null;

        public SessionInfo Session => this.Sign?.Session;
    }

    // Decides what Keybo shows, worst first: Mac red > session waiting > Pomodoro done > session done > Mac amber >
    // offline > focus (a Pomodoro running) > sleepy (no input for 10 minutes) > idle. The thresholds match the stat
    // keys' colours, except memory: macOS sits at pressure level 2 for hours on a busy Mac, so only critical (4) counts.
    // A tap sets the sign aside: a Mac problem for 30 minutes (even if it clears and comes back meanwhile), a session
    // until it leaves that state, and the Pomodoro until it's cleared.

    public sealed class PetMood
    {
        public static readonly TimeSpan Doze = TimeSpan.FromMinutes(10);

        private const String PomodoroProblem = "pomodoro";
        private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(30);

        private readonly KeyboPlugin _plugin;
        private readonly Dictionary<String, DateTime> _macQuietUntil = new Dictionary<String, DateTime>();
        private readonly HashSet<(String Id, SessionStatus Status)> _quietSessions = new HashSet<(String, SessionStatus)>();
        private Boolean _pomodoroQuiet;

        public PetMood(KeyboPlugin plugin) => this._plugin = plugin;

        // No Ethernet or Wi-Fi link.
        public Boolean IsOffline
        {
            get
            {
                var monitor = this._plugin.Monitor;
                return monitor.HasReadings && monitor.EthernetSpeed == null && monitor.WifiSpeed == null;
            }
        }

        public Mood Read()
        {
            var pomodoro = this._plugin.Pomodoro;
            var now = DateTime.UtcNow;
            var (red, amber) = MacProblems(this._plugin.Monitor);

            // A set-aside Done lasts until that Pomodoro is cleared, so the next one shows.
            if (!pomodoro.IsDone)
            {
                this._pomodoroQuiet = false;
            }

            // A set-aside session stays quiet only until it leaves that state, so its next wait or finish shows again.
            var sessions = this._plugin.Sessions.All;
            this._quietSessions.IntersectWith(sessions.Select(s => (s.Id, s.Status)));
            var waiting = sessions.Where(s => s.Status == SessionStatus.Waiting && !this._quietSessions.Contains((s.Id, s.Status))).ToList();
            var done = sessions.Where(s => s.Status == SessionStatus.Done && !this._quietSessions.Contains((s.Id, s.Status))).ToList();

            if (red is (String redText, String redProblem) && !this.IsQuiet(redProblem, now))
            {
                return new Mood("warning", new Sign(redText), redProblem);
            }

            if (waiting.Count > 0)
            {
                return new Mood("waiting", new Sign(null, waiting[0], waiting.Count));
            }

            if (pomodoro.IsDone && !this._pomodoroQuiet)
            {
                return new Mood("done", new Sign("Pomodoro"), PomodoroProblem);
            }

            if (done.Count > 0)
            {
                return new Mood("done", new Sign(null, done[0], done.Count));
            }

            if (amber is (String amberText, String amberProblem) && !this.IsQuiet(amberProblem, now))
            {
                return new Mood("waiting", new Sign(amberText), amberProblem);
            }

            var ambient = this.IsOffline ? "offline"
                : pomodoro.IsRunning ? "focus"
                : TimeSpan.FromSeconds(Mac.IdleSeconds()) >= Doze ? "sleepy"
                : "idle";

            return new Mood(ambient);
        }

        // A tap dealt with this sign.
        public void SetAside(Mood mood)
        {
            if (mood.Session is SessionInfo session)
            {
                this._quietSessions.Add((session.Id, session.Status));
            }
            else if (mood.Problem == PomodoroProblem)
            {
                this._pomodoroQuiet = true;
            }
            else if (mood.Problem != null)
            {
                this._macQuietUntil[mood.Problem] = DateTime.UtcNow + Quiet;
            }
        }

        private Boolean IsQuiet(String problem, DateTime now) => this._macQuietUntil.TryGetValue(problem, out var until) && now < until;

        // The worst red and amber problem, each with its sign text and a name a tap can set aside.
        private static ((String Text, String Problem)? Red, (String Text, String Problem)? Amber) MacProblems(SystemMonitor m)
        {
            if (!m.HasReadings)
            {
                return (null, null);
            }

            var cpu = m.CpuHistory.ToArray().TakeLast(10).DefaultIfEmpty(0).Average();
            var free = m.DiskTotal > 0 ? (Double)m.DiskFree / m.DiskTotal : 1;
            var battery = m.Battery is BatteryState b && !b.IsCharging && !b.OnPower ? b.Percent : (Double?)null;

            var red = m.CpuTemperature >= 95 ? ($"Hot {m.CpuTemperature:0}°", "hot")
                : m.PressureLevel >= 4 ? ("Memory", "memory")
                : free < 0.05 ? ("Disk full", "disk")
                : battery < 10 ? ($"Battery {battery:0}%", "battery")
                : cpu > 90 ? ($"CPU {cpu:0}%", "cpu")
                : default((String, String)?);

            var amber = free < 0.15 ? ($"Disk {free * 100:0}%", "disk")
                : battery < 20 ? ($"Battery {battery:0}%", "battery")
                : default((String, String)?);

            return (red, amber);
        }
    }
}
