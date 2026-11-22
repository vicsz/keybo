namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;

    // Calls into macOS: the kernel, Foundation, Wi-Fi, app icons, where apps live and run, and process paths. IOKit hardware
    // readings are in Sensors.cs.

    internal static class Mac
    {
        private const String System = "/usr/lib/libSystem.dylib";
        private const String ObjC = "/usr/lib/libobjc.dylib";
        private const String CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const Int32 HostCpuLoadInfo = 3;
        private const Int32 HostVmInfo64 = 4;
        private const Int32 VmInfo64Count = 38;

        private static readonly UInt32 Host = mach_host_self();

        static Mac()
        {
            NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
            NativeLibrary.Load("/System/Library/Frameworks/CoreWLAN.framework/CoreWLAN");
            NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        }

        // Total CPU ticks for all cores since boot: busy (user, system, nice) and idle.
        public static (UInt64 Busy, UInt64 Idle) CpuTicks()
        {
            var ticks = new UInt32[4];
            var count = 4;

            if (host_statistics(Host, HostCpuLoadInfo, ticks, ref count) != 0)
            {
                return (0, 0);
            }

            return ((UInt64)ticks[0] + ticks[1] + ticks[3], ticks[2]);
        }

        // Used memory the way Activity Monitor counts it: app memory (anonymous pages that can't be purged),
        // wired memory, and the pages the compressor holds.
        public static UInt64 MemoryUsed()
        {
            var info = new UInt32[VmInfo64Count];
            var count = VmInfo64Count;

            if (host_statistics64(Host, HostVmInfo64, info, ref count) != 0)
            {
                return 0;
            }

            const Int32 wired = 3, purgeable = 22, compressor = 32, anonymous = 35;
            var pages = (Int64)info[anonymous] - info[purgeable] + info[wired] + info[compressor];
            return (UInt64)Math.Max(0, pages) * SysctlLong("hw.pagesize");
        }

        public static UInt64 MemoryTotal() => SysctlLong("hw.memsize");

        // 1 is normal, 2 is warning, 4 is critical.
        public static Int32 MemoryPressureLevel() => SysctlInt("kern.memorystatus_vm_pressure_level");

        // The share of memory the kernel considers free, 0 to 100. Activity Monitor's pressure is 100 minus this.
        public static Int32 MemoryFreeLevel() => SysctlInt("kern.memorystatus_level");

        // Seconds since the last keyboard, mouse or trackpad input, from the window server (combined session state, any event).
        public static Double IdleSeconds() => CGEventSourceSecondsSinceLastEventType(0, UInt32.MaxValue);

        // Free space on the startup disk as Finder shows it, which counts purgeable space as free.
        // Falls back to plain free space if Foundation can't answer.
        public static (Int64 Free, Int64 Total) Disk()
        {
            var drive = new global::System.IO.DriveInfo("/");
            var free = VolumeValue("NSURLVolumeAvailableCapacityForImportantUsageKey");
            return (free > 0 ? free : drive.AvailableFreeSpace, drive.TotalSize);
        }

        private static Int64 VolumeValue(String key)
        {
            var pool = objc_autoreleasePoolPush();

            try
            {
                var url = Send(Class("NSURL"), "fileURLWithPath:", NSString("/"));
                var value = IntPtr.Zero;

                if (url == IntPtr.Zero || SendGet(url, Selector("getResourceValue:forKey:error:"), ref value, NSString(key), IntPtr.Zero) == 0 || value == IntPtr.Zero)
                {
                    return 0;
                }

                return SendLong(value, Selector("longLongValue"));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Couldn't read " + key);
                return 0;
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        // The Wi-Fi interface's name (like en1), signal and noise in dBm, and link rate in Mb/s.
        // Signal is 0 when Wi-Fi isn't connected.
        public static (String Name, Int64 Rssi, Int64 Noise, Double Rate) Wifi()
        {
            var pool = objc_autoreleasePoolPush();

            try
            {
                var client = objc_msgSend(Class("CWWiFiClient"), Selector("sharedWiFiClient"));
                var wifi = client == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(client, Selector("interface"));

                if (wifi == IntPtr.Zero)
                {
                    return (null, 0, 0, 0);
                }

                var name = objc_msgSend(wifi, Selector("interfaceName"));
                var text = name == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(name, Selector("UTF8String"));
                return (Marshal.PtrToStringUTF8(text), SendLong(wifi, Selector("rssiValue")), SendLong(wifi, Selector("noiseMeasurement")),
                    SendDouble(wifi, Selector("transmitRate")));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Couldn't read Wi-Fi");
                return (null, 0, 0, 0);
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        // The executable a process runs, or null once it has gone.
        public static String ProcessPath(Int32 pid)
        {
            var buffer = new Byte[4096];
            var length = proc_pidpath(pid, buffer, (UInt32)buffer.Length);
            return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
        }

        // Where an app is installed, from its bundle ID, or null if it isn't. LaunchServices gives the real place, so an app
        // reached through a link (/Applications/Safari.app links into a system volume) doesn't get Finder's alias arrow.
        public static String AppPath(String bundleId)
        {
            var pool = objc_autoreleasePoolPush();

            try
            {
                var workspace = objc_msgSend(Class("NSWorkspace"), Selector("sharedWorkspace"));
                var url = Send(workspace, "URLForApplicationWithBundleIdentifier:", NSString(bundleId));
                return url == IntPtr.Zero ? null : ReadString(objc_msgSend(url, Selector("path")));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Couldn't find the app {bundleId}");
                return null;
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        // The processes of an app's running copies, usually one. Empty when it isn't running.
        public static List<Int32> AppPids(String bundleId)
        {
            var pids = new List<Int32>();
            var pool = objc_autoreleasePoolPush();

            try
            {
                var apps = Send(Class("NSRunningApplication"), "runningApplicationsWithBundleIdentifier:", NSString(bundleId));
                var count = apps == IntPtr.Zero ? 0 : SendLong(apps, Selector("count"));

                for (var i = 0L; i < count; i++)
                {
                    var pid = (Int32)SendLong(SendIndex(apps, Selector("objectAtIndex:"), (UInt64)i), Selector("processIdentifier"));

                    if (pid > 0)
                    {
                        pids.Add(pid);
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Couldn't list the processes of {bundleId}");
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }

            return pids;
        }

        // The bundle ID of the app at a path, or null.
        public static String BundleIdOf(String appPath)
        {
            var pool = objc_autoreleasePoolPush();

            try
            {
                var bundle = Send(Class("NSBundle"), "bundleWithPath:", NSString(appPath));
                return bundle == IntPtr.Zero ? null : ReadString(objc_msgSend(bundle, Selector("bundleIdentifier")));
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Couldn't read the bundle at {appPath}");
                return null;
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        // An app's icon as a PNG about pixels across, the one Finder shows, or null if macOS has none for it.
        // Faded is grey at half strength, for a key whose app isn't running.
        public static Byte[] AppIcon(String appPath, Int32 pixels, Boolean faded = false)
        {
            var pool = objc_autoreleasePoolPush();
            var grey = IntPtr.Zero;

            try
            {
                var workspace = objc_msgSend(Class("NSWorkspace"), Selector("sharedWorkspace"));
                var image = Send(workspace, "iconForFile:", NSString(appPath));
                var rect = new Rect { Width = pixels, Height = pixels };
                var picture = image == IntPtr.Zero ? IntPtr.Zero : SendImage(image, Selector("CGImageForProposedRect:context:hints:"), ref rect, IntPtr.Zero, IntPtr.Zero);

                if (picture != IntPtr.Zero && faded)
                {
                    picture = grey = Grey(picture, pixels);
                }

                if (picture == IntPtr.Zero)
                {
                    return null;
                }

                var bitmap = Send(objc_msgSend(Class("NSBitmapImageRep"), Selector("alloc")), "initWithCGImage:", picture);

                try
                {
                    const UInt64 Png = 4;
                    var data = SendPng(bitmap, Selector("representationUsingType:properties:"), Png, objc_msgSend(Class("NSDictionary"), Selector("dictionary")));

                    if (data == IntPtr.Zero)
                    {
                        return null;
                    }

                    var bytes = new Byte[SendLong(data, Selector("length"))];
                    Marshal.Copy(objc_msgSend(data, Selector("bytes")), bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    objc_msgSend(bitmap, Selector("release"));
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Couldn't read the icon for {appPath}");
                return null;
            }
            finally
            {
                if (grey != IntPtr.Zero)
                {
                    CGImageRelease(grey);
                }

                objc_autoreleasePoolPop(pool);
            }
        }

        // The icon in grey at half strength: drawn, turned grey by a saturation blend (which also fills the transparent
        // corners), then cut back to the icon's own shape at half its alpha. The caller releases the image.
        private static IntPtr Grey(IntPtr image, Int32 pixels)
        {
            const UInt32 PremultipliedLast = 1;
            const Int32 SaturationBlend = 13, DestinationInBlend = 22;
            var space = CGColorSpaceCreateDeviceRGB();
            var context = CGBitmapContextCreate(IntPtr.Zero, (UIntPtr)pixels, (UIntPtr)pixels, 8, UIntPtr.Zero, space, PremultipliedLast);
            CGColorSpaceRelease(space);

            if (context == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                var rect = new Rect { Width = pixels, Height = pixels };
                CGContextDrawImage(context, rect, image);
                CGContextSetBlendMode(context, SaturationBlend);
                CGContextSetRGBFillColor(context, 0.5, 0.5, 0.5, 1);
                CGContextFillRect(context, rect);
                CGContextSetBlendMode(context, DestinationInBlend);
                CGContextSetAlpha(context, 0.5);
                CGContextDrawImage(context, rect, image);
                return CGBitmapContextCreateImage(context);
            }
            finally
            {
                CGContextRelease(context);
            }
        }

        private static UInt64 SysctlLong(String name)
        {
            UInt64 value = 0;
            var size = (UIntPtr)8;
            return sysctlbyname(name, ref value, ref size, IntPtr.Zero, UIntPtr.Zero) == 0 ? value : 0;
        }

        private static Int32 SysctlInt(String name)
        {
            var value = 0;
            var size = (UIntPtr)4;
            return sysctlbyname(name, ref value, ref size, IntPtr.Zero, UIntPtr.Zero) == 0 ? value : 0;
        }

        private static IntPtr NSString(String text) => Send(Class("NSString"), "stringWithUTF8String:", Encoding.UTF8.GetBytes(text + "\0"));

        private static String ReadString(IntPtr text) => text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(objc_msgSend(text, Selector("UTF8String")));

        private static IntPtr Class(String name) => objc_getClass(name);

        private static IntPtr Selector(String name) => sel_registerName(name);

        private static IntPtr Send(IntPtr target, String selector, IntPtr argument) => objc_msgSend(target, Selector(selector), argument);

        private static IntPtr Send(IntPtr target, String selector, Byte[] argument) => objc_msgSend(target, Selector(selector), argument);

        [DllImport(CoreGraphics)]
        private static extern Double CGEventSourceSecondsSinceLastEventType(Int32 source, UInt32 eventType);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGColorSpaceCreateDeviceRGB();

        [DllImport(CoreGraphics)]
        private static extern void CGColorSpaceRelease(IntPtr space);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGBitmapContextCreate(IntPtr data, UIntPtr width, UIntPtr height, UIntPtr bitsPerComponent, UIntPtr bytesPerRow, IntPtr space, UInt32 bitmapInfo);

        [DllImport(CoreGraphics)]
        private static extern void CGContextDrawImage(IntPtr context, Rect rect, IntPtr image);

        [DllImport(CoreGraphics)]
        private static extern void CGContextSetBlendMode(IntPtr context, Int32 mode);

        [DllImport(CoreGraphics)]
        private static extern void CGContextSetRGBFillColor(IntPtr context, Double red, Double green, Double blue, Double alpha);

        [DllImport(CoreGraphics)]
        private static extern void CGContextFillRect(IntPtr context, Rect rect);

        [DllImport(CoreGraphics)]
        private static extern void CGContextSetAlpha(IntPtr context, Double alpha);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGBitmapContextCreateImage(IntPtr context);

        [DllImport(CoreGraphics)]
        private static extern void CGContextRelease(IntPtr context);

        [DllImport(CoreGraphics)]
        private static extern void CGImageRelease(IntPtr image);

        [DllImport(System)]
        private static extern UInt32 mach_host_self();

        [DllImport(System)]
        private static extern Int32 host_statistics(UInt32 host, Int32 flavor, [Out] UInt32[] info, ref Int32 count);

        [DllImport(System)]
        private static extern Int32 host_statistics64(UInt32 host, Int32 flavor, [Out] UInt32[] info, ref Int32 count);

        [DllImport(System)]
        private static extern Int32 sysctlbyname(String name, ref UInt64 value, ref UIntPtr size, IntPtr newValue, UIntPtr newSize);

        [DllImport(System)]
        private static extern Int32 sysctlbyname(String name, ref Int32 value, ref UIntPtr size, IntPtr newValue, UIntPtr newSize);

        [DllImport(System)]
        private static extern Int32 proc_pidpath(Int32 pid, [Out] Byte[] buffer, UInt32 bufferSize);

        [DllImport(ObjC)]
        private static extern IntPtr objc_getClass(String name);

        [DllImport(ObjC)]
        private static extern IntPtr sel_registerName(String name);

        [DllImport(ObjC)]
        private static extern IntPtr objc_autoreleasePoolPush();

        [DllImport(ObjC)]
        private static extern void objc_autoreleasePoolPop(IntPtr pool);

        [DllImport(ObjC)]
        private static extern IntPtr objc_msgSend(IntPtr target, IntPtr selector);

        [DllImport(ObjC)]
        private static extern IntPtr objc_msgSend(IntPtr target, IntPtr selector, IntPtr argument);

        [DllImport(ObjC)]
        private static extern IntPtr objc_msgSend(IntPtr target, IntPtr selector, Byte[] argument);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern Byte SendGet(IntPtr target, IntPtr selector, ref IntPtr value, IntPtr key, IntPtr error);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern Int64 SendLong(IntPtr target, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendIndex(IntPtr target, IntPtr selector, UInt64 index);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern Double SendDouble(IntPtr target, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendImage(IntPtr target, IntPtr selector, ref Rect rect, IntPtr context, IntPtr hints);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPng(IntPtr target, IntPtr selector, UInt64 type, IntPtr properties);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public Double X;
            public Double Y;
            public Double Width;
            public Double Height;
        }
    }
}
