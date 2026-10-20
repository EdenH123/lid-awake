using System.Text.Json;
using System.Text.Json.Nodes;

namespace LidAwake;

// Everything Lid Awake remembers, in %APPDATA%\LidAwake\settings.json (the Mac keeps the same
// things in UserDefaults). A second instance, on machine.json, keeps what is only true of this PC.
// Written whole on every change, to a temp file that then replaces it, so a crash mid-write
// cannot leave a truncated file.
public sealed class Settings
{
    readonly string path;
    readonly object gate = new();
    JsonObject data;

    public const string MachineFileName = "machine.json";

    public Settings(string? folder = null, string fileName = "settings.json")
    {
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LidAwake");
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, fileName);
        data = Load(path);
    }

    // What belongs to this PC and must not roam with the user's profile (the lid values Lid Awake
    // saved and the power plan they came from, the brightness, the phone-alert key and phones, and
    // this PC's Tailscale name and Serve state): %LOCALAPPDATA%\LidAwake\machine.json.
    public static Settings ForMachine(string? folder = null) =>
        new(folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LidAwake"), MachineFileName);

    // A damaged file must not stop the app from starting. A file that does not parse is moved
    // aside as settings.json.bad, and the app starts with nothing remembered.
    static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new();
        }
        catch (JsonException)
        {
            try { File.Move(path, path + ".bad", overwrite: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new();
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
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
            Save();
        }
    }

    // Takes the key out of the file altogether (not even null is left).
    public void Remove(string key)
    {
        lock (gate)
        {
            if (!data.Remove(key)) return;
            Save();
        }
    }

    // Moves these keys to `to` (from the roaming settings to machine.json): a value `to` doesn't
    // have yet is written there first, then the key is removed here. One that couldn't be moved
    // (a file couldn't be written) stays where it was and is tried again next time.
    public void MoveTo(Settings to, params string[] keys)
    {
        foreach (var key in keys)
        {
            JsonNode? value;
            lock (gate)
            {
                if (!data.TryGetPropertyValue(key, out value)) continue;
                value = value?.DeepClone();
            }
            try
            {
                if (value != null) to.SetIfMissing(key, value);
                Remove(key);
            }
            catch (Exception) { }
        }
    }

    void SetIfMissing(string key, JsonNode value)
    {
        lock (gate)
        {
            if (data.TryGetPropertyValue(key, out var there) && there is not null) return;
            data[key] = value;
            Save();
        }
    }

    // Under gate.
    void Save()
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, data.ToJsonString());
        File.Move(tmp, path, overwrite: true);
    }
}
