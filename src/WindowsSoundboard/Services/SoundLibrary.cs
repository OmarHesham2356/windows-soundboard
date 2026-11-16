using WindowsSoundboard.Models;

namespace WindowsSoundboard.Services;

public static class SoundLibrary
{
    private static readonly string[] SupportedExtensions =
    [
        ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff",
    ];

    /// <summary>
    /// Lists playable files directly inside <paramref name="folder"/> (one level,
    /// no recursion) so a big music folder cannot accidentally become the board.
    /// </summary>
    public static List<SoundItem> Scan(string folder)
    {
        var items = new List<SoundItem>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return items;
        }

        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (!SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            items.Add(new SoundItem(RelativeId(folder, path), path, name));
        }

        items.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    public static string RelativeId(string folder, string path)
        => Path.GetRelativePath(folder, path).Replace('\\', '/').ToLowerInvariant();
}

public static class VirtualCable
{
    private static readonly string[] CableMarkers =
    [
        "cable input", "vb-audio", "vb-cable", "voicemeeter", "virtual audio",
    ];

    /// <summary>
    /// True when an endpoint looks like the playback side of a virtual cable.
    /// Windows exposes VB-CABLE as "CABLE Input (VB-Audio Virtual Cable)".
    /// </summary>
    public static bool LooksLikeCable(string friendlyName)
        => CableMarkers.Any(marker => friendlyName.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
