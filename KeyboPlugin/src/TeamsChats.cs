namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text.RegularExpressions;
    using System.Threading;

    // Teams chats for the session keys: which chat is open, and opening one. Teams has no API on this Mac, so it reads
    // Teams' window through Accessibility (Logi Plugin Service needs that permission). All Teams knowledge stays here.
    // It reads only the chat list's structure: the open chat's ID and the window title, never messages.
    //
    // Undocumented, found by probing Teams 26289 (English UI):
    // - Teams' web view builds its accessibility tree only once asked (AXManualAccessibility; it returns an error but works).
    // - The main window is titled "Chat | <names>" while a chat is open. Chat list rows are AXRows titled
    //   "Chat …", "Group chat …" or "Meeting chat …".
    // - Each row has a descendant whose DOM ID is "title-chat-list-item_<chat ID>" (19:…@unq.gbl.spaces or 19:…@thread.v2).
    // - The open chat's row has its own DOM class list; the rest share one. AXSelected is always false.
    // - msteams:/l/chat/<chat ID>/0 opens a chat and brings Teams forward.
    //
    // A pinned chat's key ID is "teams:<chat ID>|<name>", so the name survives restarts with no file of its own.
    //
    // Unread: a pinned chat turns green while its row's title says "Unread" (an unread row starts "Unread message …").
    // With a remembered handle to the chat list, each refresh reads just its rows (about 5 ms). Without one (Teams
    // restarted, or showing Calendar or Activity), the slow full search (about 110 ms) runs only when Teams' Dock badge
    // changes or every 30 s. Muted chats don't change the badge, which is why the rows are read directly.
    // Nothing runs unless a Teams chat is pinned.

    public static class TeamsChats
    {
        public const String BundleId = "com.microsoft.teams2";
        public const String IdPrefix = "teams:";

        private const String ProcessName = "MSTeams";
        private const String ChatTitlePrefix = "Chat | ";
        private const String RowIdPrefix = "title-chat-list-item_";
        private const Int32 MaxDepth = 60;
        private const Int32 MaxNodes = 6000;
        private const Int32 TreeBuildMilliseconds = 400;
        private const Int32 ListDepth = 8;

        private static readonly Regex ChatRowTitle = new Regex(@"^(Unread messages? )?(Chat|Group chat|Meeting chat) ", RegexOptions.Compiled);
        private static readonly Regex ValidChatId = new Regex(@"^19:[A-Za-z0-9_.:@-]+$", RegexOptions.Compiled);

        private const String DockName = "Microsoft Teams";
        private static readonly TimeSpan SearchAgainAfter = TimeSpan.FromSeconds(30);

        // After a tap opens a chat: its green stays off this long, so Teams has time to mark it read, and the chat list
        // is searched for on every refresh this long, since opening the chat switches Teams to its Chat view.
        private static readonly TimeSpan QuietAfterOpen = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SearchAfterOpen = TimeSpan.FromSeconds(10);
        private static readonly Regex UnreadWord = new Regex(@"\bUnread\b", RegexOptions.Compiled);

        private static readonly Object StateLock = new Object();
        private static Boolean _warnedUntrusted;
        private static HashSet<String> _watched = new HashSet<String>();
        private static HashSet<String> _unread = new HashSet<String>();
        private static String _lastBadge;
        private static DateTime _lastRead = DateTime.MinValue;
        private static DateTime _searchUntil = DateTime.MinValue;
        private static readonly Dictionary<String, DateTime> QuietUntil = new Dictionary<String, DateTime>();

        // A handle to Teams' chat list, kept between reads (retained), and the Teams process it belongs to.
        private static IntPtr _list;
        private static Int32 _listPid;

        public static Boolean IsTeamsId(String id) => id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal);

        // The session-key entry for a pinned chat, from its key ID alone.
        public static SessionInfo FromId(String id)
        {
            if (!IsTeamsId(id))
            {
                return null;
            }

            var rest = id.Substring(IdPrefix.Length);
            var bar = rest.IndexOf('|');
            var name = bar >= 0 ? rest.Substring(bar + 1) : "Teams chat";
            var chatId = bar >= 0 ? rest.Substring(0, bar) : rest;
            Boolean unread;

            lock (StateLock)
            {
                unread = _unread.Contains(chatId);
            }

            return new SessionInfo(id, name.Length > 0 ? name : "Teams chat", unread ? SessionStatus.Done : SessionStatus.Idle, null, IsTeams: true);
        }

        // The session keys say which Teams chats are pinned, so unread is only watched for those.
        public static void Watch(IEnumerable<String> keyIds)
        {
            var chatIds = keyIds.Where(IsTeamsId).Select(id => id.Substring(IdPrefix.Length).Split('|')[0]).ToHashSet();

            lock (StateLock)
            {
                if (!chatIds.SetEquals(_watched))
                {
                    _watched = chatIds;
                    _lastBadge = null;
                }
            }
        }

        // Called on every store refresh. True if a pinned chat turned unread or read.
        public static Boolean Poll()
        {
            HashSet<String> watched;

            lock (StateLock)
            {
                watched = _watched;
            }

            if (watched.Count == 0)
            {
                return SetUnread(new HashSet<String>());
            }

            Boolean haveList;

            lock (StateLock)
            {
                haveList = _list != IntPtr.Zero;
            }

            if (!haveList)
            {
                var badge = Shell.Run("/usr/bin/lsappinfo", "info", "-only", "StatusLabel", DockName).Output.Trim();

                lock (StateLock)
                {
                    if (badge == _lastBadge && DateTime.UtcNow - _lastRead < SearchAgainAfter && DateTime.UtcNow > _searchUntil)
                    {
                        return false;
                    }

                    _lastBadge = badge;
                    _lastRead = DateTime.UtcNow;
                }
            }

            try
            {
                return ReadUnread(watched) is HashSet<String> unread && SetUnread(unread);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                PluginLog.Warning(ex, "Could not read Teams through Accessibility");
                return false;
            }
        }

        private static Boolean SetUnread(HashSet<String> unread)
        {
            lock (StateLock)
            {
                var now = DateTime.UtcNow;

                foreach (var (chatId, until) in QuietUntil.ToList())
                {
                    if (now > until)
                    {
                        QuietUntil.Remove(chatId);
                    }
                    else
                    {
                        unread.Remove(chatId);
                    }
                }

                if (unread.SetEquals(_unread))
                {
                    return false;
                }

                _unread = unread;
                return true;
            }
        }

        // Which of these chats are unread, from the chat list's rows. Null if the list can't be read right now
        // (Teams closed, another view open), so the keys keep what they showed. Chats not in the list count as read.
        public static HashSet<String> ReadUnread(HashSet<String> watched)
        {
            if (!AXIsProcessTrusted())
            {
                return null;
            }

            using var process = Process.GetProcessesByName(ProcessName).FirstOrDefault();

            if (process == null)
            {
                ForgetList();
                return new HashSet<String>();
            }

            using var tree = new Tree(process.Id);
            var list = RememberedList(tree, process.Id);

            if (list == IntPtr.Zero)
            {
                tree.Set(tree.App, "AXManualAccessibility", true);

                foreach (var window in tree.Children(tree.App, "AXWindows"))
                {
                    if (tree.Text(window, "AXTitle")?.StartsWith(ChatTitlePrefix, StringComparison.Ordinal) == true
                        && FindChatRows(tree, window).Select(r => r.List).FirstOrDefault(l => l != IntPtr.Zero) is IntPtr found && found != IntPtr.Zero)
                    {
                        list = RememberList(found, process.Id);
                        break;
                    }
                }

                if (list == IntPtr.Zero)
                {
                    return null;
                }
            }

            var unread = new HashSet<String>();
            var stack = new Stack<(IntPtr Element, Int32 Depth)>(tree.Children(list, "AXChildren").Select(c => (c, 1)));

            while (stack.Count > 0)
            {
                var (element, depth) = stack.Pop();
                var title = tree.Text(element, "AXRole") == "AXRow" ? tree.Text(element, "AXTitle") : null;

                if (title != null && ChatRowTitle.IsMatch(title))
                {
                    if (UnreadWord.IsMatch(title) && FindChatId(tree, element) is String chatId && watched.Contains(chatId))
                    {
                        unread.Add(chatId);
                    }

                    // A chat row's own contents are its name and preview: nothing more to find inside.
                    continue;
                }

                // Section rows ("Chats", "Teams and channels") hold their chats inside a group, so look through both.
                if (depth < ListDepth)
                {
                    foreach (var child in tree.Children(element, "AXChildren"))
                    {
                        stack.Push((child, depth + 1));
                    }
                }
            }

            return unread;
        }

        private static IntPtr RememberedList(Tree tree, Int32 pid)
        {
            lock (StateLock)
            {
                if (_list != IntPtr.Zero && (_listPid != pid || tree.Text(_list, "AXRole") != "AXOutline"))
                {
                    ForgetList();
                }

                return _list;
            }
        }

        private static IntPtr RememberList(IntPtr list, Int32 pid)
        {
            lock (StateLock)
            {
                ForgetList();
                _list = CFRetain(list);
                _listPid = pid;
                return _list;
            }
        }

        private static void ForgetList()
        {
            lock (StateLock)
            {
                if (_list != IntPtr.Zero)
                {
                    CFRelease(_list);
                    _list = IntPtr.Zero;
                }
            }
        }

        // The chat open in Teams' main window, or null if none is (another view, a call window in front, or no permission).
        public static SessionInfo ReadOpenChat()
        {
            try
            {
                var read = ReadOpenChatOnce();

                if (read == null)
                {
                    // Right after Teams starts, its tree builds only once asked, so ask again shortly.
                    Thread.Sleep(TreeBuildMilliseconds);
                    read = ReadOpenChatOnce();
                }

                if (read == null)
                {
                    PluginLog.Info("Teams: no open chat found");
                    return null;
                }

                return FromId($"{IdPrefix}{read.Value.ChatId}|{ShortName(read.Value.Title)}");
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                PluginLog.Warning(ex, "Could not read Teams through Accessibility");
                return null;
            }
        }

        public static void Open(String id)
        {
            var rest = IsTeamsId(id) ? id.Substring(IdPrefix.Length) : null;
            var chatId = rest?.Split('|')[0];

            if (chatId == null || !ValidChatId.IsMatch(chatId))
            {
                PluginLog.Warning("Not a usable Teams chat ID");
                return;
            }

            // Clear its green now, like a Claude session's. If Teams still says unread once the quiet time is over, it comes back.
            lock (StateLock)
            {
                _unread.Remove(chatId);
                QuietUntil[chatId] = DateTime.UtcNow + QuietAfterOpen;
                _searchUntil = DateTime.UtcNow + SearchAfterOpen;
            }

            Shell.Start("/usr/bin/open", $"msteams:/l/chat/{Uri.EscapeDataString(chatId)}/0");
        }

        // "Doe, Jane" reads better on a key as "Jane Doe". Group and meeting names stay as they are.
        private static String ShortName(String title)
        {
            var parts = title.Split(", ");
            return parts.Length == 2 && !parts[1].Contains(' ') ? $"{parts[1]} {parts[0]}" : title;
        }

        private static (String ChatId, String Title)? ReadOpenChatOnce()
        {
            if (!AXIsProcessTrusted())
            {
                if (!_warnedUntrusted)
                {
                    PluginLog.Warning("Teams: Logi Plugin Service needs Accessibility permission");
                    _warnedUntrusted = true;
                }

                return null;
            }

            using var process = Process.GetProcessesByName(ProcessName).FirstOrDefault();

            if (process == null)
            {
                return null;
            }

            using var tree = new Tree(process.Id);
            tree.Set(tree.App, "AXManualAccessibility", true);

            foreach (var window in tree.Children(tree.App, "AXWindows"))
            {
                var title = tree.Text(window, "AXTitle");

                if (title == null || !title.StartsWith(ChatTitlePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var rows = FindChatRows(tree, window);
                var common = rows.GroupBy(r => r.Classes).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
                var names = title.Substring(ChatTitlePrefix.Length);

                // The open chat is styled apart. If more than one row is, the window title picks between them.
                var open = rows.Where(r => r.Classes != common).ToList();

                if (open.Count > 1)
                {
                    open = open.Where(r => r.Title.Contains(names, StringComparison.Ordinal)).ToList();
                }

                if (open.Count == 1 && FindChatId(tree, open[0].Element) is String chatId)
                {
                    return (chatId, names);
                }
            }

            return null;
        }

        // Every chat row in the window, with the list (AXOutline) it sits in.
        private static List<(IntPtr Element, String Title, String Classes, IntPtr List)> FindChatRows(Tree tree, IntPtr window)
        {
            var rows = new List<(IntPtr, String, String, IntPtr)>();
            var stack = new Stack<(IntPtr Element, Int32 Depth, IntPtr List)>();
            var visited = 0;
            stack.Push((window, 0, IntPtr.Zero));

            while (stack.Count > 0 && visited++ < MaxNodes)
            {
                var (element, depth, list) = stack.Pop();
                var role = tree.Text(element, "AXRole");

                if (role == "AXOutline")
                {
                    list = element;
                }
                else if (role == "AXRow")
                {
                    var title = tree.Text(element, "AXTitle");

                    if (title != null && ChatRowTitle.IsMatch(title))
                    {
                        rows.Add((element, title, String.Join(" ", tree.Texts(element, "AXDOMClassList")), list));
                    }
                }

                // Rows nest: the chats sit inside a "Chats" section row, so keep going into rows too.
                if (depth < MaxDepth)
                {
                    foreach (var child in tree.Children(element, "AXChildren"))
                    {
                        stack.Push((child, depth + 1, list));
                    }
                }
            }

            return rows;
        }

        private static String FindChatId(Tree tree, IntPtr row)
        {
            var stack = new Stack<(IntPtr Element, Int32 Depth)>();
            stack.Push((row, 0));

            while (stack.Count > 0)
            {
                var (element, depth) = stack.Pop();
                var domId = tree.Text(element, "AXDOMIdentifier");

                if (domId != null && domId.StartsWith(RowIdPrefix, StringComparison.Ordinal))
                {
                    var chatId = domId.Substring(RowIdPrefix.Length);
                    return ValidChatId.IsMatch(chatId) ? chatId : null;
                }

                if (depth < 6)
                {
                    foreach (var child in tree.Children(element, "AXChildren"))
                    {
                        stack.Push((child, depth + 1));
                    }
                }
            }

            return null;
        }

        // One read of an app's accessibility tree. Everything it copies is released together when it's disposed,
        // so elements stay valid for the whole walk.
        private sealed class Tree : IDisposable
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
                AXUIElementSetAttributeValue(element, this.Name(attribute), value ? CFBoolean("kCFBooleanTrue") : CFBoolean("kCFBooleanFalse"));

            public String Text(IntPtr element, String attribute) =>
                this.Copy(element, attribute) is IntPtr value && CFGetTypeID(value) == CFStringGetTypeID() ? ReadString(value) : null;

            public IEnumerable<String> Texts(IntPtr element, String attribute) =>
                this.Array(element, attribute).Where(v => CFGetTypeID(v) == CFStringGetTypeID()).Select(ReadString);

            public IEnumerable<IntPtr> Children(IntPtr element, String attribute) => this.Array(element, attribute);

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

            private static IntPtr CFBoolean(String name) =>
                Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), name));
        }

        private const String ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const String CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

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
        private static extern UInt64 CFGetTypeID(IntPtr reference);

        [DllImport(CoreFoundation)]
        private static extern UInt64 CFStringGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern UInt64 CFArrayGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr reference);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFRetain(IntPtr reference);
    }
}
