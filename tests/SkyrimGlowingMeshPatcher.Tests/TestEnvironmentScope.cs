namespace SkyrimGlowingMeshPatcher.Tests;

internal sealed class TestEnvironmentScope : IDisposable
{
    private readonly string variableName;
    private readonly string? previousValue;

    public TestEnvironmentScope(string variableName, string value)
    {
        this.variableName = variableName;
        previousValue = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(variableName, previousValue);
    }
}
