using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;

namespace SkyrimLightingPatcher.Tests;

public sealed class ShapeClassifierTests
{
    private readonly ShapeClassifier classifier = new();

    [Fact]
    public void Classify_EyeTextureDirectory_ReturnsEye()
    {
        var probe = CreateProbe(
            filePath: @"C:\Mods\Meshes\actors\character\eyes\eyeshuman.nif",
            shapeName: "Eyes",
            texturePaths: [@"textures\actors\character\eyes\blue.dds"]);

        var result = classifier.Classify(probe);

        Assert.Equal(ShapeKind.Eye, result.Kind);
    }

    [Fact]
    public void Classify_EyelashShape_IsIgnoredEvenIfEyeTokenExists()
    {
        var probe = CreateProbe(
            filePath: @"C:\Mods\Meshes\actors\character\facegendata\facegeom\npc.nif",
            shapeName: "EyeLashes",
            texturePaths: [@"textures\actors\character\character assets\femalehead.dds"]);

        var result = classifier.Classify(probe);

        Assert.Equal(ShapeKind.Ignore, result.Kind);
    }

    [Fact]
    public void Classify_SharedBodyMesh_ReturnsBody()
    {
        var probe = CreateProbe(
            filePath: @"C:\Mods\Meshes\actors\character\character assets\femalebody_1.nif",
            shapeName: "Body",
            texturePaths: [@"textures\actors\character\character assets\femalebody_1.dds"]);

        var result = classifier.Classify(probe);

        Assert.Equal(ShapeKind.Body, result.Kind);
    }

    [Fact]
    public void Classify_FaceGenHeadWithSkinTexture_ReturnsBody()
    {
        var probe = CreateProbe(
            filePath: @"C:\Mods\Meshes\actors\character\facegendata\facegeom\SomeMod\000ABC.nif",
            shapeName: "Head",
            texturePaths: [@"textures\actors\character\female\femalebody_1.dds"]);

        var result = classifier.Classify(probe);

        Assert.Equal(ShapeKind.Body, result.Kind);
    }

    [Fact]
    public void Classify_EnvMapShader_ReturnsEye()
    {
        var probe = CreateProbe(
            filePath: @"C:\Mods\Meshes\actors\character\misc\custom.nif",
            shapeName: "Orb",
            texturePaths: [@"textures\actors\character\misc\orb.dds"],
            shaderType: "EnvironmentMap");

        var result = classifier.Classify(probe);

        Assert.Equal(ShapeKind.Eye, result.Kind);
    }

    private static NifShapeProbe CreateProbe(
        string filePath,
        string shapeName,
        IReadOnlyList<string> texturePaths,
        string? shaderType = null)
    {
        return new NifShapeProbe(
            filePath,
            $"{Path.GetFileName(filePath)}:{shapeName}",
            shapeName,
            new ShaderMetadata(shaderType, Array.Empty<string>()),
            texturePaths,
            HasSoftLighting: true,
            LightingEffect1: 0.25f);
    }
}
