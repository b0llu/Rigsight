using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rigsight.Core.Ai;

/// <summary>
/// One saved key: the service it's for, the name the person gave it, the key itself (empty for a server of their own
/// that needs none), the server's address for "Custom", the model picked with it, and when it was added and last used.
/// </summary>
public sealed record SavedKey(string Id, string Service, string Name, string Secret, string Address, string Model, DateTime Added, DateTime? LastUsed);

/// <summary>
/// Every key pasted in Settings › Ask RigSight, one of them in use (the user's ask, 2026-10-27: keep keys for several
/// services and switch between them). All of it, the keys too, is one file, ai-keys.bin, locked to the Windows account
/// with DPAPI (<see cref="KeyStore.Protect"/>): never settings.json, never the log.
/// <para>
/// Moving from the one-key days: the first read with no ai-keys.bin but an old ai-key.bin makes one saved key from it.
/// The app calls <see cref="Migrate"/> at start with the settings' service, model and address so that key keeps them;
/// a read before that call uses OpenRouter, the default service. The old file goes only once the new one is written.
/// </para>
/// </summary>
public static class SavedKeys
{
    /// <summary>A test's own folder, so tests running side by side never share keys.</summary>
    internal static readonly AsyncLocal<string?> TestFolder = new();

    /// <summary>Where the keys live: the data folder, or a test's own.</summary>
    internal static string Folder => TestFolder.Value ?? RigsightPaths.DataDir;

    public static string File => Path.Combine(Folder, "ai-keys.bin");

    /// <summary>Something changed (added, used, removed, renamed, a model picked).</summary>
    public static event Action? Changed;

    private static readonly object Gate = new();
    private static (string Service, string Model, string Address) _old = ("OpenRouter", "", "");

    private sealed class Stored
    {
        public string? Active { get; set; }
        public List<SavedKey> Keys { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>
    /// The settings' service, model and address from the one-key days, for the key moved from ai-key.bin. Called once at
    /// start, before anything reads the keys; moves the old key straight away when there is one.
    /// </summary>
    public static void Migrate(string service, string model, string customAddress)
    {
        lock (Gate)
        {
            _old = (service ?? "", model ?? "", customAddress ?? "");
            Read();
        }
    }

    public static IReadOnlyList<SavedKey> All() { lock (Gate) return Read().Keys; }

    /// <summary>The key answers use; null when none is saved.</summary>
    public static SavedKey? Active()
    {
        lock (Gate)
        {
            var s = Read();
            return s.Keys.FirstOrDefault(k => k.Id == s.Active);
        }
    }

    /// <summary>A key that worked, saved under the name given (the service's name when none) and put in use.</summary>
    public static SavedKey Add(string service, string name, string secret, string address, string model)
    {
        SavedKey key;
        lock (Gate)
        {
            var s = Read();
            key = new SavedKey(NewId(s), service, name.Trim().Length > 0 ? name.Trim() : DefaultName(service, address), secret.Trim(), address.Trim(), model, DateTime.Now, null);
            s.Keys.Add(key);
            s.Active = key.Id;
            Write(s);
        }
        Log.Write("own-key", $"Saved a key for {service} as \"{key.Name}\" ({Mask(key)}), now in use; {All().Count} saved");
        Changed?.Invoke();
        return key;
    }

    /// <summary>Puts a saved key in use.</summary>
    public static void Use(string id)
    {
        lock (Gate)
        {
            var s = Read();
            if (s.Keys.All(k => k.Id != id) || s.Active == id) return;
            s.Active = id;
            Write(s);
        }
        Log.Write("own-key", $"Answers now use the saved key {Describe(id)}");
        Changed?.Invoke();
    }

    /// <summary>Forgets a key on this PC. The one in use gone: the first left takes over, or none.</summary>
    public static void Remove(string id)
    {
        string what = Describe(id);
        lock (Gate)
        {
            var s = Read();
            if (s.Keys.RemoveAll(k => k.Id == id) == 0) return;
            if (s.Active == id) s.Active = s.Keys.FirstOrDefault()?.Id;
            Write(s);
        }
        Log.Write("own-key", $"Removed the saved key {what}; {All().Count} left, in use: {(Active() is { } a ? a.Name : "none")}");
        Changed?.Invoke();
    }

    public static void SetModel(string id, string model) => Change(id, k => k with { Model = model ?? "" });
    public static void Rename(string id, string name) => Change(id, k => k with { Name = name.Trim().Length > 0 ? name.Trim() : DefaultName(k.Service, k.Address) });

    /// <summary>The key in use was just used for a question.</summary>
    public static void Touch()
    {
        lock (Gate)
        {
            var s = Read();
            int i = s.Keys.FindIndex(k => k.Id == s.Active);
            if (i < 0) return;
            s.Keys[i] = s.Keys[i] with { LastUsed = DateTime.Now };
            Write(s);
        }
    }

    /// <summary>
    /// The key as it's shown once saved: its start to the first dash after the first two letters, then the last four
    /// ("sk-or-…a41f"); a server shows its address without http://. A saved key is never shown whole again.
    /// </summary>
    public static string Mask(SavedKey k)
    {
        if (k.Secret.Length == 0) return Bare(k.Address);
        string s = k.Secret;
        int dash = s.Length > 3 ? s.IndexOf('-', 3) : -1;
        string start = dash > 0 && dash < 12 ? s[..(dash + 1)] : s[..Math.Min(3, s.Length)];
        string end = s.Length > start.Length + 4 ? s[^4..] : "";
        string key = start + "…" + end;
        return k.Address.Length > 0 ? $"{Bare(k.Address)} · {key}" : key;
    }

    /// <summary>For <see cref="KeyStore.Save"/>: the key in use gets the new key, or it becomes a new saved key.</summary>
    internal static void SaveSecret(string secret)
    {
        lock (Gate)
        {
            var s = Read();
            int i = s.Keys.FindIndex(k => k.Id == s.Active);
            if (i >= 0)
            {
                s.Keys[i] = s.Keys[i] with { Secret = secret.Trim() };
                Write(s);
                Changed?.Invoke();
                return;
            }
        }
        var service = KeyDetect.Service(secret) ?? _old.Service;
        Add(service, "", secret, "", "");
    }

    private static void Change(string id, Func<SavedKey, SavedKey> change)
    {
        lock (Gate)
        {
            var s = Read();
            int i = s.Keys.FindIndex(k => k.Id == id);
            if (i < 0) return;
            s.Keys[i] = change(s.Keys[i]);
            Write(s);
        }
        Changed?.Invoke();
    }

    private static string Describe(string id) => All().FirstOrDefault(k => k.Id == id) is { } k ? $"\"{k.Name}\" ({Mask(k)})" : id;

    private static string Bare(string address) =>
        address.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("http://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');

    private static string DefaultName(string service, string address) =>
        address.Length > 0 && !KnownService(service) ? Lang.T("Your own server") : service;

    private static bool KnownService(string service) => service is "OpenRouter" or "OpenAI" or "Anthropic" or "Google Gemini" or "DeepSeek" or "xAI" or "Mistral" or "Groq";

    private static string NewId(Stored s)
    {
        string id;
        do id = Guid.NewGuid().ToString("N")[..8]; while (s.Keys.Any(k => k.Id == id));
        return id;
    }

    /// <summary>The saved keys; the old one-key file moved in on the first read; nothing when the file can't be read.</summary>
    private static Stored Read()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Stored>(Encoding.UTF8.GetString(KeyStore.Unprotect(System.IO.File.ReadAllBytes(File))), Json) ?? new Stored();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyStore.CryptographicException or JsonException)
        {
            Log.Write("own-key", $"The saved keys couldn't be read ({ex.GetType().Name}: {ex.Message}); going on with none");
            return new Stored();
        }
        // The one-key days: that key becomes the first saved key, with the settings' service, model and address.
        if (KeyStore.LoadOld() is not { Length: > 0 } old) return new Stored();
        var (service, model, address) = _old;
        if (address.Trim().Length > 0 && !KnownService(service)) service = "Custom";
        else if (!KnownService(service)) service = KeyDetect.Service(old) ?? "OpenRouter";
        if (service != "Custom") address = "";
        var moved = new Stored();
        var key = new SavedKey(NewId(moved), service, DefaultName(service, address), old.Trim(), address.Trim(), model, DateTime.Now, null);
        moved.Keys.Add(key);
        moved.Active = key.Id;
        try
        {
            Write(moved);
            System.IO.File.Delete(KeyStore.File);
            Log.Write("own-key", $"Moved the one saved key ({Mask(key)}, {service}) into the saved keys");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyStore.CryptographicException)
        {
            Log.Write("own-key", $"Couldn't move the old key into the saved keys ({ex.Message}); the old file stays");
        }
        return moved;
    }

    private static void Write(Stored s)
    {
        Directory.CreateDirectory(Folder);
        System.IO.File.WriteAllBytes(File, KeyStore.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s, Json))));
    }
}
