namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;

    // Reading and driving other apps' windows through Accessibility, the permission Logi Plugin Service is given once
    // (Privacy & Security > Accessibility). Teams chats, app pins and their Dock badges use it.

    internal static class Accessibility
    {
        private const String ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const String CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const Int64 PosixPathStyle = 0;

        // CoreFoundation's shared true and false, looked up once, for flags like AXMinimized.
        private static readonly IntPtr CFTrue = CFBooleanNamed("kCFBooleanTrue");
        private static readonly IntPtr CFFalse = CFBooleanNamed("kCFBooleanFalse");

        public static Boolean IsTrusted() => AXIsProcessTrusted();

        // Keeps an element past the read that found it, like a pinned window. Every Retain needs a Release.
        public static IntPtr Retain(IntPtr reference) => CFRetain(reference);

        public static void Release(IntPtr reference) => CFRelease(reference);

        // One read of an app's accessibility tree. Everything it copies is released together when it's disposed,
        // so elements stay valid for the whole walk.
        public sealed class Tree : IDisposable
        {
            private readonly List<IntPtr> _owned = new List<IntPtr>();
            private readonly Dictionary<String, IntPtr> _names = new Dictionary<String, IntPtr>();

            public Tree(Int32 pid)
            {
                this.App = AXUIElementCreateApplication(pid);
                this._owned.Add(this.App);
            }

            public IntPtr App { get; }

            public void Set(IntPtr element, String attribute, Boolean value) =>
                AXUIElementSetAttributeValue(element, this.Name(attribute), value ? CFTrue : CFFalse);

            public String Text(IntPtr element, String attribute) =>
                this.Copy(element, attribute) is IntPtr value && CFGetTypeID(value) == CFStringGetTypeID() ? ReadString(value) : null;

            public IEnumerable<String> Texts(IntPtr element, String attribute) =>
                this.Array(element, attribute).Where(v => CFGetTypeID(v) == CFStringGetTypeID()).Select(ReadString);

            public IEnumerable<IntPtr> Children(IntPtr element, String attribute) => this.Array(element, attribute);

            // One element an attribute points to, like an app's focused window, or zero.
            public IntPtr Element(IntPtr element, String attribute) =>
                this.Copy(element, attribute) is IntPtr value && CFGetTypeID(value) == AXUIElementGetTypeID() ? value : IntPtr.Zero;

            public Boolean Flag(IntPtr element, String attribute) =>
                this.Copy(element, attribute) is IntPtr value && CFGetTypeID(value) == CFBooleanGetTypeID() && CFBooleanGetValue(value);

            // The file a URL attribute points to, like the app behind a Dock icon.
            public String FilePath(IntPtr element, String attribute)
            {
                if (this.Copy(element, attribute) is not IntPtr url || CFGetTypeID(url) != CFURLGetTypeID())
                {
                    return null;
                }

                var path = CFURLCopyFileSystemPath(url, PosixPathStyle);

                if (path == IntPtr.Zero)
                {
                    return null;
                }

                this._owned.Add(path);
                return ReadString(path);
            }

            // Asks an element to act, like a window to come to the front of its app. False if it couldn't.
            public Boolean Perform(IntPtr element, String action) => AXUIElementPerformAction(element, this.Name(action)) == 0;

            // A closed window stops answering, even for its role.
            public Boolean IsAlive(IntPtr element) => this.Text(element, "AXRole") != null;

            public void Dispose()
            {
                foreach (var reference in this._owned.Concat(this._names.Values))
                {
                    CFRelease(reference);
                }
            }

            private List<IntPtr> Array(IntPtr element, String attribute)
            {
                var items = new List<IntPtr>();

                if (this.Copy(element, attribute) is IntPtr array && CFGetTypeID(array) == CFArrayGetTypeID())
                {
                    for (var i = 0L; i < CFArrayGetCount(array); i++)
                    {
                        items.Add(CFArrayGetValueAtIndex(array, i));
                    }
                }

                return items;
            }

            private IntPtr? Copy(IntPtr element, String attribute)
            {
                if (AXUIElementCopyAttributeValue(element, this.Name(attribute), out var value) != 0 || value == IntPtr.Zero)
                {
                    return null;
                }

                this._owned.Add(value);
                return value;
            }

            private IntPtr Name(String attribute)
            {
                if (!this._names.TryGetValue(attribute, out var name))
                {
                    name = CFStringCreateWithCharacters(IntPtr.Zero, attribute, attribute.Length);
                    this._names[attribute] = name;
                }

                return name;
            }

            private static String ReadString(IntPtr value)
            {
                var length = CFStringGetLength(value);
                var buffer = new Char[length];
                CFStringGetCharacters(value, new CFRange { Location = 0, Length = length }, buffer);
                return new String(buffer);
            }
        }

        private static IntPtr CFBooleanNamed(String name) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), name));

        [StructLayout(LayoutKind.Sequential)]
        private struct CFRange
        {
            public Int64 Location;
            public Int64 Length;
        }

        [DllImport(ApplicationServices)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern Boolean AXIsProcessTrusted();

        [DllImport(ApplicationServices)]
        private static extern IntPtr AXUIElementCreateApplication(Int32 pid);

        [DllImport(ApplicationServices)]
        private static extern Int32 AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

        [DllImport(ApplicationServices)]
        private static extern Int32 AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);

        [DllImport(ApplicationServices)]
        private static extern Int32 AXUIElementPerformAction(IntPtr element, IntPtr action);

        [DllImport(ApplicationServices)]
        private static extern UInt64 AXUIElementGetTypeID();

        [DllImport(CoreFoundation, CharSet = CharSet.Unicode)]
        private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, [MarshalAs(UnmanagedType.LPWStr)] String text, Int64 length);

        [DllImport(CoreFoundation)]
        private static extern Int64 CFStringGetLength(IntPtr text);

        [DllImport(CoreFoundation, CharSet = CharSet.Unicode)]
        private static extern void CFStringGetCharacters(IntPtr text, CFRange range, [Out] Char[] buffer);

        [DllImport(CoreFoundation)]
        private static extern Int64 CFArrayGetCount(IntPtr array);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, Int64 index);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern Boolean CFBooleanGetValue(IntPtr boolean);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFURLCopyFileSystemPath(IntPtr url, Int64 pathStyle);

        [DllImport(CoreFoundation)]
        private static extern UInt64 CFGetTypeID(IntPtr reference);

        [DllImport(CoreFoundation)]
        private static extern UInt64 CFStringGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern UInt64 CFArrayGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern UInt64 CFBooleanGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern UInt64 CFURLGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr reference);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFRetain(IntPtr reference);
    }
}
