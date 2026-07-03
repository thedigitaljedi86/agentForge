namespace DevAgent.Forge.Tests;

using DevAgent.Forge;
using DevAgent.Forge.Tools;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;
using Xunit;

/// <summary>
/// The write-scope guarantees behind the new agents:
///  * DocScribe (docs-only scope) can never modify code — only docs/ + README.md.
///  * CodeReviewer (read-only scope) can never write anything at all.
///  * Existing agents keep the AllowAll default and are unaffected.
/// The scope is a POLICY enforced by the tools, not a prompt instruction.
/// </summary>
public class WriteScopePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devagent-scope-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspacePathValidator _paths;
    private readonly ProtectedFilePolicy _protected = new();

    public WriteScopePolicyTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new WorkspacePathValidator(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static WriteScopePolicy DocsOnly => WriteScopePolicy.FromPrefixes(new[] { "docs/", "README.md" });

    private WorkspaceFileTool Files(WriteScopePolicy scope) => new(_paths, _protected, allowDeploymentEdits: false, scope);
    private PatchApplicationService Patches(WriteScopePolicy scope) => new(_paths, _protected, allowDeploymentEdits: false, scope);

    private void WriteFile(string rel, string content)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Theory]
    [InlineData("docs/ARCHITECTURE.md")]
    [InlineData("docs/areas/hub.md")]
    [InlineData("README.md")]
    public async Task Docs_scope_allows_writes_inside_docs(string path)
    {
        var result = await Files(DocsOnly).ReplaceAsync(new ReplaceFileToolCall { RelativePath = path, NewContent = "# Docs\n" });
        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("DevAgent.sln")]
    [InlineData("README2.md")]
    [InlineData("docsX/notes.md")]
    public async Task Docs_scope_denies_writes_outside_docs(string path)
    {
        var result = await Files(DocsOnly).ReplaceAsync(new ReplaceFileToolCall { RelativePath = path, NewContent = "x" });
        Assert.True(result.DeniedByPolicy);
        Assert.Contains("write scope", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docs_scope_denies_patches_outside_docs()
    {
        WriteFile("src/Program.cs", "a\n");
        var result = await Patches(DocsOnly).ApplyAsync(new ApplyPatchToolCall
        {
            RelativePath = "src/Program.cs",
            UnifiedDiff = "@@ -1,1 +1,1 @@\n-a\n+b\n",
        });
        Assert.True(result.DeniedByPolicy);
    }

    [Fact]
    public async Task Docs_scope_still_respects_protected_files()
    {
        // Even inside the scope, the protected-file rules stay on top:
        // a secret under docs/ is still untouchable.
        var result = await Files(DocsOnly).ReplaceAsync(new ReplaceFileToolCall
        {
            RelativePath = "docs/.env",
            NewContent = "SECRET=1",
        });
        Assert.True(result.DeniedByPolicy);
    }

    [Theory]
    [InlineData("docs/ARCHITECTURE.md")]
    [InlineData("src/Program.cs")]
    [InlineData("README.md")]
    public async Task ReadOnly_scope_denies_every_write(string path)
    {
        var replace = await Files(WriteScopePolicy.ReadOnly).ReplaceAsync(new ReplaceFileToolCall { RelativePath = path, NewContent = "x" });
        Assert.True(replace.DeniedByPolicy);
        Assert.Contains("read-only", replace.Error, StringComparison.OrdinalIgnoreCase);

        var patch = await Patches(WriteScopePolicy.ReadOnly).ApplyAsync(new ApplyPatchToolCall
        {
            RelativePath = path,
            UnifiedDiff = "@@ -1,1 +1,1 @@\n-a\n+b\n",
        });
        Assert.True(patch.DeniedByPolicy);
    }

    [Fact]
    public async Task ReadOnly_scope_still_allows_reading()
    {
        WriteFile("src/Program.cs", "code");
        var result = await Files(WriteScopePolicy.ReadOnly).ReadAsync(new ReadFileToolCall { RelativePath = "src/Program.cs" });
        Assert.True(result.Success);
        Assert.Equal("code", result.Output);
    }

    [Fact]
    public async Task AllowAll_default_keeps_existing_agents_unaffected()
    {
        var result = await Files(WriteScopePolicy.AllowAll).ReplaceAsync(new ReplaceFileToolCall
        {
            RelativePath = "src/Program.cs",
            NewContent = "new",
        });
        Assert.True(result.Success);
    }

    [Fact]
    public void Prefix_matching_is_case_insensitive_and_slash_normalized()
    {
        var scope = WriteScopePolicy.FromPrefixes(new[] { "Docs/", "README.md" });

        Assert.True(scope.ValidateWrite("docs/guide.md").IsValid);
        Assert.True(scope.ValidateWrite("DOCS\\guide.md").IsValid);
        Assert.True(scope.ValidateWrite("readme.md").IsValid);
        Assert.False(scope.ValidateWrite("src/readme-generator.cs").IsValid);
    }
}
