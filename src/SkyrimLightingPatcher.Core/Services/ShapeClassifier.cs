using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class ShapeClassifier : IShapeClassifier
{
    private static readonly HashSet<string> SharedEyeMeshes = new(StringComparer.OrdinalIgnoreCase)
    {
        "eyeshuman",
        "eyehuman",
        "femaleheadhumaneyes",
        "maleheadhumaneyes",
        "eyeball",
        "eyeleft",
        "eyeright",
    };

    private static readonly HashSet<string> SharedBodyMeshes = new(StringComparer.OrdinalIgnoreCase)
    {
        "femalebody_0",
        "femalebody_1",
        "malebody_0",
        "malebody_1",
        "femalehands_0",
        "femalehands_1",
        "malehands_0",
        "malehands_1",
        "femalefeet_0",
        "femalefeet_1",
        "malefeet_0",
        "malefeet_1",
        "femalehead",
        "malehead",
        "femaleheadhuman",
        "maleheadhuman",
    };

    private static readonly string[] ExclusionTokens =
    [
        "eyelash",
        "eyelashes",
        "eyebrow",
        "eyebrows",
        "lash",
        "brow",
        "hair",
        "teeth",
        "tongue",
        "beard",
        "helmet",
        "armor",
        "armour",
        "gauntlet",
        "boot",
        "clothes",
        "robe",
    ];

    public ShapeClassification Classify(NifShapeProbe probe)
    {
        var reasons = new List<string>();
        var normalizedFilePath = PathUtility.NormalizeSlashes(probe.FilePath);
        var normalizedShapeName = PathUtility.NormalizeSlashes(probe.ShapeName);
        var normalizedTexturePaths = probe.TexturePaths.Select(PathUtility.NormalizeSlashes).ToArray();
        var combinedMetadata = string.Join(
            " ",
            normalizedTexturePaths
                .Append(normalizedFilePath)
                .Append(normalizedShapeName)
                .Append(probe.Shader.ShaderType ?? string.Empty)
                .Concat(probe.Shader.Flags));

        if (ContainsAnyToken(combinedMetadata, ExclusionTokens))
        {
            reasons.Add("Excluded by known non-skin/non-eye token.");
            return ShapeClassification.Ignore("Excluded", reasons.ToArray());
        }

        if (TryClassifyEye(probe, normalizedFilePath, normalizedShapeName, normalizedTexturePaths, reasons))
        {
            return new ShapeClassification(ShapeKind.Eye, "Eye", reasons);
        }

        if (TryClassifyBody(probe, normalizedFilePath, normalizedShapeName, normalizedTexturePaths, reasons))
        {
            return new ShapeClassification(ShapeKind.Body, "Body", reasons);
        }

        reasons.Add("No strong eye or body signal matched.");
        return new ShapeClassification(ShapeKind.Other, "Other", reasons.ToArray());
    }

    private static bool TryClassifyEye(
        NifShapeProbe probe,
        string normalizedFilePath,
        string normalizedShapeName,
        IReadOnlyList<string> normalizedTexturePaths,
        List<string> reasons)
    {
        if (normalizedTexturePaths.Any(static texture => texture.Contains(@"textures\actors\character\eyes\", StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("Matched eye texture directory.");
            return true;
        }

        var fileStem = Path.GetFileNameWithoutExtension(normalizedFilePath);
        if (fileStem is not null && SharedEyeMeshes.Contains(fileStem))
        {
            reasons.Add("Matched shared eye mesh allowlist.");
            return true;
        }

        if (normalizedShapeName.Contains("eye", StringComparison.OrdinalIgnoreCase) &&
            !normalizedShapeName.Contains("eyelash", StringComparison.OrdinalIgnoreCase) &&
            !normalizedShapeName.Contains("eyebrow", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Shape name contains eye.");
            return true;
        }

        var shaderTokens = string.Join(" ", probe.Shader.Flags.Append(probe.Shader.ShaderType ?? string.Empty));
        if (shaderTokens.Contains("eye", StringComparison.OrdinalIgnoreCase) ||
            shaderTokens.Contains("envmap", StringComparison.OrdinalIgnoreCase) ||
            shaderTokens.Contains("environmentmap", StringComparison.OrdinalIgnoreCase) ||
            shaderTokens.Contains("environment map", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Shader metadata indicates eye or environment mapping.");
            return true;
        }

        return false;
    }

    private static bool TryClassifyBody(
        NifShapeProbe probe,
        string normalizedFilePath,
        string normalizedShapeName,
        IReadOnlyList<string> normalizedTexturePaths,
        List<string> reasons)
    {
        var fileStem = Path.GetFileNameWithoutExtension(normalizedFilePath);
        if (fileStem is not null && SharedBodyMeshes.Contains(fileStem))
        {
            reasons.Add("Matched shared body mesh allowlist.");
            return true;
        }

        if (normalizedTexturePaths.Any(IsSkinTexturePath))
        {
            reasons.Add("Matched skin-like texture path.");
            return true;
        }

        if (normalizedShapeName.Contains("body", StringComparison.OrdinalIgnoreCase) ||
            normalizedShapeName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
            normalizedShapeName.Contains("hands", StringComparison.OrdinalIgnoreCase) ||
            normalizedShapeName.Contains("feet", StringComparison.OrdinalIgnoreCase) ||
            normalizedShapeName.Contains("skin", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Matched body-oriented shape name.");
            return true;
        }

        if (normalizedFilePath.Contains(@"facegendata\facegeom\", StringComparison.OrdinalIgnoreCase))
        {
            if (normalizedTexturePaths.Any(static texture => texture.Contains(@"textures\actors\character\", StringComparison.OrdinalIgnoreCase)))
            {
                reasons.Add("FaceGen mesh with character texture path.");
                return true;
            }

            if (normalizedShapeName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                normalizedShapeName.Contains("face", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("FaceGen mesh with head-like shape name.");
                return true;
            }
        }

        return false;
    }

    private static bool IsSkinTexturePath(string texturePath)
    {
        if (!texturePath.Contains(@"textures\actors\character\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return texturePath.Contains("body", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("head", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("hands", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("feet", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("skin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAnyToken(string value, IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
