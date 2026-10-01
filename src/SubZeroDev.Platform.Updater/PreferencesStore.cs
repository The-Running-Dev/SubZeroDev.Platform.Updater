using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubZeroDev.Platform.Updater;

internal interface IPreferencesStore
{
    Task<UpdaterPreferences> LoadAsync(CancellationToken token);
    Task SaveAsync(UpdaterPreferences preferences, CancellationToken token);
}

internal sealed class PreferencesStore(string directory, Action<string>? log) : IPreferencesStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(Path.GetFullPath(directory), "updater.json");
    internal static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public async Task<UpdaterPreferences> LoadAsync(CancellationToken token)
    {
        try {
            if (!File.Exists(path)) return new();
            var value = JsonSerializer.Deserialize<UpdaterPreferences>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), Json);
            if (value is null || value.SchemaVersion != 1 || !Enum.IsDefined(value.Channel) || !Enum.IsDefined(value.ConsentMode))
                throw new JsonException("Unsupported updater preferences.");
            return value;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            try { log?.Invoke($"Updater preferences could not be loaded ({ex.GetType().Name}); using safe defaults."); } catch { }
            return new();
        }
    }

    public async Task SaveAsync(UpdaterPreferences preferences, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous)) {
                await JsonSerializer.SerializeAsync(stream, preferences, Json, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            for (int attempt = 0; ; attempt++) {
                token.ThrowIfCancellationRequested();
                try {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    break;
                } catch (IOException ex) when (attempt < 4 && (ex.HResult & 0xffff) is 32 or 33 or 1175) {
                    // Indexers and sync clients can briefly hold the destination without delete sharing.
                    // Retain the original file and retry the atomic replacement, never delete it first.
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), token).ConfigureAwait(false);
                }
            }
        } finally {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            gate.Release();
        }
    }
}
