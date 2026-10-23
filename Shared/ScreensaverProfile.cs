using System;
using System.IO;
using System.Text.Json;

namespace Shared;

// Store argv directly so Windows quoting never changes a saved renderer profile.
internal static class ScreensaverProfile
{
    internal static string ProfilePath => Environment.GetEnvironmentVariable("MATRIXDESKTOP_SCREENSAVER_PROFILE") is string path
        && !string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(path) : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MatrixDesktop", "screensaver-profile.json");

    public static string[] Load() => LoadFrom(ProfilePath);

    internal static string[] LoadFrom(string path)
    {
        if (!File.Exists(path)) return ["--scene", "matrix"];
        if (new FileInfo(path).Length > 1_048_576)
            throw new InvalidDataException("The screensaver profile exceeds 1 MB.");
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
        if (arguments is null || Array.Exists(arguments, argument => argument is null))
            throw new InvalidDataException("The screensaver profile must contain an array of arguments.");
        return arguments;
    }

    public static void Save(string[] arguments) => SaveTo(ProfilePath, arguments);

    internal static void SaveTo(string path, string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Array.Exists(arguments, argument => argument is null))
            throw new ArgumentException("A screensaver argument cannot be null.", nameof(arguments));
        var json = JsonSerializer.Serialize(arguments);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1_048_576)
            throw new ArgumentException("The screensaver profile exceeds 1 MB.", nameof(arguments));
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
