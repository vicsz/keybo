namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;

    // The default microphone through CoreAudio: is there one, is an app recording from it, is it muted.
    // Muting uses the device's own mute switch, or its input volume when it has no switch. Only the default
    // microphone is muted, so an app set to a different microphone still hears that one.
    // Also which apps are recording (macOS 14.2 or later), for Dictate. It never listens to the mic itself.

    internal static class Audio
    {
        private const String CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        private const UInt32 SystemObject = 1;
        private const Single UnmuteVolume = 0.75f;

        private static readonly UInt32 Global = Code("glob");
        private static readonly UInt32 Input = Code("inpt");

        // The input volume before we muted by volume, so unmuting puts it back.
        private static Single? _volumeBeforeMute;

        public static MicState Mic()
        {
            var device = DefaultInput();

            if (device == 0)
            {
                return new MicState();
            }

            return new MicState
            {
                Exists = true,
                InUse = GetUInt(device, Code("gone"), Global, 0) is UInt32 running && running != 0,
                Muted = IsMuted(device),
            };
        }

        public static void SetMicMuted(Boolean muted)
        {
            var device = DefaultInput();

            if (device == 0)
            {
                return;
            }

            var mute = Code("mute");

            if (IsSettable(device, mute, Input, 0))
            {
                SetUInt(device, mute, Input, 0, muted ? 1u : 0u);
                return;
            }

            // No mute switch: set the input volume on the main element, or on each channel.
            foreach (var element in VolumeElements(device))
            {
                if (muted)
                {
                    _volumeBeforeMute ??= GetFloat(device, Code("volm"), Input, element);
                    SetFloat(device, Code("volm"), Input, element, 0);
                }
                else
                {
                    var volume = _volumeBeforeMute is Single before && before > 0 ? before : UnmuteVolume;
                    SetFloat(device, Code("volm"), Input, element, volume);
                }
            }

            if (!muted)
            {
                _volumeBeforeMute = null;
            }
        }

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

        private static Boolean IsMuted(UInt32 device)
        {
            if (GetUInt(device, Code("mute"), Input, 0) is UInt32 mute)
            {
                return mute != 0;
            }

            foreach (var element in VolumeElements(device))
            {
                return GetFloat(device, Code("volm"), Input, element) is Single volume && volume <= 0;
            }

            return false;
        }

        // The elements that carry input volume: the main one (0) if it has it, otherwise channels 1 and 2.
        private static UInt32[] VolumeElements(UInt32 device)
        {
            if (IsSettable(device, Code("volm"), Input, 0))
            {
                return new UInt32[] { 0 };
            }

            return IsSettable(device, Code("volm"), Input, 1)
                ? (IsSettable(device, Code("volm"), Input, 2) ? new UInt32[] { 1, 2 } : new UInt32[] { 1 })
                : Array.Empty<UInt32>();
        }

        private static UInt32 DefaultInput() => GetUInt(SystemObject, Code("dIn "), Global, 0) ?? 0;

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
                PluginLog.Warning($"Couldn't set the microphone's mute: CoreAudio error {result}");
            }
        }

        private static void SetFloat(UInt32 device, UInt32 selector, UInt32 scope, UInt32 element, Single value)
        {
            var address = new Address(selector, scope, element);
            var result = AudioObjectSetPropertyData(device, ref address, 0, IntPtr.Zero, sizeof(Single), ref value);

            if (result != 0)
            {
                PluginLog.Warning($"Couldn't set the microphone's volume: CoreAudio error {result}");
            }
        }

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

    public class MicState
    {
        public Boolean Exists { get; set; }

        // An app is recording from it right now.
        public Boolean InUse { get; set; }

        public Boolean Muted { get; set; }

        public override String ToString() => !this.Exists ? "none" : $"{(this.Muted ? "muted" : "on")}{(this.InUse ? ", in use" : String.Empty)}";
    }
}
