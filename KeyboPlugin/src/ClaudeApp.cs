namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Text.RegularExpressions;
    using System.Threading;

    // Everything that acts on the Claude app window, and which app is in front.

    public static class ClaudeApp
    {
        public const String BundleId = "com.anthropic.claudefordesktop";
        private const Int32 ActivationDelayMilliseconds = 150;

        private static readonly Regex ValidBridgeId = new Regex("^session_[A-Za-z0-9]+$");
        private static readonly Regex ValidLocalId = new Regex("^local_[A-Za-z0-9-]{1,64}$");

        // lsappinfo info prints `"Name" ASN:…` on its first line, then `bundleID="…"` and `pid = 123`.
        private static readonly Regex FrontName = new Regex("^\"(.*)\" ASN:", RegexOptions.Multiline);
        private static readonly Regex FrontBundleId = new Regex("bundleID=\"([^\"]+)\"");
        private static readonly Regex FrontPid = new Regex(@"\bpid = (\d+)");

        // The link that opens a session, or null. Bridge IDs exist only while Remote Control is on, so the local
        // link is the fallback.
        private static String SessionLink(String bridgeId, String localId) =>
            bridgeId != null && ValidBridgeId.IsMatch(bridgeId) ? $"claude://claude.ai/code/{bridgeId}"
            : localId != null && ValidLocalId.IsMatch(localId) ? $"claude://code/continue?session={localId}&source=desktop_action"
            : null;

        public static void OpenSession(String bridgeId, String localId)
        {
            var link = SessionLink(bridgeId, localId);

            if (link == null)
            {
                PluginLog.Warning($"No usable ID to open the session: {localId}");
                return;
            }

            try
            {
                Shell.Run("/usr/bin/open", link);
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Could not open the session");
            }
        }

        // Keys mean something else in other apps, so nothing is sent unless Claude is in front.
        public static Boolean BringToFront()
        {
            if (!IsFront(BundleId))
            {
                Shell.Run("/usr/bin/open", "-b", BundleId);
                Thread.Sleep(ActivationDelayMilliseconds);
            }

            return IsFront(BundleId);
        }

        public static Boolean IsFront(String bundleId) => Front()?.BundleId == bundleId;

        // The app in front: its bundle ID, process and name, as lsappinfo prints them. Null if lsappinfo can't say.
        public static FrontApp Front()
        {
            var front = Shell.Run("/usr/bin/lsappinfo", "front").Output.Trim();

            if (front.Length == 0)
            {
                return null;
            }

            var info = Shell.Run("/usr/bin/lsappinfo", "info", "-only", "bundleid", "-only", "pid", "-only", "name", front).Output;
            var bundleId = FrontBundleId.Match(info);
            var pid = FrontPid.Match(info);
            var name = FrontName.Match(info);
            return new FrontApp(bundleId.Success ? bundleId.Groups[1].Value : null, pid.Success ? Int32.Parse(pid.Groups[1].Value) : 0, name.Success ? name.Groups[1].Value : null);
        }

        public record FrontApp(String BundleId, Int32 Pid, String Name);
    }
}
