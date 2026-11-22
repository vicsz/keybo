namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;

    // The default microphone and the default sound output through CoreAudio: is there one, is an app using it, is it
    // muted, how loud is it, and which process records from the mic. Muting uses the device's own mute switch, or its
    // volume when it has no switch. Only the default devices are touched, so an app set to another device still uses that
    // one. Also which apps are recording (macOS 14.2 or later), for Dictate. It never listens to or plays anything itself.

    internal static class Audio
    {
        private const String CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        private const UInt32 SystemObject = 1;

        // Where unmuting by volume goes when the volume from before the mute is gone (the plugin restarted since):
        // about where a mic usually sits, and low for speakers so they never come back loud.
        private const Single MicUnmuteVolume = 0.75f;
        private const Single SoundUnmuteVolume = 0.25f;

        private static readonly UInt32 Global = Code("glob");
        private static readonly UInt32 Input = Code("inpt");
        private static readonly UInt32 Output = Code("outp");

        // The volumes from before we muted by volume, by device and side, so unmuting puts them back.
        private static readonly ConcurrentDictionary<(UInt32 Device, UInt32 Scope), Single[]> VolumesBeforeMute =
            new ConcurrentDictionary<(UInt32 Device, UInt32 Scope), Single[]>();

        public static AudioState Mic()
        {
            var mic = Read(DefaultDevice("dIn "), Input);

            if (mic.InUse)
            {
                mic.UsedBy = ProcessUsing("piri");
            }

            return mic;
        }

        public static AudioState Sound() => Read(DefaultDevice("dOut"), Output);

        public static void SetMicMuted(Boolean muted) => SetMuted(DefaultDevice("dIn "), Input, muted);

        public static void SetSoundMuted(Boolean muted) => SetMuted(DefaultDevice("dOut"), Output, muted);

        // True if an app whose process name starts with one of these is recording. False if CoreAudio can't say.
        public static Boolean IsRecording(params String[] processPrefixes)
        {
            try
            {
                return GetArray(SystemObject, Code("prs#"))
                    .Where(p => GetArray(p, Code("piri")).FirstOrDefault() != 0)
                    .Select(p => ProcessName((Int32)GetArray(p, Code("ppid")).FirstOrDefault()))
                    .Any(name => processPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                PluginLog.Warning(ex, "Could not ask CoreAudio which apps are recording");
                return false;
            }
        }

        private static AudioState Read(UInt32 device, UInt32 scope)
        {
            if (device == 0)
            {
                return new AudioState();
            }

            var volume = Volume(device, scope);
            var hasSwitch = IsSettable(device, Code("mute"), scope, 0);

            return new AudioState
            {
                Exists = true,
                InUse = GetUInt(device, Code("gone"), Global, 0) is UInt32 running && running != 0,

                // The switch, or a volume of zero on a device without a settable switch, since that's how we mute it.
                Muted = (GetUInt(device, Code("mute"), scope, 0) is UInt32 mute && mute != 0) || (!hasSwitch && volume is Single v && v <= 0),
                CanMute = hasSwitch || volume != null,
                Volume = volume,
            };
        }

        private static void SetMuted(UInt32 device, UInt32 scope, Boolean muted)
        {
            if (device == 0)
            {
                return;
            }

            var mute = Code("mute");

            if (IsSettable(device, mute, scope, 0))
            {
                SetUInt(device, mute, scope, 0, muted ? 1u : 0u);
                return;
            }

            // No mute switch: set the volume on the main element, or on each channel, so the balance comes back too.
            var volm = Code("volm");
            var elements = VolumeElements(device, scope);

            if (muted)
            {
                var volumes = elements.Select(element => GetFloat(device, volm, scope, element) ?? 0).ToArray();

                // Already at zero: keep the volumes saved by the first mute.
                if (volumes.Any(volume => volume > 0))
                {
                    VolumesBeforeMute[(device, scope)] = volumes;
                }
            }

            var before = !muted && VolumesBeforeMute.TryRemove((device, scope), out var saved) && saved.Length == elements.Length ? saved : null;

            for (var i = 0; i < elements.Length; i++)
            {
                SetFloat(device, volm, scope, elements[i], muted ? 0 : before?[i] ?? (scope == Input ? MicUnmuteVolume : SoundUnmuteVolume));
            }
        }

        // The executable of a process using a device this way ('piri' recording), one inside an app if there is one.
        // Null when no process says so, or CoreAudio can't say (before macOS 14.2).
        private static String ProcessUsing(String property)
        {
            try
            {
                var paths = GetArray(SystemObject, Code("prs#"))
                    .Where(p => GetArray(p, Code(property)).FirstOrDefault() != 0)
                    .Select(p => (Int32)GetArray(p, Code("ppid")).FirstOrDefault())
                    .Where(pid => pid > 0)
                    .Select(Mac.ProcessPath)
                    .Where(path => path != null)
                    .ToList();

                return paths.FirstOrDefault(path => path.Contains(".app/", StringComparison.Ordinal)) ?? paths.FirstOrDefault();
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                PluginLog.Warning(ex, "Could not ask CoreAudio which app is recording");
                return null;
            }
        }

        private static String ProcessName(Int32 pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                // It exited between the two calls.
                return "";
            }
        }

        // An array of UInt32s on the global scope: 'prs#' lists audio processes, 'piri' is "recording", 'ppid' is the PID.
        private static UInt32[] GetArray(UInt32 obj, UInt32 selector)
        {
            var address = new Address(selector, Global, 0);

            if (AudioObjectGetPropertyDataSize(obj, ref address, 0, IntPtr.Zero, out var size) != 0 || size == 0)
            {
                return Array.Empty<UInt32>();
            }

            var buffer = Marshal.AllocHGlobal((Int32)size);

            try
            {
                if (AudioObjectGetPropertyData(obj, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                {
                    return Array.Empty<UInt32>();
                }

                var result = new UInt32[size / 4];

                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = (UInt32)Marshal.ReadInt32(buffer, i * 4);
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // The volume from 0 to 1: the main element's, or the channels' average. Null when it has no volume we can set.
        private static Single? Volume(UInt32 device, UInt32 scope)
        {
            var volumes = VolumeElements(device, scope).Select(element => GetFloat(device, Code("volm"), scope, element)).OfType<Single>().ToArray();
            return volumes.Length == 0 ? null : volumes.Average();
        }

        // The elements that carry volume: the main one (0) if it has it, otherwise channels 1 and 2.
        private static UInt32[] VolumeElements(UInt32 device, UInt32 scope)
        {
            if (IsSettable(device, Code("volm"), scope, 0))
            {
                return new UInt32[] { 0 };
            }

            return IsSettable(device, Code("volm"), scope, 1)
                ? (IsSettable(device, Code("volm"), scope, 2) ? new UInt32[] { 1, 2 } : new UInt32[] { 1 })
                : Array.Empty<UInt32>();
        }

        // 'dIn ' is the default input device, 'dOut' the default output.
        private static UInt32 DefaultDevice(String selector) => GetUInt(SystemObject, Code(selector), Global, 0) ?? 0;

        private static UInt32 Code(String fourCharacters) =>
            ((UInt32)fourCharacters[0] << 24) | ((UInt32)fourCharacters[1] << 16) | ((UInt32)fourCharacters[2] << 8) | fourCharacters[3];

        private static Boolean IsSettable(UInt32 device, UInt32 selector, UInt32 scope, UInt32 element)
        {
            var address = new Address(selector, scope, element);
            return AudioObjectHasProperty(device, ref address) != 0 && AudioObjectIsPropertySettable(device, ref address, out var settable) == 0 && settable != 0;
        }

        private static UInt32? GetUInt(UInt32 device, UInt32 selector, UInt32 scope, UInt32 element)
        {
            var address = new Address(selector, scope, element);
            var size = (UInt32)sizeof(UInt32);

            if (AudioObjectHasProperty(device, ref address) == 0)
            {
                return null;
            }

            return AudioObjectGetPropertyData(device, ref address, 0, IntPtr.Zero, ref size, out UInt32 value) == 0 ? value : null;
        }

        private static Single? GetFloat(UInt32 device, UInt32 selector, UInt32 scope, UInt32 element)
        {
            var address = new Address(selector, scope, element);
            var size = (UInt32)sizeof(Single);

            if (AudioObjectHasProperty(device, ref address) == 0)
            {
                return null;
            }

            return AudioObjectGetPropertyData(device, ref address, 0, IntPtr.Zero, ref size, out Single value) == 0 ? value : null;
        }

        private static void SetUInt(UInt32 device, UInt32 selector, UInt32 scope, UInt32 element, UInt32 value)
        {
            var address = new Address(selector, scope, element);
            var result = AudioObjectSetPropertyData(device, ref address, 0, IntPtr.Zero, sizeof(UInt32), ref value);

            if (result != 0)
            {
                PluginLog.Warning($"Couldn't set the {Side(scope)}'s mute: CoreAudio error {result}");
            }
        }

        private static void SetFloat(UInt32 device, UInt32 selector, UInt32 scope, UInt32 element, Single value)
        {
            var address = new Address(selector, scope, element);
            var result = AudioObjectSetPropertyData(device, ref address, 0, IntPtr.Zero, sizeof(Single), ref value);

            if (result != 0)
            {
                PluginLog.Warning($"Couldn't set the {Side(scope)}'s volume: CoreAudio error {result}");
            }
        }

        private static String Side(UInt32 scope) => scope == Input ? "microphone" : "sound output";

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct Address
        {
            public readonly UInt32 Selector;
            public readonly UInt32 Scope;
            public readonly UInt32 Element;

            public Address(UInt32 selector, UInt32 scope, UInt32 element)
            {
                this.Selector = selector;
                this.Scope = scope;
                this.Element = element;
            }
        }

        [DllImport(CoreAudio)]
        private static extern Byte AudioObjectHasProperty(UInt32 objectId, ref Address address);

        [DllImport(CoreAudio)]
        private static extern Int32 AudioObjectIsPropertySettable(UInt32 objectId, ref Address address, out Byte settable);

        [DllImport(CoreAudio)]
        private static extern Int32 AudioObjectGetPropertyData(UInt32 objectId, ref Address address, UInt32 qualifierSize, IntPtr qualifier, ref UInt32 size, out UInt32 data);

        [DllImport(CoreAudio)]
        private static extern Int32 AudioObjectGetPropertyData(UInt32 objectId, ref Address address, UInt32 qualifierSize, IntPtr qualifier, ref UInt32 size, out Single data);

        [DllImport(CoreAudio)]
        private static extern Int32 AudioObjectGetPropertyDataSize(UInt32 objectId, ref Address address, UInt32 qualifierSize, IntPtr qualifier, out UInt32 size);

        [DllImport(CoreAudio)]
        private static extern Int32 AudioObjectGetPropertyData(UInt32 objectId, ref Address address, UInt32 qualifierSize, IntPtr qualifier, ref UInt32 size, IntPtr data);

        [DllImport(CoreAudio)]
        private static extern Int32 AudioObjectSetPropertyData(UInt32 objectId, ref Address address, UInt32 qualifierSize, IntPtr qualifier, UInt32 size, ref UInt32 data);

        [DllImport(CoreAudio)]
        private static extern Int32 AudioObjectSetPropertyData(UInt32 objectId, ref Address address, UInt32 qualifierSize, IntPtr qualifier, UInt32 size, ref Single data);
    }

    // The default microphone or sound output, as last read.
    public class AudioState
    {
        public Boolean Exists { get; set; }

        // An app is recording from it (mic) or playing through it (sound) right now.
        public Boolean InUse { get; set; }

        public Boolean Muted { get; set; }

        // False with neither a mute switch nor a volume we can set, as with many displays' speakers.
        public Boolean CanMute { get; set; }

        // 0 to 1, or null without a volume we can set.
        public Single? Volume { get; set; }

        // The mic only: the executable of the process recording from it, when CoreAudio can say (macOS 14.2 or later).
        public String UsedBy { get; set; }

        public override String ToString() => !this.Exists ? "none" :
            (this.Muted ? "muted" : "on") + (this.Volume is Single volume ? FormattableString.Invariant($" {volume * 100:0}%") : String.Empty) +
            (this.InUse ? ", in use" : String.Empty);
    }
}
