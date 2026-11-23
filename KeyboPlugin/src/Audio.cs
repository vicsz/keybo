namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;

    // A mic and a sound output through CoreAudio: is there one, its name, is an app using it and which, is it muted, and
    // how loud is it. Each is the device an app is using on that side (the default if it's one of them), else the default.
    // Muting uses the device's own mute switch, or its volume when it has no switch. Also which apps are recording (macOS
    // 14.2 or later), for Dictate. It never listens to or plays anything itself.

    internal static class Audio
    {
        private const String CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        private const String CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const UInt32 SystemObject = 1;
        private const UInt32 Utf8 = 0x08000100;

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

        // The mic an app is recording from, else the default. Shown is the program shown last time, kept while it still records.
        public static AudioState Mic(String shown = null) => Pick(Input, "dIn ", "piri", shown);

        // The output an app is playing through, else the default, the same way.
        public static AudioState Sound(String shown = null) => Pick(Output, "dOut", "piro", shown);

        // Mute the device that Mic() or Sound() picked (its Device), which isn't always the default.
        public static void SetMicMuted(UInt32 device, Boolean muted) => SetMuted(device, Input, muted);

        public static void SetSoundMuted(UInt32 device, Boolean muted) => SetMuted(device, Output, muted);

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

        // The device an app is using this way ('piri' recording, 'piro' playing), the default if it's one of them, else the
        // default. CoreAudio's process list (macOS 14.2 or later) says which devices each process uses, so it also decides
        // "in use" and who; without it, the device running counts.
        private static AudioState Pick(UInt32 scope, String defaultSelector, String usingProperty, String shown)
        {
            var defaultDevice = DefaultDevice(defaultSelector);
            var (listed, clients) = Clients(usingProperty, scope);
            var live = clients.SelectMany(client => client.Devices).Distinct().OrderBy(id => id).ToList();
            var device = live.Count == 0 || live.Contains(defaultDevice) ? defaultDevice : live[0];
            var state = Read(device, scope);
            state.Name = device == 0 ? null : GetString(device, Code("lnam"));

            if (listed)
            {
                // A process that doesn't say which device it uses counts for this one only while this one is running.
                var users = clients.Where(client => client.Devices.Contains(device) || (client.Devices.Length == 0 && state.InUse)).Select(client => client.Pid).ToList();
                state.InUse = users.Count > 0;
                state.UsedBy = Program(users, shown);
            }

            return state;
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
                Device = device,
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

        // Every process using a device this way ('piri' recording, 'piro' playing), with the devices it uses on that side
        // ('pdv#'), and whether CoreAudio could list processes at all (macOS 14.2 or later).
        private static (Boolean Listed, List<(Int32 Pid, UInt32[] Devices)> Clients) Clients(String property, UInt32 scope)
        {
            try
            {
                var processes = GetArray(SystemObject, Code("prs#"));
                var clients = processes
                    .Where(p => GetArray(p, Code(property)).FirstOrDefault() != 0)
                    .Select(p => ((Int32)GetArray(p, Code("ppid")).FirstOrDefault(), GetArray(p, Code("pdv#"), scope)))
                    .ToList();

                return (processes.Length > 0, clients);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                PluginLog.Warning(ex, "Could not ask CoreAudio which apps are using sound");
                return (false, new List<(Int32, UInt32[])>());
            }
        }

        // The program to show for these processes: the one macOS holds responsible for each (an app for its helpers and
        // the WebKit processes it starts), apps first. The one shown before stays while it's still among them, so the icon
        // doesn't flicker when several play at once. Null for none.
        private static String Program(IEnumerable<Int32> pids, String shown)
        {
            var paths = pids
                .Where(pid => pid > 0)
                .Select(pid => Mac.ProcessPath(Mac.ResponsiblePid(pid)) ?? Mac.ProcessPath(pid))
                .Where(path => path != null)
                .Distinct()
                .ToList();
            var apps = paths.Where(path => path.Contains(".app/", StringComparison.Ordinal)).ToList();
            var choices = apps.Count > 0 ? apps : paths;

            return choices.Contains(shown) ? shown : choices.FirstOrDefault();
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

        // An array of UInt32s: 'prs#' lists audio processes, 'piri' is "recording", 'ppid' is the PID, 'pdv#' the devices
        // a process uses on one side.
        private static UInt32[] GetArray(UInt32 obj, UInt32 selector) => GetArray(obj, selector, Global);

        private static UInt32[] GetArray(UInt32 obj, UInt32 selector, UInt32 scope)
        {
            var address = new Address(selector, scope, 0);

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

        // A string property ('lnam' is a device's name), or null. CoreAudio hands over a CFString, which is ours to release.
        private static String GetString(UInt32 obj, UInt32 selector)
        {
            var address = new Address(selector, Global, 0);
            var size = (UInt32)IntPtr.Size;

            if (AudioObjectHasProperty(obj, ref address) == 0 || AudioObjectGetPropertyData(obj, ref address, 0, IntPtr.Zero, ref size, out IntPtr text) != 0 || text == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var buffer = new Byte[512];
                return CFStringGetCString(text, buffer, buffer.Length, Utf8) != 0 ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (Byte)0)) : null;
            }
            finally
            {
                CFRelease(text);
            }
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

        [DllImport(CoreAudio)]
        private static extern Int32 AudioObjectGetPropertyData(UInt32 objectId, ref Address address, UInt32 qualifierSize, IntPtr qualifier, ref UInt32 size, out IntPtr data);

        [DllImport(CoreFoundation)]
        private static extern Byte CFStringGetCString(IntPtr text, [Out] Byte[] buffer, Int64 bufferSize, UInt32 encoding);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr value);
    }

    // A mic or a sound output, as last read.
    public class AudioState
    {
        public Boolean Exists { get; set; }

        // CoreAudio's ID for the device, for muting the same one.
        public UInt32 Device { get; set; }

        // Its name as macOS shows it, like "EarPods Microphone".
        public String Name { get; set; }

        // An app is recording from it (mic) or playing through it (sound) right now.
        public Boolean InUse { get; set; }

        public Boolean Muted { get; set; }

        // False with neither a mute switch nor a volume we can set, as with many displays' speakers.
        public Boolean CanMute { get; set; }

        // 0 to 1, or null without a volume we can set.
        public Single? Volume { get; set; }

        // The program recording from it or playing through it (the app macOS holds responsible), when CoreAudio can say
        // (macOS 14.2 or later).
        public String UsedBy { get; set; }

        public override String ToString() => !this.Exists ? "none" :
            (this.Name == null ? String.Empty : this.Name + " ") + (this.Muted ? "muted" : "on") +
            (this.Volume is Single volume ? FormattableString.Invariant($" {volume * 100:0}%") : String.Empty) +
            (this.InUse ? ", in use" : String.Empty) + (this.UsedBy == null ? String.Empty : " by " + Path.GetFileName(this.UsedBy));
    }
}
