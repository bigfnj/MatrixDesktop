using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MatrixDesktop.Shared;

// 2026-10-23: UC3D media is an explicit local input, separate from bundled web
// assets. Only catalogued files can be served to the renderer.
internal sealed record MediaEntry(string Id, string Path, string ContentType, string Caption, string Group, string[]? Groups = null);

internal static class MediaCatalog
{
    internal const long MaxFileBytes = 32 * 1024 * 1024;
    internal const int MaxImages = 256;
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".bmp"] = "image/bmp",
    };
    private static readonly Dictionary<string, string> AudioTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg", [".m4a"] = "audio/mp4",
    };

    internal static IReadOnlyList<MediaEntry> Images(string? personal, string? donor, ICollection<string> warnings)
    {
        var entries = new List<MediaEntry>();
        AddFolder(personal, "custom", entries, warnings);
        if (ReadDonorManifest(donor, entries, warnings)) return entries;
        AddFolder(donor, "matrix", entries, warnings);
        if (!string.IsNullOrWhiteSpace(donor))
        {
            foreach (var group in new[] { "matrix", "reloaded", "revolutions" })
                AddFolder(System.IO.Path.Combine(donor, group), group, entries, warnings, optional: true);
        }
        return entries;
    }

    private static bool ReadDonorManifest(string? directory, List<MediaEntry> entries, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var root = new DirectoryInfo(System.IO.Path.GetFullPath(directory));
            if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var file = new FileInfo(System.IO.Path.Combine(root.FullName, "uc3d-media.json"));
            if (!file.Exists) return false;
            if (!Readable(file) || file.Length > 1024 * 1024) throw new InvalidDataException("Donor manifest is too large or unavailable.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            var schema = document.RootElement.GetProperty("schema");
            if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
                throw new InvalidDataException("Unsupported donor manifest version.");
            var imported = new List<MediaEntry>();
            foreach (var image in document.RootElement.GetProperty("images").EnumerateArray())
            {
                if (entries.Count + imported.Count >= MaxImages) { warnings.Add($"Picture library is limited to {MaxImages} images."); break; }
                var name = image.GetProperty("file").GetString() ?? "";
                // Manifests grant direct children only, never arbitrary paths or subtrees.
                if (name.Length == 0 || name != System.IO.Path.GetFileName(name) || name.Contains('/') || name.Contains('\\'))
                    throw new InvalidDataException("Donor manifest contains a non-local filename.");
                var local = new FileInfo(System.IO.Path.Combine(root.FullName, name));
                if (!ImageTypes.TryGetValue(local.Extension, out var type) || !Readable(local)) continue;
                var caption = image.TryGetProperty("caption", out var value) ? value.GetString() ?? "" : System.IO.Path.GetFileNameWithoutExtension(name);
                var groups = image.TryGetProperty("groups", out var groupList) ? groupList.EnumerateArray()
                    .Select(g => g.GetString() ?? "").Where(g => g is "matrix" or "reloaded" or "revolutions").Distinct().ToArray() : [];
                if (groups.Length == 0) groups = ["matrix"];
                imported.Add(new($"image-{entries.Count + imported.Count}", local.FullName, type, caption[..Math.Min(caption.Length, 200)], groups[0], groups));
            }
            entries.AddRange(imported);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            warnings.Add($"Donor manifest could not be loaded: {ex.Message}");
            return false;
        }
    }

    internal static MediaEntry? Glyphs(string? directory, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        foreach (var name in new[] { "glyphs.png", "glyphs.bmp" })
        {
            try
            {
                var file = new FileInfo(System.IO.Path.Combine(directory, name));
                if (Readable(file) && file.Directory is { } parent && (parent.Attributes & FileAttributes.ReparsePoint) == 0)
                    return new("glyphs", file.FullName, ImageTypes[file.Extension], "", "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        warnings.Add("Donor glyph atlas is unavailable. Import a local donor media pack or use the normal font.");
        return null;
    }

    private static void AddFolder(string? directory, string group, List<MediaEntry> entries, ICollection<string> warnings, bool optional = false)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            var root = new DirectoryInfo(System.IO.Path.GetFullPath(directory));
            if (!root.Exists)
            {
                if (!optional) warnings.Add($"Picture folder is unavailable: {directory}");
                return;
            }
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add("Picture folder links are unsupported; select the original folder.");
                return;
            }
            // No recursive walk. Donor film groups are enumerated explicitly above.
            foreach (var file in root.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entries.Count >= MaxImages)
                {
                    warnings.Add($"Picture library is limited to {MaxImages} images.");
                    break;
                }
                if (!ImageTypes.TryGetValue(file.Extension, out var type) || !Readable(file)) continue;
                entries.Add(new($"image-{entries.Count}", file.FullName, type, System.IO.Path.GetFileNameWithoutExtension(file.Name), group));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warnings.Add($"Picture folder could not be read: {ex.Message}");
        }
    }

    internal static MediaEntry? Audio(string? path, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var file = new FileInfo(System.IO.Path.GetFullPath(path));
            if (AudioTypes.TryGetValue(file.Extension, out var type) && Readable(file))
                return new("music", file.FullName, type, "", "");
            warnings.Add("Music must be an available MP3, WAV, OGG or M4A file of at most 32 MiB.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warnings.Add($"Music could not be opened: {ex.Message}");
        }
        return null;
    }

    internal static bool Readable(FileInfo file) => file.Exists && file.Length > 0 && file.Length <= MaxFileBytes
        && (file.Attributes & FileAttributes.ReparsePoint) == 0;

    internal static bool StillAllowed(MediaEntry entry)
    {
        try
        {
            var file = new FileInfo(entry.Path);
            // Recheck the directory as well: a folder replaced by a link after enumeration
            // must not redirect a previously granted resource to another directory.
            return Readable(file) && file.Directory is { Exists: true } parent
                && (parent.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
