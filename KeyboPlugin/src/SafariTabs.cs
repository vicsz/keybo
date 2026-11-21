namespace Loupedeck.KeyboPlugin
{
    using System;

    // Safari tabs on session keys: which tab is in front, and switching back to one. It talks to Safari through its
    // AppleScript dictionary (macOS asks once to let Logi Plugin Service control Safari). All Safari knowledge stays here.
    // It only ever reads the front tab's address and title, when you pin it, and never logs either.
    //
    // A pinned tab's key ID is "safari:<address>\n<title>": the title can contain "|" and the address can't contain a
    // line break, so both survive restarts with no file of their own. A pinned tab is never gone: if it was closed,
    // a tap opens its address again.

    public static class SafariTabs
    {
        public const String BundleId = "com.apple.Safari";
        public const String IdPrefix = "safari:";

        private const String FallbackName = "Safari tab";

        // Read only while Safari is in front, so it never starts Safari.
        private const String ReadFrontTab = """
            tell application "Safari"
                if (count of windows) is 0 then return ""
                set t to current tab of front window
                return (URL of t) & linefeed & (name of t)
            end tell
            """;

        // Brings the pinned tab to the front, or opens its address in a new tab if no tab is it. Pages often change their own
        // address (a date or page number in the query), so it looks for, in order: the exact address, the same page (the
        // address up to "?" or "#"), then the same title. argv: address, page, title.
        private const String ShowTab = """
            on run argv
                set target to item 1 of argv
                set page to item 2 of argv
                set wanted to item 3 of argv
                tell application "Safari"
                    repeat with pass from 1 to 3
                        repeat with w in windows
                            repeat with t in tabs of w
                                set u to (URL of t) as text
                                if pass is 1 then
                                    set hit to (u is target)
                                else if pass is 2 then
                                    set hit to (u is page or u starts with (page & "?") or u starts with (page & "#"))
                                else
                                    set hit to (wanted is not "" and ((name of t) as text) is wanted)
                                end if
                                if hit then
                                    set current tab of w to t
                                    set index of w to 1
                                    activate
                                    return "found"
                                end if
                            end repeat
                        end repeat
                    end repeat
                    activate
                    if (count of windows) is 0 then
                        make new document with properties {URL:target}
                    else
                        tell front window to set current tab to (make new tab with properties {URL:target})
                    end if
                    return "opened"
                end tell
            end run
            """;

        public static Boolean IsSafariId(String id) => id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal);

        // The session-key entry for a pinned tab, from its key ID alone.
        public static SessionInfo FromId(String id)
        {
            if (!IsSafariId(id))
            {
                return null;
            }

            var (_, title) = Split(id);
            return new SessionInfo(id, String.IsNullOrWhiteSpace(title) ? FallbackName : title, SessionStatus.Idle, null, IsSafari: true);
        }

        // The tab in front, or null if Safari has no window, the page isn't a web address, or Safari wouldn't answer.
        public static SessionInfo ReadFront()
        {
            var result = Shell.Run("/usr/bin/osascript", "-e", ReadFrontTab);

            if (result.ExitCode != 0)
            {
                PluginLog.Warning("Safari: couldn't read the front tab (is Logi Plugin Service allowed to control Safari?)");
                return null;
            }

            var lines = result.Output.TrimEnd('\n').Split('\n', 2);
            var address = lines[0].Trim();

            if (!IsWebAddress(address))
            {
                return null;
            }

            var title = lines.Length > 1 ? lines[1].Trim() : String.Empty;
            return FromId($"{IdPrefix}{address}\n{title}");
        }

        public static void Open(String id)
        {
            var (address, title) = Split(id);

            if (!IsWebAddress(address))
            {
                PluginLog.Warning("Not a usable Safari tab address");
                return;
            }

            Shell.Start("/usr/bin/osascript", "-e", ShowTab, address, Page(address), title);
        }

        private static (String Address, String Title) Split(String id)
        {
            var rest = id.Substring(IdPrefix.Length);
            var newline = rest.IndexOf('\n');
            return newline >= 0 ? (rest.Substring(0, newline), rest.Substring(newline + 1)) : (rest, String.Empty);
        }

        // The address without its query or fragment: the same page, whatever it has added to its address since.
        private static String Page(String address)
        {
            var cut = address.IndexOfAny(new[] { '?', '#' });
            return cut >= 0 ? address.Substring(0, cut) : address;
        }

        // Only web pages can be pinned and opened, never file:, javascript: or other kinds of address.
        private static Boolean IsWebAddress(String address) =>
            Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
