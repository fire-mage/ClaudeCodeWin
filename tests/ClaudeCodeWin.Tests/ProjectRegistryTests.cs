using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.Tests;

public class ProjectRegistryTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CCWTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void DetectsGitRoot()
    {
        // Create .git directory
        var projectDir = Path.Combine(_tempDir, "myproject");
        Directory.CreateDirectory(Path.Combine(projectDir, ".git"));
        Directory.CreateDirectory(Path.Combine(projectDir, "src"));

        var filePath = Path.Combine(projectDir, "src", "main.cs");
        File.WriteAllText(filePath, "// test");

        var root = ProjectRegistryService.DetectProjectRoot(filePath);

        Assert.Equal(projectDir, root);
    }

    [Fact]
    public void DetectsCsprojRoot()
    {
        var projectDir = Path.Combine(_tempDir, "dotnetproject");
        Directory.CreateDirectory(Path.Combine(projectDir, "src"));

        File.WriteAllText(Path.Combine(projectDir, "MyApp.csproj"), "<Project />");
        var filePath = Path.Combine(projectDir, "src", "Program.cs");
        File.WriteAllText(filePath, "// test");

        var root = ProjectRegistryService.DetectProjectRoot(filePath);

        Assert.Equal(projectDir, root);
    }

    [Fact]
    public void NoMarkers_ReturnsNull()
    {
        // Create a file without any project markers up the tree (within 10 levels)
        var deepDir = _tempDir;
        for (int i = 0; i < 11; i++)
            deepDir = Path.Combine(deepDir, $"level{i}");
        Directory.CreateDirectory(deepDir);

        var filePath = Path.Combine(deepDir, "orphan.txt");
        File.WriteAllText(filePath, "test");

        var root = ProjectRegistryService.DetectProjectRoot(filePath);

        // Should be null since we're 11 levels deep and no markers exist
        // (the temp dir itself might have markers above it, but we traverse max 10 levels)
        Assert.Null(root);
    }

    [Fact]
    public void DetectsPackageJsonRoot()
    {
        var projectDir = Path.Combine(_tempDir, "nodeproject");
        Directory.CreateDirectory(Path.Combine(projectDir, "src", "components"));

        File.WriteAllText(Path.Combine(projectDir, "package.json"), "{}");
        var filePath = Path.Combine(projectDir, "src", "components", "App.tsx");
        File.WriteAllText(filePath, "// test");

        var root = ProjectRegistryService.DetectProjectRoot(filePath);

        Assert.Equal(projectDir, root);
    }
}
