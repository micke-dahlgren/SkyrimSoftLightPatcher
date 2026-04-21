using System.Text.Json;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string settingsPath = Path.Combine(PatchOutputPaths.GetApplicationHomeDirectory(), "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsPath))
        {
            return AppSettings.Default;
        }

        await using var stream = File.OpenRead(settingsPath);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, serializerOptions, cancellationToken).ConfigureAwait(false)
               ?? AppSettings.Default;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await using var stream = File.Open(settingsPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, settings, serializerOptions, cancellationToken).ConfigureAwait(false);
    }

}
