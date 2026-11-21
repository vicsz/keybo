namespace Loupedeck.KeyboPlugin
{
    using System;

    // The plugin: owns what the keys share, so each exists once however many keys show it.

    public class KeyboPlugin : Plugin
    {
        private ClaudeUsage _usage;

        // A universal plugin: it works in every app rather than linking to one.
        public override Boolean UsesApplicationApiOnly => true;

        public override Boolean HasNoApplication => true;

        // The system readings for the stat keys and Keybo. It samples only while a key is showing.
        public SystemMonitor Monitor { get; } = new SystemMonitor();

        // The Keep awake switch. It starts off each time the plugin loads.
        public KeepAwake Awake { get; } = new KeepAwake();

        // The Pomodoro timer, which the Pomodoro key runs and Keybo watches.
        public Pomodoro Pomodoro { get; } = new Pomodoro();

        // The live Claude and Codex sessions, for the session keys and Keybo.
        public ClaudeSessionStore Sessions { get; } = new ClaudeSessionStore();

        // Whether Claude or ChatGPT is recording, for Dictate and Return.
        public Dictation Dictation { get; } = new Dictation();

        // Claude's usage for the Usage key. It starts only when that key is first drawn.
        public ClaudeUsage Usage => this._usage ??= new ClaudeUsage(this.GetPluginDataDirectory());

        // Raised when Return sends (not when it only stops dictation). Keybo throws a paper plane.
        public event Action MessageSent;

        public KeyboPlugin() => PluginLog.Init(this.Log);

        public override void Load()
        {
            this.Sessions.Start();
            this.Dictation.Start();
        }

        public override void Unload()
        {
            this.Monitor.Dispose();
            this.Awake.Dispose();
            this.Pomodoro.Dispose();
            this.Sessions.Dispose();
            this.Dictation.Dispose();
            this._usage?.Dispose();
        }

        public void OnMessageSent() => this.MessageSent?.Invoke();
    }
}
