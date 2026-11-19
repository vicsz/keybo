namespace Loupedeck.KeyboPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json;

    // The Pet key's frames: 116 x 116 PNGs in Pet/<state>/, a few badge PNGs, and Pet/animation-timing.json with each
    // frame's duration, its badge, and where the sign text goes. Pet/gags.json lists one-off gags (Pet/gags/<name>/),
    // which play once and are named "gag:<name>" here. All embedded in the plugin; loaded once.

    public static class PetArt
    {
        public static IReadOnlyDictionary<String, Animation> States { get; } = Load();

        public static IReadOnlyList<String> Gags { get; } = States.Keys.Where(k => k.StartsWith("gag:", StringComparison.Ordinal)).ToArray();

        private static Dictionary<String, Animation> Load()
        {
            var assembly = typeof(PetArt).Assembly;
            var root = JsonDocument.Parse(Read(assembly, "Pet/animation-timing.json")).RootElement;
            var badges = root.GetProperty("badges").EnumerateObject().ToDictionary(b => b.Name, b => Read(assembly, "Pet/" + b.Value.GetString()));
            var states = new Dictionary<String, Animation>();

            foreach (var state in root.GetProperty("states").EnumerateObject())
            {
                var frames = state.Value.GetProperty("frames").EnumerateArray().Select(f => new Frame(
                    Read(assembly, "Pet/" + f.GetProperty("file").GetString()),
                    f.GetProperty("duration_ms").GetInt32(),
                    f.TryGetProperty("badge", out var badge) ? badges[badge.GetString()] : null,
                    f.TryGetProperty("text_box", out var box)
                        ? (box.GetProperty("x").GetInt32(), box.GetProperty("y").GetInt32(), box.GetProperty("width").GetInt32(), box.GetProperty("height").GetInt32())
                        : null)).ToArray();

                states[state.Name] = new Animation(frames, state.Value.GetProperty("playback").GetString() == "loop");
            }

            foreach (var gag in JsonDocument.Parse(Read(assembly, "Pet/gags.json")).RootElement.GetProperty("gags").EnumerateObject())
            {
                var frames = gag.Value.GetProperty("frames").EnumerateArray().Select(f => new Frame(
                    Read(assembly, "Pet/" + f.GetProperty("file").GetString()), f.GetProperty("duration_ms").GetInt32(), null, null)).ToArray();

                states["gag:" + gag.Name] = new Animation(frames, false);
            }

            return states;
        }

        private static Byte[] Read(Assembly assembly, String name)
        {
            using (var stream = assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException($"Missing embedded {name}"))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        public record Frame(Byte[] Png, Int32 Ms, Byte[] Badge, (Int32 X, Int32 Y, Int32 Width, Int32 Height)? TextBox);

        public record Animation(Frame[] Frames, Boolean Loops);
    }
}
