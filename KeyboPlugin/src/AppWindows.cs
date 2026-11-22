namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    // Any app's window on a session key, for apps without their own support (Claude, Codex, Teams and Safari have it).
    // Pinning takes the window in front and a tap brings it back to the front, through Accessibility, the permission the
    // Claude keys already need. All knowledge of other apps' windows stays here.
    //
    // A pinned window's key ID is "app:<bundle ID>\n<name>\n<document>\n<title>", so it survives restarts with no file of
    // its own. While the window is open, the key holds on to the window itself, which stays exact whatever its title does.
    // After the window closes or its app restarts, a tap looks for it again: the same document, then the same title, then
    // a title with the key's name in it. If none matches, the tap opens the app, which usually brings its windows back, and
    // keeps looking for a few seconds. So a pinned window is never gone; at worst its key opens the app.
    //
    // The key turns green with the app's Dock badge, read from the Dock's own Accessibility tree (lsappinfo doesn't report
    // badges), and goes dark while the app isn't running. Window titles can be private: they stay on the key, never logged.

    public static class AppWindows
    {
        public const String IdPrefix = "app:";

        private const Int32 LaunchChecks = 12;
        private const Int32 LaunchCheckMilliseconds = 350;
        private const Int32 ActivationDelayMilliseconds = 150;

        // Titles join their parts, such as the file, the folder and the app, with one of these.
        private static readonly String[] TitleSeparators = { " — ", " – ", " - ", " | " };
        private static readonly Regex FileName = new Regex(@"^[^\s/]+\.[A-Za-z0-9]{1,6}$", RegexOptions.Compiled);
        private static readonly Regex ValidBundleId = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.-]*$", RegexOptions.Compiled);

        // Pinned windows held while they're open (retained), by key ID, and the latest state of each pinned app.
        private static readonly Object StateLock = new Object();
        private static readonly Dictionary<String, (IntPtr Window, Int32 Pid)> Windows = new Dictionary<String, (IntPtr, Int32)>();
        private static HashSet<String> _watched = new HashSet<String>();
        private static Dictionary<String, AppState> _states = new Dictionary<String, AppState>();
        private static Boolean _warnedUntrusted;

        // The app behind each Dock icon, by path. Only Poll uses it, on the store's refresh.
        private static readonly Dictionary<String, String> DockApps = new Dictionary<String, String>();

        private record Pin(String BundleId, String Name, String Document, String Title);

        private record AppState(Boolean Running, String Badge);

        public static Boolean IsAppId(String id) => id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal);

        // The session-key entry for a pinned window, from its key ID alone.
        public static SessionInfo FromId(String id)
        {
            if (Parse(id) is not Pin pin)
            {
                return null;
            }

            AppState state;

            lock (StateLock)
            {
                state = _states.GetValueOrDefault(pin.BundleId);
            }

            var status = state == null ? SessionStatus.Idle
                : !state.Running ? SessionStatus.NotRunning
                : state.Badge != null ? SessionStatus.Done
                : SessionStatus.Idle;
            return new SessionInfo(id, pin.Name, status, null, IsApp: true, App: pin.BundleId, Badge: state?.Badge);
        }

        // The front window of the app in front, or just that app when Accessibility can't see a window.
        public static SessionInfo ReadFront(ClaudeApp.FrontApp front)
        {
            if (front?.BundleId == null || !ValidBundleId.IsMatch(front.BundleId))
            {
                return null;
            }

            var (window, title, document) = ReadFocusedWindow(front.Pid);
            var id = IdPrefix + String.Join("\n", front.BundleId, ShortName(title, front.Name ?? front.BundleId), document ?? String.Empty, title ?? String.Empty);

            if (window != IntPtr.Zero)
            {
                Remember(id, window, front.Pid);
            }

            return FromId(id);
        }

        // The session keys say which windows are pinned, so only their apps are watched.
        public static void Watch(IEnumerable<String> keyIds)
        {
            var ids = keyIds.Where(IsAppId).ToHashSet();

            lock (StateLock)
            {
                foreach (var unpinned in Windows.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    Forget(unpinned);
                }

                _watched = ids;
            }
        }

        // Called on every store refresh. True if a pinned app started or quit, or its Dock badge changed.
        public static Boolean Poll()
        {
            HashSet<String> bundleIds;

            lock (StateLock)
            {
                bundleIds = _watched.Select(Parse).Where(pin => pin != null).Select(pin => pin.BundleId).ToHashSet();
            }

            var states = new Dictionary<String, AppState>();

            if (bundleIds.Count > 0)
            {
                var badges = ReadDockBadges();

                foreach (var bundleId in bundleIds)
                {
                    states[bundleId] = new AppState(Mac.AppPids(bundleId).Count > 0, badges?.GetValueOrDefault(bundleId));
                }
            }

            lock (StateLock)
            {
                var changed = states.Count != _states.Count || states.Any(s => _states.GetValueOrDefault(s.Key) != s.Value);
                _states = states;
                return changed;
            }
        }

        // Brings the pinned window to the front, or opens its app and looks again. It runs in the background, since an app
        // can take a few seconds to start.
        public static void Open(String id)
        {
            if (Parse(id) is not Pin pin)
            {
                PluginLog.Warning("Not a usable app pin");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    if (Raise(id, pin))
                    {
                        return;
                    }

                    Shell.Run("/usr/bin/open", "-b", pin.BundleId);

                    for (var check = 0; check < LaunchChecks && Accessibility.IsTrusted(); check++)
                    {
                        Thread.Sleep(LaunchCheckMilliseconds);

                        if (Raise(id, pin))
                        {
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "Couldn't bring a pinned window forward");
                }
            });
        }

        // A window's name for its key: the first part of its title that isn't the app's own name or a file, so a VS Code
        // window shows its folder rather than the file you're in. A title of only a file gives the file's name, and no
        // title gives the app's.
        internal static String ShortName(String title, String appName)
        {
            var parts = Parts(title).Where(part => !part.Equals(appName, StringComparison.OrdinalIgnoreCase)).ToList();
            return parts.FirstOrDefault(part => !FileName.IsMatch(part))
                ?? parts.Select(Path.GetFileNameWithoutExtension).FirstOrDefault(part => part.Length > 0)
                ?? appName;
        }

        // A title's parts, without the marks editors put before an unsaved file.
        private static IEnumerable<String> Parts(String title) =>
            (title ?? String.Empty)
                .Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.TrimStart('●', '•', '*').Trim())
                .Where(part => part.Length > 0);

        // An app's front window, retained for the caller, with its title and document. Zero when Accessibility isn't
        // allowed or the app has no window it can see.
        private static (IntPtr Window, String Title, String Document) ReadFocusedWindow(Int32 pid)
        {
            if (!IsTrusted())
            {
                return (IntPtr.Zero, null, null);
            }

            try
            {
                using var tree = new Accessibility.Tree(pid);
                var window = tree.Element(tree.App, "AXFocusedWindow");

                if (window == IntPtr.Zero)
                {
                    window = tree.Element(tree.App, "AXMainWindow");
                }

                return window == IntPtr.Zero
                    ? (IntPtr.Zero, null, null)
                    : (Accessibility.Retain(window), Clean(tree.Text(window, "AXTitle")), Clean(tree.Text(window, "AXDocument")));
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                PluginLog.Warning(ex, "Could not read windows through Accessibility");
                return (IntPtr.Zero, null, null);
            }
        }

        // Raises the pinned window, or one like it, and brings its app forward. False if no such window is open.
        private static Boolean Raise(String id, Pin pin)
        {
            var (window, pid) = FindWindow(id, pin);

            if (window == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                using var tree = new Accessibility.Tree(pid);

                if (tree.Flag(window, "AXMinimized"))
                {
                    tree.Set(window, "AXMinimized", false);
                }

                tree.Perform(window, "AXRaise");
                tree.Set(window, "AXMain", true);
                tree.Set(tree.App, "AXFrontmost", true);
            }
            finally
            {
                Accessibility.Release(window);
            }

            // AXFrontmost doesn't always win from a background process; open -b brings the app forward when it didn't.
            Thread.Sleep(ActivationDelayMilliseconds);

            if (ClaudeApp.Front()?.Pid != pid)
            {
                Shell.Run("/usr/bin/open", "-b", pin.BundleId);
            }

            return true;
        }

        // The pinned window while it's open, or else the one most like it, which the key then holds on to. The window is
        // retained for the caller.
        private static (IntPtr Window, Int32 Pid) FindWindow(String id, Pin pin)
        {
            if (!IsTrusted())
            {
                return (IntPtr.Zero, 0);
            }

            (IntPtr Window, Int32 Pid) held;

            lock (StateLock)
            {
                held = Windows.TryGetValue(id, out var known) ? (Accessibility.Retain(known.Window), known.Pid) : (IntPtr.Zero, 0);
            }

            if (held.Window != IntPtr.Zero)
            {
                using (var check = new Accessibility.Tree(held.Pid))
                {
                    if (check.IsAlive(held.Window))
                    {
                        return held;
                    }
                }

                Accessibility.Release(held.Window);

                lock (StateLock)
                {
                    Forget(id);
                }
            }

            foreach (var pid in Mac.AppPids(pin.BundleId))
            {
                using var tree = new Accessibility.Tree(pid);
                var match = Match(tree, pin);

                if (match != IntPtr.Zero)
                {
                    Remember(id, Accessibility.Retain(match), pid);
                    return (Accessibility.Retain(match), pid);
                }
            }

            return (IntPtr.Zero, 0);
        }

        // The window most like the pinned one: the same document, then the same title, then a title with the key's name in it.
        private static IntPtr Match(Accessibility.Tree tree, Pin pin)
        {
            var windows = tree.Children(tree.App, "AXWindows")
                .Select(window => (Window: window, Title: Clean(tree.Text(window, "AXTitle")) ?? String.Empty, Document: Clean(tree.Text(window, "AXDocument")) ?? String.Empty))
                .ToList();
            var match = pin.Document.Length > 0 ? windows.FirstOrDefault(w => w.Document == pin.Document).Window : IntPtr.Zero;

            if (match == IntPtr.Zero && pin.Title.Length > 0)
            {
                match = windows.FirstOrDefault(w => w.Title == pin.Title).Window;
            }

            return match != IntPtr.Zero ? match : windows.FirstOrDefault(w => Parts(w.Title).Contains(pin.Name)).Window;
        }

        // Each Dock icon's badge, by app. Null when the Dock can't be read, as without Accessibility.
        private static Dictionary<String, String> ReadDockBadges()
        {
            if (!Accessibility.IsTrusted())
            {
                return null;
            }

            using var dock = Process.GetProcessesByName("Dock").FirstOrDefault();

            if (dock == null)
            {
                return null;
            }

            try
            {
                using var tree = new Accessibility.Tree(dock.Id);
                var badges = new Dictionary<String, String>();

                foreach (var list in tree.Children(tree.App, "AXChildren").Where(child => tree.Text(child, "AXRole") == "AXList"))
                {
                    foreach (var icon in tree.Children(list, "AXChildren"))
                    {
                        var badge = tree.Text(icon, "AXStatusLabel")?.Trim();

                        if (!String.IsNullOrEmpty(badge) && tree.FilePath(icon, "AXURL") is String path && DockApp(path) is String bundleId)
                        {
                            badges[bundleId] = badge;
                        }
                    }
                }

                return badges;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                PluginLog.Warning(ex, "Could not read the Dock through Accessibility");
                return null;
            }
        }

        private static String DockApp(String path)
        {
            if (!DockApps.TryGetValue(path, out var bundleId))
            {
                bundleId = DockApps[path] = Mac.BundleIdOf(path);
            }

            return bundleId;
        }

        private static Boolean IsTrusted()
        {
            if (Accessibility.IsTrusted())
            {
                return true;
            }

            if (!_warnedUntrusted)
            {
                PluginLog.Warning("App pins: Logi Plugin Service needs Accessibility permission");
                _warnedUntrusted = true;
            }

            return false;
        }

        // Holds on to a window for its key, taking over the caller's retain.
        private static void Remember(String id, IntPtr window, Int32 pid)
        {
            lock (StateLock)
            {
                Forget(id);
                Windows[id] = (window, pid);
            }
        }

        // Lets go of a key's window. StateLock must be held.
        private static void Forget(String id)
        {
            if (Windows.Remove(id, out var held))
            {
                Accessibility.Release(held.Window);
            }
        }

        private static Pin Parse(String id)
        {
            if (!IsAppId(id))
            {
                return null;
            }

            var parts = id.Substring(IdPrefix.Length).Split('\n');
            return parts.Length == 4 && ValidBundleId.IsMatch(parts[0]) && parts[1].Length > 0 ? new Pin(parts[0], parts[1], parts[2], parts[3]) : null;
        }

        // Titles and documents go into the key's ID, which is split at line breaks.
        private static String Clean(String text) => text?.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
