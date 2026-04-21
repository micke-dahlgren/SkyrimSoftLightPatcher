using System.Text.RegularExpressions;

namespace SkyrimGlowingMeshPatcher.Core.Services;

internal sealed record ModOrganizer2Paths(
    string InstancePath,
    string GameName,
    string? GamePath,
    string ModsPath,
    string ProfilesPath,
    string SelectedProfileName,
    string SelectedProfilePath);

internal static class ModOrganizer2Support
{
    private const string IniFileName = "ModOrganizer.ini";

    public static bool TryLoadFromRootPath(string rootPath, out ModOrganizer2Paths? paths)
    {
        paths = null;
        var current = Path.GetFullPath(rootPath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (TryLoadFromInstanceRoot(current, out paths))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    public static bool TryLoadFromInstanceRoot(string instancePath, out ModOrganizer2Paths? paths)
    {
        paths = null;
        var fullInstancePath = Path.GetFullPath(instancePath);
        var iniPath = Path.Combine(fullInstancePath, IniFileName);
        if (!File.Exists(iniPath))
        {
            return false;
        }

        Dictionary<string, string> values;
        try
        {
            values = ReadIniValues(iniPath);
        }
        catch
        {
            return false;
        }

        var gameName = GetValue(values, "gameName") ?? string.Empty;
        var gamePath = NormalizeIniText(GetValue(values, "gamePath"));

        var baseDirectory = ResolveDirectory(
            instancePath: fullInstancePath,
            baseDirectory: fullInstancePath,
            rawValue: GetValue(values, "base_directory"),
            defaultRelativePath: string.Empty);

        var modsPath = ResolveDirectory(
            instancePath: fullInstancePath,
            baseDirectory: baseDirectory,
            rawValue: GetValue(values, "mod_directory"),
            defaultRelativePath: "mods");

        var profilesPath = ResolveDirectory(
            instancePath: fullInstancePath,
            baseDirectory: baseDirectory,
            rawValue: GetValue(values, "profiles_directory"),
            defaultRelativePath: "profiles");

        var selectedProfileName = NormalizeIniText(GetValue(values, "selected_profile"));
        if (string.IsNullOrWhiteSpace(selectedProfileName) && Directory.Exists(profilesPath))
        {
            selectedProfileName = Directory.EnumerateDirectories(profilesPath)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        }

        selectedProfileName ??= string.Empty;
        var selectedProfilePath = Path.Combine(profilesPath, selectedProfileName);

        paths = new ModOrganizer2Paths(
            fullInstancePath,
            gameName,
            gamePath,
            modsPath,
            profilesPath,
            selectedProfileName,
            selectedProfilePath);

        return true;
    }

    public static bool IsSkyrimSpecialEdition(ModOrganizer2Paths paths)
    {
        if (paths.GameName.Contains("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(paths.GamePath) &&
               paths.GamePath.Contains("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ReadEnabledManagedMods(ModOrganizer2Paths paths)
    {
        var modListPath = Path.Combine(paths.SelectedProfilePath, "modlist.txt");
        if (!File.Exists(modListPath))
        {
            return [];
        }

        var results = new List<string>();
        foreach (var rawLine in File.ReadAllLines(modListPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (!line.StartsWith('+'))
            {
                continue;
            }

            var modName = line[1..].Trim();
            if (!string.IsNullOrWhiteSpace(modName))
            {
                results.Add(modName);
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> ReadPluginLoadOrder(ModOrganizer2Paths paths)
    {
        var candidates = new[]
        {
            Path.Combine(paths.SelectedProfilePath, "loadorder.txt"),
            Path.Combine(paths.SelectedProfilePath, "plugins.txt"),
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var plugins = ParsePluginList(File.ReadAllLines(candidate));
            if (plugins.Count > 0)
            {
                return plugins;
            }
        }

        return [];
    }

    private static List<string> ParsePluginList(IEnumerable<string> lines)
    {
        var results = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('*'))
            {
                line = line[1..].Trim();
            }

            if (line.StartsWith('-'))
            {
                continue;
            }

            if (!(line.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                  line.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                  line.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(Path.GetFileNameWithoutExtension(line));
        }

        return results;
    }

    private static Dictionary<string, string> ReadIniValues(string iniPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(iniPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            values[key] = value;
        }

        return values;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value)
            ? value
            : null;
    }

    private static string ResolveDirectory(
        string instancePath,
        string baseDirectory,
        string? rawValue,
        string defaultRelativePath)
    {
        var normalized = NormalizeIniText(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.IsNullOrWhiteSpace(defaultRelativePath)
                ? baseDirectory
                : Path.GetFullPath(Path.Combine(baseDirectory, defaultRelativePath));
        }

        normalized = normalized.Replace("%BASE_DIR%", baseDirectory, StringComparison.OrdinalIgnoreCase);

        if (!Path.IsPathRooted(normalized))
        {
            normalized = Path.Combine(instancePath, normalized);
        }

        return Path.GetFullPath(normalized);
    }

    private static string? NormalizeIniText(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var value = rawValue.Trim();
        var byteArrayMatch = Regex.Match(value, "^@ByteArray\\((.*)\\)$", RegexOptions.IgnoreCase);
        if (byteArrayMatch.Success)
        {
            value = byteArrayMatch.Groups[1].Value;
        }

        value = value.Replace("\\\\", "\\");
        value = value.Replace('/', Path.DirectorySeparatorChar);
        return value.Trim();
    }
}
