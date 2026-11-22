namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Concurrent;

    // The agent keys' half of KeyArt: session keys, Usage, the shortcut keys and hold choices.

    public static partial class KeyArt
    {
        public const String MicIcon = "<rect x='8.5' y='2' width='7' height='13' rx='3.5'/><path d='M4.5 11.5a7.5 7.5 0 0 0 15 0M12 19v3M8 22h8'/>";
        public const String ReturnIcon = "<path d='M20 4v7a4 4 0 0 1-4 4H4'/><path d='M9 10l-5 5 5 5'/>";
        public const String PinIcon = "<path d='M12 16v6M8 2.5h8l-1 7.5 4 5H5l4-5z'/>";
        public const String PinHereIcon = PinIcon + "<path d='M20.5 2v5M18 4.5h5'/>";
        public const String UnpinIcon = PinIcon + "<path d='M3 3l18 18'/>";

        // A lidded box with an arrow going in: put away, not gone.
        public const String ArchiveIcon = "<rect x='2.5' y='3.5' width='19' height='5' rx='1.2'/><path d='M4.5 8.5v10a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2v-10'/><path d='M12 11v6M9 14.5l3 3 3-3'/>";

        public const String CancelIcon = "<path d='M6 6l12 12M18 6L6 18'/>";
        private const String QuestionMark = "<path d='M9.6 8.2a2.5 2.5 0 1 1 3.6 2.3c-.8.4-1.2 1-1.2 1.7'/><path d='M12 15v.01'/>";
        private const String CheckMark = "<path d='M7.5 11l3 3 6-6'/>";
        private static readonly String NewMessageDot = $"<circle cx='12' cy='11' r='3.2' fill='{Hex(Green)}' stroke='none'/>";

        private const UInt32 ChoiceSurface = 0x1B2330;
        private const UInt32 Off = 0x0C1016;
        private const UInt32 DimBlue = 0x2B5E8E;
        private const UInt32 UnlitDot = 0x24405C;
        private const UInt32 DimText = 0x66727F;
        private const UInt32 Ink = 0x14100A;

        // The Dictate key's sound arcs: a soft red, faded into the key rather than drawn see-through.
        public const Int32 ListeningFrames = 8;
        private const UInt32 ListeningRed = 0xFF7A7A;
        private static readonly Double[] ListeningWave = { 0, 0.55, 0.85, 0.7, 0.45, 0.25, 0.1, 0 };

        // Each app's own icon, as macOS shows it, centred on the bubble's top-right corner like a badge. ChatGPT's stands
        // for Codex. Found by bundle ID and read from the installed app once, so no logos live in the repo; a missing app
        // just means no icon. A pinned app that isn't running shows its icon grey.
        private const Int32 AppIconSize = 28;
        private const Int32 AppIconLeft = 69;
        private const Int32 AppIconTop = 1;
        private const Int32 AppIconPixels = 64;
        private static readonly ConcurrentDictionary<(String App, Boolean Faded), Byte[]> AppIcons = new ConcurrentDictionary<(String, Boolean), Byte[]>();

        // On the Usage key the icon tucks into the key's top-right corner, a little smaller so it clears the ring and the tick.
        private const Int32 UsageIconSize = 26;
        private const Int32 UsageIconLeft = 88;
        private const Int32 UsageIconTop = 2;

        // A pinned app's Dock badge sits in its window, under the toolbar line. DrawText sets digits a little above the middle
        // of their box, so the box starts lower to centre them in the window.
        private const Int32 WindowBadgeTop = 23;
        private const Int32 WindowMiddle = 34;

        private const Int32 LineLength = 9;

        // Session keys keep the bubble and name area in the same place, whatever the name's length.
        // The bubble is nearly square, sized for a single-digit count like 3/5. Its tail ends just above the name.
        private const Int32 BubbleWidth = 54;
        private const Int32 BubbleTop = 11;
        private const Int32 BubbleBottom = 48;
        private const Int32 BubbleAreaHeight = 56;
        private const Single BubbleStroke = 38 / 12f;
        private const Single BubbleCorner = 38 / 8f;
        private const Single InsideScale = 38 / 24f * 1.25f;
        private const Int32 CountFontSize = 21;
        private const Int32 LongCountFontSize = 15;
        private const Int32 NameTop = 50;
        private const Int32 NameHeight = 60;
        private const Int32 NameFontSize = 20;
        private const Int32 NameLineHeight = 23;

        public static BitmapImage Empty(PluginImageSize imageSize)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                builder.Clear(BitmapColor.FromRgb(Off));
                return builder.ToImage();
            }
        }

        public static BitmapImage Control(PluginImageSize imageSize, String icon, String label) =>
            DrawControl(imageSize, icon, label, Text, Surface);

        // Dictate while an app is recording: the usual key, with faint red sound arcs either side of the mic that ripple
        // outwards, inner arcs first. Each frame sets how bright each pair is, from 0 (not drawn) to 1.
        public static BitmapImage Listening(PluginImageSize imageSize, String label, Int32 frame)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var scale = (Single)builder.Width / DesignSize;
                var pixels = builder.Width;
                var arcs = Arc(ListeningWave[frame % ListeningFrames], "M37 22Q30 32 37 42M79 22Q86 32 79 42")
                    + Arc(ListeningWave[(frame + ListeningFrames - 2) % ListeningFrames], "M29 16Q19 32 29 48M87 16Q97 32 87 48");

                builder.Clear(BitmapColor.FromRgb(Surface));
                builder.DrawImage(BitmapImage.FromSvg($"<svg xmlns='http://www.w3.org/2000/svg' width='{pixels}' height='{pixels}' viewBox='0 0 {DesignSize} {DesignSize}'>{arcs}{KeepColor}</svg>"), 0, 0, pixels, pixels);
                DrawIcon(builder, MicIcon, Text, 33, 12, 50, scale);
                builder.DrawText(label, 0, Scaled(74, scale), builder.Width, Scaled(32, scale), BitmapColor.FromRgb(Text), Scaled(19, scale));
                return builder.ToImage();
            }
        }

        // Return while an app is recording: the usual key with a small soft red dot in the top corner, pulsing in step
        // with Dictate's inner arcs. It dims but never quite goes out.
        public static BitmapImage ControlWithDot(PluginImageSize imageSize, String icon, String label, Int32 frame)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var scale = (Single)builder.Width / DesignSize;
                var pixels = builder.Width;
                var dot = Fade(ListeningRed, 0.3 + (0.7 * ListeningWave[frame % ListeningFrames]));

                builder.Clear(BitmapColor.FromRgb(Surface));
                builder.DrawImage(BitmapImage.FromSvg($"<svg xmlns='http://www.w3.org/2000/svg' width='{pixels}' height='{pixels}' viewBox='0 0 {DesignSize} {DesignSize}'><circle cx='100' cy='16' r='5' fill='{Hex(dot)}' stroke='none'/>{KeepColor}</svg>"), 0, 0, pixels, pixels);
                DrawIcon(builder, icon, Text, 33, 12, 50, scale);
                builder.DrawText(label, 0, Scaled(74, scale), builder.Width, Scaled(32, scale), BitmapColor.FromRgb(Text), Scaled(19, scale));
                return builder.ToImage();
            }
        }

        public static BitmapImage EmptySessionKey(PluginImageSize imageSize) =>
            DrawControl(imageSize, PinHereIcon, "Pin here", Dim, Off);

        // A hold choice uses the same layout as the main keys, on a slightly lifted background. Never mind is muted.
        public static BitmapImage HoldChoice(PluginImageSize imageSize, HoldMenu.Choice choice) =>
            DrawControl(imageSize, choice.Icon, choice.Label, choice.Run == null ? Muted : Text, ChoiceSurface);

        private static BitmapImage DrawControl(PluginImageSize imageSize, String icon, String label, UInt32 color, UInt32 background)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var scale = (Single)builder.Width / DesignSize;

                builder.Clear(BitmapColor.FromRgb(background));
                DrawIcon(builder, icon, color, 33, 12, 50, scale);
                builder.DrawText(label, 0, Scaled(74, scale), builder.Width, Scaled(32, scale), BitmapColor.FromRgb(color), Scaled(19, scale));
                return builder.ToImage();
            }
        }

        // The usage ring: the arc is how much is left, and the white tick is where an even pace would leave you.
        // The arc takes the forecast's colour, and so does the footer when it's a warning. The amount sits in the middle
        // of the ring, a size smaller when it's four characters or more (100%, $150) so it clears the ring. DrawText sets
        // digits above the middle of their box, so the box starts lower: measured on rendered keys, that puts the digits
        // on the ring's centre to within half a pixel. The ring is drawn at key size. The app's icon (a bundle ID) says
        // whose limit it is.
        public static BitmapImage Usage(PluginImageSize imageSize, Double left, Double pace, UsageTone tone, String amount, String footer, Boolean footerWarns, String app)
        {
            const Single cx = 58, cy = 50, r = 37, stroke = 8;
            var circumference = 2 * MathF.PI * r;
            var color = tone switch { UsageTone.Good => Green, UsageTone.Warn => Amber, UsageTone.Bad => Red, _ => Muted };
            var arc = (Single)Math.Clamp(left, 0, 1) * circumference;
            var angle = (Single)Math.Clamp(pace, 0, 1) * 2 * MathF.PI;
            var (sin, cos) = MathF.SinCos(angle);
            var (amountSize, amountTop) = amount.Length > 3 ? (22, 36) : (26, 37);

            using (var builder = new BitmapBuilder(imageSize))
            {
                var scale = (Single)builder.Width / DesignSize;
                var pixels = Scaled(DesignSize, scale);
                var svg = FormattableString.Invariant(
                    $"<svg xmlns='http://www.w3.org/2000/svg' width='{pixels}' height='{pixels}' viewBox='0 0 {DesignSize} {DesignSize}' fill='none' stroke-width='{stroke}'>") +
                    FormattableString.Invariant($"<circle cx='{cx}' cy='{cy}' r='{r}' stroke='{Hex(Dim)}'/>") +
                    (arc > 0 ? FormattableString.Invariant($"<circle cx='{cx}' cy='{cy}' r='{r}' stroke='{Hex(color)}' stroke-linecap='round' stroke-dasharray='{arc} {circumference}' transform='rotate(-90 {cx} {cy})'/>") : String.Empty) +
                    (tone == UsageTone.Stale ? String.Empty : FormattableString.Invariant($"<line x1='{cx + ((r - 7) * sin)}' y1='{cy - ((r - 7) * cos)}' x2='{cx + ((r + 7) * sin)}' y2='{cy - ((r + 7) * cos)}' stroke='{Hex(Text)}' stroke-width='3' stroke-linecap='round'/>")) +
                    $"{KeepColor}</svg>";

                builder.Clear(BitmapColor.FromRgb(Surface));
                builder.DrawImage(BitmapImage.FromSvg(svg), 0, 0, pixels, pixels);
                builder.DrawText(amount, 0, Scaled(amountTop, scale), builder.Width, Scaled(32, scale), BitmapColor.FromRgb(tone == UsageTone.Stale ? Muted : Text), Scaled(amountSize, scale));
                builder.DrawText(footer, 0, Scaled(94, scale), builder.Width, Scaled(18, scale), BitmapColor.FromRgb(footerWarns ? color : Muted), Scaled(14, scale));

                if (AppIcon(app) is Byte[] icon)
                {
                    builder.DrawImage(icon, Scaled(UsageIconLeft, scale), Scaled(UsageIconTop, scale), Scaled(UsageIconSize, scale), Scaled(UsageIconSize, scale));
                }

                return builder.ToImage();
            }
        }

        // When there's no reading yet, or it can't be read: an empty ring and a short reason.
        public static BitmapImage UsageMessage(PluginImageSize imageSize, String message)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var scale = (Single)builder.Width / DesignSize;
                builder.Clear(BitmapColor.FromRgb(Surface));
                builder.DrawText("Usage", 0, Scaled(28, scale), builder.Width, Scaled(24, scale), BitmapColor.FromRgb(Muted), Scaled(17, scale));
                builder.DrawText(message, 0, Scaled(58, scale), builder.Width, Scaled(36, scale), BitmapColor.FromRgb(Text), Scaled(14, scale));
                return builder.ToImage();
            }
        }

        // dotFrame picks which dot is lit while work is going on. A grey bubble with dots means the session is
        // ready for you, but a command it started is still running.
        public static BitmapImage Session(PluginImageSize imageSize, SessionInfo session, Int32 dotFrame)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                var scale = (Single)builder.Width / DesignSize;

                builder.Clear(BitmapColor.FromRgb(GetBackground(session.Status)));
                DrawBubble(builder, session, dotFrame, scale);
                builder.DrawText(FitName(session.Name), 0, Scaled(NameTop, scale), builder.Width, Scaled(NameHeight, scale), BitmapColor.FromRgb(GetTextColor(session.Status)), Scaled(NameFontSize, scale), Scaled(NameLineHeight, scale));

                if (AppIconFor(session) is Byte[] icon)
                {
                    builder.DrawImage(icon, Scaled(AppIconLeft, scale), Scaled(AppIconTop, scale), Scaled(AppIconSize, scale), Scaled(AppIconSize, scale));
                }
                return builder.ToImage();
            }
        }

        // The bubble is wide on every key, so it sits the same on all of them whatever is inside.
        // A Teams chat has the same bubble as a session: empty and grey when read; on green (a new message), dark with a
        // green dot, since a message isn't "done". A Safari tab or any app's window is a small window instead.
        private static void DrawBubble(BitmapBuilder builder, SessionInfo session, Int32 dotFrame, Single scale)
        {
            if (session.IsApp)
            {
                DrawAppWindow(builder, session, scale);
                return;
            }

            if (session.IsSafari)
            {
                DrawWindow(builder, Muted, null, Muted, scale);
                return;
            }

            if (session.IsTeams)
            {
                if (session.Status == SessionStatus.Done)
                {
                    DrawWideBubble(builder, NewMessageDot, Ink, Ink, scale);
                }
                else
                {
                    DrawWideBubble(builder, String.Empty, Muted, null, scale);
                }

                return;
            }

            switch (session.Status)
            {
                case SessionStatus.Busy when session.Progress != null:
                    DrawWideBubble(builder, String.Empty, Blue, null, scale);
                    DrawCount(builder, $"{session.Progress.Step}/{session.Progress.Steps}", dotFrame == 2 ? DimBlue : Blue, scale);
                    break;
                case SessionStatus.Busy:
                    DrawWideBubble(builder, Dots(dotFrame, Blue, UnlitDot), Blue, null, scale);
                    break;
                case SessionStatus.Background:
                    DrawWideBubble(builder, Dots(dotFrame, Blue, UnlitDot), Muted, null, scale);
                    break;
                case SessionStatus.Waiting:
                    DrawWideBubble(builder, Stroked(QuestionMark, Amber), Ink, Ink, scale);
                    break;
                case SessionStatus.Done:
                    DrawWideBubble(builder, Stroked(CheckMark, Green), Ink, Ink, scale);
                    break;
                case SessionStatus.Idle:
                    DrawWideBubble(builder, String.Empty, Muted, null, scale);
                    break;
                default:
                    DrawWideBubble(builder, String.Empty, Dim, null, scale);
                    break;
            }
        }

        // The outline is drawn at key size. What goes inside uses the 24-unit icon grid, a little larger than
        // the icons, with its centre on the bubble's centre.
        // A window the size of the speech bubble, with a toolbar line and three dots: a Safari tab, or a pinned app's window.
        private static void DrawWindow(BitmapBuilder builder, UInt32 color, UInt32? fill, UInt32 dotColor, Single scale)
        {
            const Single left = ((DesignSize - BubbleWidth) / 2f) + (BubbleStroke / 2);
            const Single right = ((DesignSize + BubbleWidth) / 2f) - (BubbleStroke / 2);
            const Single top = BubbleTop;
            const Single bottom = BubbleBottom;
            const Single toolbar = top + 9;
            var width = Scaled(DesignSize, scale);
            var height = Scaled(BubbleAreaHeight, scale);
            var dots = String.Empty;

            for (var dot = 0; dot < 3; dot++)
            {
                dots += FormattableString.Invariant($"<circle cx='{left + 5.5f + (dot * 4.5f)}' cy='{top + 4.7f}' r='1.5' fill='{Hex(dotColor)}' stroke='none'/>");
            }

            var svg = FormattableString.Invariant(
                $"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}' viewBox='0 0 {DesignSize} {BubbleAreaHeight}' fill='none' stroke='{Hex(color)}' stroke-width='{BubbleStroke}' stroke-linecap='round' stroke-linejoin='round'>") +
                FormattableString.Invariant($"<rect x='{left}' y='{top}' width='{right - left}' height='{bottom - top}' rx='{BubbleCorner}' fill='{(fill.HasValue ? Hex(fill.Value) : "none")}'/><path d='M{left} {toolbar}H{right}'/>") +
                $"{dots}{KeepColor}</svg>";

            builder.DrawImage(BitmapImage.FromSvg(svg), 0, 0, width, height);
        }

        // Any app's window, the shape Safari tabs use. Its app's Dock badge turns the key green, with the badge in the
        // window (a dot when it's more than a short count). Dark while the app isn't running.
        private static void DrawAppWindow(BitmapBuilder builder, SessionInfo session, Single scale)
        {
            switch (session.Status)
            {
                case SessionStatus.Done:
                    DrawWindow(builder, Ink, Ink, Green, scale);
                    DrawWindowBadge(builder, session.Badge, scale);
                    break;
                case SessionStatus.NotRunning:
                    DrawWindow(builder, Dim, null, Dim, scale);
                    break;
                default:
                    DrawWindow(builder, Muted, null, Muted, scale);
                    break;
            }
        }

        private static void DrawWindowBadge(BitmapBuilder builder, String badge, Single scale)
        {
            if (badge != null && badge.Length <= 3)
            {
                builder.DrawText(badge, 0, Scaled(WindowBadgeTop, scale), builder.Width, Scaled(BubbleBottom - WindowBadgeTop, scale), BitmapColor.FromRgb(Green), Scaled(CountFontSize, scale));
                return;
            }

            var pixels = Scaled(DesignSize, scale);
            var dot = FormattableString.Invariant(
                $"<svg xmlns='http://www.w3.org/2000/svg' width='{pixels}' height='{pixels}' viewBox='0 0 {DesignSize} {DesignSize}'><circle cx='{DesignSize / 2f}' cy='{WindowMiddle}' r='4' fill='{Hex(Green)}'/>{KeepColor}</svg>");
            builder.DrawImage(BitmapImage.FromSvg(dot), 0, 0, pixels, pixels);
        }

        private static void DrawWideBubble(BitmapBuilder builder, String inside, UInt32 color, UInt32? fill, Single scale)
        {
            const Single left = ((DesignSize - BubbleWidth) / 2f) + (BubbleStroke / 2);
            const Single right = ((DesignSize + BubbleWidth) / 2f) - (BubbleStroke / 2);
            const Single top = BubbleTop;
            const Single bottom = BubbleBottom;
            const Single r = BubbleCorner;
            const Single middle = (top + bottom) / 2;

            var outline = FormattableString.Invariant(
                $"M{left + r},{top} H{right - r} A{r},{r} 0 0 1 {right},{top + r} V{bottom - r} A{r},{r} 0 0 1 {right - r},{bottom} ") +
                FormattableString.Invariant($"H{left + 14} L{left + 6},{bottom + 5} V{bottom} H{left + r} A{r},{r} 0 0 1 {left},{bottom - r} V{top + r} A{r},{r} 0 0 1 {left + r},{top} Z");
            var width = Scaled(DesignSize, scale);
            var height = Scaled(BubbleAreaHeight, scale);
            var svg = FormattableString.Invariant(
                $"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}' viewBox='0 0 {DesignSize} {BubbleAreaHeight}' fill='none' stroke='{Hex(color)}' stroke-linecap='round' stroke-linejoin='round'>") +
                FormattableString.Invariant($"<path d='{outline}' fill='{(fill.HasValue ? Hex(fill.Value) : "none")}' stroke-width='{BubbleStroke}'/>") +
                FormattableString.Invariant($"<g transform='translate({(DesignSize / 2f) - (12 * InsideScale)} {middle - (11 * InsideScale)}) scale({InsideScale})' stroke-width='{BubbleStroke / InsideScale}'>{inside}</g>{KeepColor}</svg>");

            builder.DrawImage(BitmapImage.FromSvg(svg), 0, 0, width, height);
        }

        // Plans are nearly always single digit. A longer one like 10/12 gets a smaller size to fit.
        private static void DrawCount(BitmapBuilder builder, String count, UInt32 color, Single scale)
        {
            var size = count.Length > 3 ? LongCountFontSize : CountFontSize;
            builder.DrawText(count, 0, Scaled(BubbleTop, scale), builder.Width, Scaled(BubbleBottom - BubbleTop, scale), BitmapColor.FromRgb(color), Scaled(size, scale));
        }

        private static void DrawIcon(BitmapBuilder builder, String body, UInt32 color, Int32 x, Int32 y, Int32 size, Single scale)
        {
            var pixels = Scaled(size, scale);
            var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='{pixels}' height='{pixels}' viewBox='0 0 24 24' fill='none' stroke='{Hex(color)}' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>{body}{KeepColor}</svg>";

            builder.DrawImage(BitmapImage.FromSvg(svg), Scaled(x, scale), Scaled(y, scale), pixels, pixels);
        }

        private static Byte[] AppIconFor(SessionInfo session)
        {
            var app = session.App ?? (session.IsSafari ? SafariTabs.BundleId : session.IsTeams ? TeamsChats.BundleId : session.IsCodex ? CodexThreads.BundleId : ClaudeApp.BundleId);
            return AppIcon(app, faded: session.IsApp && session.Status == SessionStatus.NotRunning);
        }

        // An app's icon by bundle ID, read once. Null if the app isn't installed.
        private static Byte[] AppIcon(String app, Boolean faded = false) =>
            AppIcons.GetOrAdd((app, faded), key => Mac.AppPath(key.App) is String path ? Mac.AppIcon(path, AppIconPixels, key.Faded) : null);

        private static String Stroked(String body, UInt32 color) => $"<g stroke='{Hex(color)}'>{body}</g>";

        private static String Arc(Double brightness, String path) => brightness <= 0
            ? String.Empty
            : $"<path d='{path}' fill='none' stroke='{Hex(Fade(ListeningRed, brightness))}' stroke-width='3' stroke-linecap='round'/>";

        // A colour part of the way from the key's background to this one.
        private static UInt32 Fade(UInt32 color, Double amount)
        {
            UInt32 Channel(Int32 shift) => (UInt32)Math.Round((((Surface >> shift) & 0xFF) * (1 - amount)) + (((color >> shift) & 0xFF) * amount)) << shift;
            return Channel(16) | Channel(8) | Channel(0);
        }

        private static String Dots(Int32 litDot, UInt32 lit, UInt32 unlit, Single centre = 12, Single gap = 5, Single radius = 1.6f)
        {
            var dots = String.Empty;

            for (var dot = 0; dot < 3; dot++)
            {
                var color = dot == litDot ? lit : unlit;
                dots += FormattableString.Invariant($"<circle cx='{centre + ((dot - 1) * gap)}' cy='11' r='{radius}' fill='{Hex(color)}' stroke='none'/>");
            }

            return dots;
        }

        private static UInt32 GetBackground(SessionStatus status) => status switch
        {
            SessionStatus.Waiting => Amber,
            SessionStatus.Done => Green,
            SessionStatus.NotRunning => Off,
            _ => Surface,
        };

        private static UInt32 GetTextColor(SessionStatus status) => status switch
        {
            SessionStatus.Waiting => Ink,
            SessionStatus.Done => Ink,
            SessionStatus.NotRunning => DimText,
            _ => Text,
        };

        // Fits the name on two short lines, breaking at a space when one is available.
        private static String FitName(String name)
        {
            if (name.Length <= LineLength)
            {
                return name;
            }

            var split = name.LastIndexOf(' ', LineLength);
            var first = split > 0 ? name.Substring(0, split) : name.Substring(0, LineLength);
            var second = name.Substring(first.Length).Trim();

            if (second.Length > LineLength)
            {
                second = second.Substring(0, LineLength - 1) + "…";
            }

            return first + Environment.NewLine + second;
        }
    }
}
