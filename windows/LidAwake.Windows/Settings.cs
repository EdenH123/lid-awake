using System.Text.Json;
using System.Text.Json.Nodes;

namespace LidAwake;

// Everything Lid Awake remembers, in %APPDATA%\LidAwake\settings.json (the Mac keeps the same
// things in UserDefaults). Written whole on every change; small enough not to matter.
public sealed class Settings
{
    readonly string path;
    readonly object gate = new();
    JsonObject data;

    public Settings(string? folder = null)
    {
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LidAwake");
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "settings.json");
        data = File.Exists(path) ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new()) : new();
    }

    public T? Get<T>(string key)
    {
        lock (gate) return data.TryGetPropertyValue(key, out var v) && v is not null ? v.Deserialize<T>() : default;
    }

    public void Set<T>(string key, T? value)
    {
        lock (gate)
        {
            data[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
            File.WriteAllText(path, data.ToJsonString());
        }
    }
}
