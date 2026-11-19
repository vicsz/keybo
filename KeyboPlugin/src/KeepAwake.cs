namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;

    // Keeps the Mac and its screen on, for watching a long call without touching anything: no idle sleep, no screen-off,
    // and so no lock, which macOS starts after inactivity. It holds a display-sleep assertion (what video players use,
    // like caffeinate -d) and, every 30 seconds, tells macOS the user is active (like caffeinate -u), so a screen saver
    // timer, including one a company sets, never runs out either. macOS drops both if the plugin quits or crashes, so
    // the Mac can't get stuck awake.

    public class KeepAwake : IDisposable
    {
        private const String IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const String CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const UInt32 Utf8 = 0x08000100;
        private const UInt32 AssertionOn = 255;
        private const UInt32 UserActiveLocal = 0;
        private static readonly TimeSpan ActivityEvery = TimeSpan.FromSeconds(30);

        private readonly Object _lock = new Object();
        private IntPtr _name;
        private UInt32 _assertion;
        private UInt32 _activity;
        private Timer _activityTimer;

        public event Action Changed;

        public Boolean IsOn
        {
            get
            {
                lock (this._lock)
                {
                    return this._assertion != 0;
                }
            }
        }

        public void Toggle()
        {
            lock (this._lock)
            {
                if (this._assertion != 0)
                {
                    this.Release();
                }
                else
                {
                    this.Hold();
                }
            }

            this.Changed?.Invoke();
        }

        public void Dispose()
        {
            lock (this._lock)
            {
                this.Release();
            }
        }

        private void Hold()
        {
            var type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleDisplaySleep", Utf8);
            this._name = CFStringCreateWithCString(IntPtr.Zero, "Keybo: Keep awake key", Utf8);
            var result = IOPMAssertionCreateWithName(type, AssertionOn, this._name, out this._assertion);
            CFRelease(type);

            if (result != 0)
            {
                this._assertion = 0;
                this.Release();
                PluginLog.Error($"Keep awake failed: IOKit error {result:X}");
                return;
            }

            this.DeclareActivity();
            this._activityTimer = new Timer(_ => this.OnActivityTimer(), null, ActivityEvery, ActivityEvery);
            PluginLog.Info("Keep awake on");
        }

        private void OnActivityTimer()
        {
            lock (this._lock)
            {
                if (this._assertion != 0)
                {
                    this.DeclareActivity();
                }
            }
        }

        // Resets macOS's inactivity timers, so a screen saver and the lock that follows it don't start.
        private void DeclareActivity()
        {
            var result = IOPMAssertionDeclareUserActivity(this._name, UserActiveLocal, ref this._activity);

            if (result != 0)
            {
                PluginLog.Warning($"Keep awake couldn't declare activity: IOKit error {result:X}");
            }
        }

        private void Release()
        {
            this._activityTimer?.Dispose();
            this._activityTimer = null;

            if (this._activity != 0)
            {
                IOPMAssertionRelease(this._activity);
                this._activity = 0;
            }

            if (this._assertion != 0)
            {
                IOPMAssertionRelease(this._assertion);
                this._assertion = 0;
                PluginLog.Info("Keep awake off");
            }

            if (this._name != IntPtr.Zero)
            {
                CFRelease(this._name);
                this._name = IntPtr.Zero;
            }
        }

        [DllImport(IOKit)]
        private static extern Int32 IOPMAssertionCreateWithName(IntPtr type, UInt32 level, IntPtr name, out UInt32 assertion);

        [DllImport(IOKit)]
        private static extern Int32 IOPMAssertionDeclareUserActivity(IntPtr name, UInt32 userType, ref UInt32 assertion);

        [DllImport(IOKit)]
        private static extern Int32 IOPMAssertionRelease(UInt32 assertion);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, String text, UInt32 encoding);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr reference);
    }
}
