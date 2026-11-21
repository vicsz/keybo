namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Threading;

    // Whether Claude or ChatGPT is recording, for the keys that show it: Dictate ripples, Return shows a dot.
    // It asks CoreAudio every 0.6 s and never listens to the mic. While recording it also steps a shared frame,
    // so every key that shows it pulses together.

    public sealed class Dictation : IDisposable
    {
        // ChatGPT's dictation runs in "Codex (Aperitif)". Claude's runs in Claude or one of its helpers.
        public const String ChatGptProcess = "Codex";
        public const String ClaudeProcess = "Claude";

        private const Int32 FrameMs = 200;
        private const Int32 FramesPerCheck = 3;

        private Timer _timer;
        private Int32 _tick;

        // Raised on every frame while recording, and once when it stops.
        public event Action Changed;

        public Boolean IsListening { get; private set; }

        public Int32 Frame => this._tick;

        public void Start() => this._timer = new Timer(_ => this.Tick(), null, FrameMs, FrameMs);

        public void Dispose() => this._timer?.Dispose();

        private void Tick()
        {
            var tick = Interlocked.Increment(ref this._tick);
            var was = this.IsListening;

            if (tick % FramesPerCheck == 0)
            {
                this.IsListening = Audio.IsRecording(ClaudeProcess, ChatGptProcess);
            }

            if (this.IsListening || was != this.IsListening)
            {
                this.Changed?.Invoke();
            }
        }
    }
}
