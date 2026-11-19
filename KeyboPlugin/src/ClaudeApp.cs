namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Text.RegularExpressions;
    using System.Threading;

    // Everything that acts on the Claude app window.

    public static class ClaudeApp
    {
        public const String BundleId = "com.anthropic.claudefordesktop";
        private const Int32 ActivationDelayMilliseconds = 150;

        private static readonly Regex ValidBridgeId = new Regex("^session_[A-Za-z0-9]+$");
        private static readonly Regex ValidLocalId = new Regex("^local_[A-Za-z0-9-]{1,64}$");

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

        public static Boolean IsFront(String bundleId)
        {
            var front = Shell.Run("/usr/bin/lsappinfo", "front").Output.Trim();
            return Shell.Run("/usr/bin/lsappinfo", "info", "-only", "bundleid", front).Output.Contains($"\"{bundleId}\"");
        }
    }
}
