using System.Text.Json;
using System.Text.Json.Nodes;
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
    // The file as last read, so fields written by a newer version survive this version's saves.
    private JsonObject? original;
    private readonly string path = Path.Combine(Path.GetFullPath(directory), "updater.json");
    internal static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public async Task<UpdaterPreferences> LoadAsync(CancellationToken token)
    {
        try {
            if (!File.Exists(path)) return new();
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false)) as JsonObject ?? throw new JsonException("Unsupported updater preferences.");
            int version = node["schemaVersion"] is JsonValue raw && raw.TryGetValue<int>(out var number) ? number : 1;
            if (version < 1) throw new JsonException("Unsupported updater preferences.");
            var value = version == 1 ? node.Deserialize<UpdaterPreferences>(Json) : ReadNewer(node, version);
            if (value is null || !Enum.IsDefined(value.Channel) || !Enum.IsDefined(value.ConsentMode)) throw new JsonException("Unsupported updater preferences.");
            original = node;
            return value;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            try { log?.Invoke($"Updater preferences could not be loaded ({ex.GetType().Name}); using safe defaults."); } catch { }
            return new();
        }
    }

    // A newer version may add fields or values this one does not know; keep what is understood and default the rest.
    private static UpdaterPreferences ReadNewer(JsonObject node, int version)
    {
        T Field<T>(string name, T fallback)
        {
            try { return node[name] is { } value && value.Deserialize<T>(Json) is { } parsed ? parsed : fallback; }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return fallback; }
        }
        var defaults = new UpdaterPreferences();
        var channel = Field("channel", defaults.Channel);
        var consent = Field("consentMode", defaults.ConsentMode);
        return defaults with {
            SchemaVersion = version,
            CheckAutomatically = Field("checkAutomatically", defaults.CheckAutomatically),
            Channel = Enum.IsDefined(channel) ? channel : defaults.Channel,
            ConsentMode = Enum.IsDefined(consent) ? consent : defaults.ConsentMode,
            LastAutomaticNetworkCheckUtc = Field<DateTimeOffset?>("lastAutomaticNetworkCheckUtc", null),
            LastSuccessfulCheckUtc = Field<DateTimeOffset?>("lastSuccessfulCheckUtc", null),
            LastOfferedVersion = Field<string?>("lastOfferedVersion", null),
            OfferDeferredUntilUtc = Field<DateTimeOffset?>("offerDeferredUntilUtc", null),
            PendingInstallVersion = Field<string?>("pendingInstallVersion", null)
        };
    }

    public async Task SaveAsync(UpdaterPreferences preferences, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var merged = original?.DeepClone() as JsonObject ?? new JsonObject();
            foreach (var property in (JsonSerializer.SerializeToNode(preferences, Json) as JsonObject)!) merged[property.Key] = property.Value?.DeepClone();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous)) {
                await JsonSerializer.SerializeAsync(stream, merged, Json, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            for (int attempt = 0; ; attempt++) {
                token.ThrowIfCancellationRequested();
                try {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    original = merged;
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
