using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

internal sealed class StorageService
{
    private const string FileName = "MatrixDesktopConfigurator.presets.json";
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string StoragePath { get; }
    public bool UsesPortablePath { get; }

    public StorageService()
    {
        (StoragePath, UsesPortablePath) = ResolveStoragePath();
    }

    public ConfiguratorState Load()
    {
        try
        {
            if (!File.Exists(StoragePath))
            {
                return new ConfiguratorState();
            }

            var json = File.ReadAllText(StoragePath);
            var state = JsonSerializer.Deserialize<ConfiguratorState>(json, _jsonOptions) ?? new ConfiguratorState();
            state.SchemaVersion = Math.Max(1, state.SchemaVersion);
            state.PresetSeedVersion = Math.Max(0, state.PresetSeedVersion);
            state.LastDraft ??= [];
            state.UserPresets ??= [];
            return state;
        }
        catch
        {
            return new ConfiguratorState();
        }
    }

    public void Save(ConfiguratorState state)
    {
        var directory = Path.GetDirectoryName(StoragePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(StoragePath, JsonSerializer.Serialize(state, _jsonOptions));
    }

    public static JsonObject CloneObject(JsonObject? source)
    {
        if (source is null)
        {
            return [];
        }

        return JsonNode.Parse(source.ToJsonString())?.AsObject() ?? [];
    }

    private static (string Path, bool Portable) ResolveStoragePath()
    {
        var portablePath = Path.Combine(AppContext.BaseDirectory, FileName);
        if (CanWriteStorageFile(portablePath))
        {
            return (portablePath, true);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.GetTempPath();
        }

        return (Path.Combine(appData, "MatrixDesktop", "Configurator", FileName), false);
    }

    private static bool CanWriteStorageFile(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            return stream.CanWrite;
        }
        catch
        {
            return false;
        }
    }
}
