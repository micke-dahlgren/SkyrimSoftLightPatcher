using System.Reflection;
using NiflySharp;
using NiflySharp.Blocks;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;

namespace SkyrimLightingPatcher.NiflyAdapter.Reflection;

/// <summary>
/// Bridges the app's mesh abstraction to NiflySharp.
/// Uses reflection fallbacks to tolerate minor API/name differences between Nifly builds.
/// </summary>
public sealed class ReflectionNifMeshService : INifMeshService
{
    /// <summary>
    /// Reads patch-relevant shape/shader data from a NIF file.
    /// </summary>
    public Task<IReadOnlyList<NifShapeProbe>> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ProbeCore(filePath, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Applies planned shape operations and writes a patched copy to <paramref name="outputPath"/>.
    /// </summary>
    public Task WritePatchedFileAsync(
        string sourcePath,
        string outputPath,
        IReadOnlyList<ShapePatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => WritePatchedFileCore(sourcePath, outputPath, operations, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<NifShapeProbe> ProbeCore(string filePath, CancellationToken cancellationToken)
    {
        var nifFile = LoadFile(filePath);
        var probes = new List<NifShapeProbe>();
        var index = 0;

        foreach (var shape in nifFile.GetShapes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (shape is not BSTriShape and not NiTriShape)
            {
                index++;
                continue;
            }

            var shaderCandidate = nifFile.GetShader(shape) as BSLightingShaderProperty;
            if (shaderCandidate is null)
            {
                index++;
                continue;
            }

            var shader = ResolveShaderBlock(nifFile, shaderCandidate);

            var shapeName = GetShapeName(shape);
            probes.Add(new NifShapeProbe(
                filePath,
                $"{index}:{shapeName}",
                shapeName,
                BuildShaderMetadata(shader),
                GetTexturePaths(nifFile, shader),
                shader.HasSoftlight,
                shader.HasRimlight,
                shader.HasBacklight,
                GetLightingEffect1(shader),
                GetLightingEffect2(shader)));

            index++;
        }

        return probes;
    }

    private static void WritePatchedFileCore(
        string sourcePath,
        string outputPath,
        IReadOnlyList<ShapePatchOperation> operations,
        CancellationToken cancellationToken)
    {
        var nifFile = LoadFile(sourcePath);
        var operationsByKey = operations.ToDictionary(static operation => operation.ShapeKey, StringComparer.Ordinal);
        var applied = 0;
        var index = 0;

        foreach (var shape in nifFile.GetShapes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (shape is not BSTriShape and not NiTriShape)
            {
                index++;
                continue;
            }

            var shapeName = GetShapeName(shape);
            var shapeKey = $"{index}:{shapeName}";
            if (!operationsByKey.TryGetValue(shapeKey, out var operation))
            {
                index++;
                continue;
            }

            var shaderCandidate = nifFile.GetShader(shape) as BSLightingShaderProperty
                ?? throw new InvalidOperationException($"Shape '{shapeName}' no longer has a BSLightingShaderProperty.");
            var shader = ResolveShaderBlock(nifFile, shaderCandidate);

            if (operation.NewValue1.HasValue)
            {
                SetLightingEffect1(shader, operation.NewValue1.Value);
            }
            if (operation.NewValue2.HasValue)
            {
                SetLightingEffect2(shader, operation.NewValue2.Value);
            }
            if (operation.ClearSoftRimBackFlags)
            {
                shader.HasSoftlight = false;
                shader.HasRimlight = false;
                shader.HasBacklight = false;
            }
            applied++;
            index++;
        }

        if (applied == 0)
        {
            throw new InvalidOperationException("No matching shapes were found when applying the patch plan.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        nifFile.FinalizeData();
        var saveResult = nifFile.Save(outputPath, new NifFileSaveOptions());
        if (saveResult != 0)
        {
            throw new InvalidOperationException($"Nifly failed to save '{outputPath}' with result code {saveResult}.");
        }
    }

    private static NifFile LoadFile(string filePath)
    {
        var nifFile = new NifFile();
        using var stream = new MemoryStream(File.ReadAllBytes(filePath), writable: false);
        var loadResult = nifFile.Load(stream, new NifFileLoadOptions());
        if (loadResult != 0 || !nifFile.Valid)
        {
            throw new InvalidOperationException("Unable to open a NIF document through the Nifly runtime.");
        }

        return nifFile;
    }

    private static ShaderMetadata BuildShaderMetadata(BSLightingShaderProperty shader)
    {
        var flags = new List<string>();
        AddFlagStrings(flags, shader.ShaderFlags_SSPF1);
        AddFlagStrings(flags, shader.ShaderFlags_SSPF2);

        if (shader.HasEnvironmentMapping)
        {
            flags.Add(nameof(shader.HasEnvironmentMapping));
        }

        if (shader.HasEyeEnvironmentMapping)
        {
            flags.Add(nameof(shader.HasEyeEnvironmentMapping));
        }

        if (shader.HasBacklight)
        {
            flags.Add(nameof(shader.HasBacklight));
        }

        if (shader.HasRimlight)
        {
            flags.Add(nameof(shader.HasRimlight));
        }

        if (shader.HasSoftlight)
        {
            flags.Add(nameof(shader.HasSoftlight));
        }

        return new ShaderMetadata(shader.ShaderType_SK_FO4.ToString(), flags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyList<string> GetTexturePaths(NifFile nifFile, BSLightingShaderProperty shader)
    {
        var textureSetRef = GetFieldValue<object?>(shader, "_textureSet");
        if (textureSetRef is null)
        {
            return Array.Empty<string>();
        }

        var textureSet = nifFile.GetBlock((dynamic)textureSetRef) as BSShaderTextureSet;
        if (textureSet is null)
        {
            return Array.Empty<string>();
        }

        var texturesObject = GetRawFieldValue(textureSet, "_textures");
        if (texturesObject is null)
        {
            return Array.Empty<string>();
        }

        return ExtractStrings(texturesObject).ToArray();
    }

    private static string GetShapeName(object shape)
    {
        var property = shape.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
        if (property is not null)
        {
            var text = ExtractText(property.GetValue(shape));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        var field = shape.GetType().GetField("_name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is not null)
        {
            var fieldText = ExtractText(field.GetValue(shape));
            if (!string.IsNullOrWhiteSpace(fieldText))
            {
                return fieldText;
            }
        }

        return shape.GetType().Name;
    }

    private static void AddFlagStrings<TEnum>(ICollection<string> flags, TEnum value)
        where TEnum : struct, Enum
    {
        foreach (var part in value.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            flags.Add(part);
        }
    }

    private static BSLightingShaderProperty ResolveShaderBlock(NifFile nifFile, BSLightingShaderProperty shader)
    {
        // Re-resolve through block index when possible so writes target the canonical block instance.
        if (nifFile.GetBlockIndex(shader, out var blockIndex))
        {
            return nifFile.GetBlock<BSLightingShaderProperty>(blockIndex) ?? shader;
        }

        return shader;
    }

    private static float GetLightingEffect1(BSLightingShaderProperty shader)
    {
        // Nifly builds may expose different member casing, so probe both spellings before raw-field fallback.
        if (TryGetMemberValue(shader, "LightingEffect1", out float propertyValue) ||
            TryGetMemberValue(shader, "Lightingeffect1", out propertyValue))
        {
            return propertyValue;
        }

        return GetFieldValue<float>(shader, "_lightingEffect1");
    }

    private static void SetLightingEffect1(BSLightingShaderProperty shader, float value)
    {
        var propertyWasSet =
            TrySetMemberValue(shader, "LightingEffect1", value) ||
            TrySetMemberValue(shader, "Lightingeffect1", value);

        SetFieldValue(shader, "_lightingEffect1", value);

        var currentValue = GetLightingEffect1(shader);
        if (Math.Abs(currentValue - value) > 0.0001f && !propertyWasSet)
        {
            throw new InvalidOperationException("Unable to update LightingEffect1 on the shader block.");
        }
    }

    private static float GetLightingEffect2(BSLightingShaderProperty shader)
    {
        // Nifly builds may expose different member casing, so probe both spellings before raw-field fallback.
        if (TryGetMemberValue(shader, "LightingEffect2", out float propertyValue) ||
            TryGetMemberValue(shader, "Lightingeffect2", out propertyValue))
        {
            return propertyValue;
        }

        return GetFieldValue<float>(shader, "_lightingEffect2");
    }

    private static void SetLightingEffect2(BSLightingShaderProperty shader, float value)
    {
        var propertyWasSet =
            TrySetMemberValue(shader, "LightingEffect2", value) ||
            TrySetMemberValue(shader, "Lightingeffect2", value);

        SetFieldValue(shader, "_lightingEffect2", value);

        var currentValue = GetLightingEffect2(shader);
        if (Math.Abs(currentValue - value) > 0.0001f && !propertyWasSet)
        {
            throw new InvalidOperationException("Unable to update LightingEffect2 on the shader block.");
        }
    }

    private static T? GetFieldValue<T>(object source, string fieldName)
    {
        var value = GetRawFieldValue(source, fieldName);
        if (value is null)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        if (typeof(T).IsAssignableFrom(value.GetType()))
        {
            return (T)value;
        }

        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static object? GetRawFieldValue(object source, string fieldName)
    {
        var field = source.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found on {source.GetType().FullName}.");

        return field.GetValue(source);
    }

    private static void SetFieldValue(object source, string fieldName, object value)
    {
        var field = source.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found on {source.GetType().FullName}.");

        var targetType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
        var coercedValue = targetType.IsInstanceOfType(value)
            ? value
            : Convert.ChangeType(value, targetType);

        field.SetValue(source, coercedValue);
    }

    private static bool TryGetMemberValue<T>(object source, string memberName, out T value)
    {
        var property = source.GetType().GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is not null && property.CanRead)
        {
            var raw = property.GetValue(source);
            if (TryConvertValue(raw, out value))
            {
                return true;
            }
        }

        var field = source.GetType().GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null)
        {
            var raw = field.GetValue(source);
            if (TryConvertValue(raw, out value))
            {
                return true;
            }
        }

        value = default!;
        return false;
    }

    private static bool TrySetMemberValue(object source, string memberName, object value)
    {
        var property = source.GetType().GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is not null && property.CanWrite)
        {
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var coerced = propertyType.IsInstanceOfType(value) ? value : Convert.ChangeType(value, propertyType);
            property.SetValue(source, coerced);
            return true;
        }

        var field = source.GetType().GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null)
        {
            var fieldType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
            var coerced = fieldType.IsInstanceOfType(value) ? value : Convert.ChangeType(value, fieldType);
            field.SetValue(source, coerced);
            return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractStrings(object value)
    {
        if (value is string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }

            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                var textValue = ExtractText(item);
                if (!string.IsNullOrWhiteSpace(textValue))
                {
                    yield return textValue;
                }
            }

            yield break;
        }

        var singleValue = ExtractText(value);
        if (!string.IsNullOrWhiteSpace(singleValue))
        {
            yield return singleValue;
        }
    }

    private static string? ExtractText(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return text;
        }

        foreach (var propertyName in new[] { "Value", "Text", "String" })
        {
            var property = value.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(value) is string propertyText && !string.IsNullOrWhiteSpace(propertyText))
            {
                return propertyText;
            }
        }

        var result = value.ToString();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool TryConvertValue<T>(object? raw, out T value)
    {
        if (raw is null)
        {
            value = default!;
            return false;
        }

        if (raw is T typed)
        {
            value = typed;
            return true;
        }

        if (typeof(T).IsAssignableFrom(raw.GetType()))
        {
            value = (T)raw;
            return true;
        }

        try
        {
            value = (T)Convert.ChangeType(raw, typeof(T));
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }
}
