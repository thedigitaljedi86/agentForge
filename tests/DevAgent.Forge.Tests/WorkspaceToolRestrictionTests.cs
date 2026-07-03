namespace DevAgent.Forge.Tests;

using DevAgent.Forge;
using DevAgent.Forge.Tools;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;
using Xunit;

public class WorkspaceToolRestrictionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devagent-forge-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspacePathValidator _paths;
    private readonly ProtectedFilePolicy _protected = new();

    public WorkspaceToolRestrictionTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new WorkspacePathValidator(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private WorkspaceFileTool Files(bool allowDeploy = false) => new(_paths, _protected, allowDeploy);
    private PatchApplicationService Patches(bool allowDeploy = false) => new(_paths, _protected, allowDeploy);

    private void WriteFile(string rel, string content)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../escape.txt")]
    [InlineData("/etc/shadow")]
    public async Task Read_rejects_path_traversal(string path)
    {
        var result = await Files().ReadAsync(new ReadFileToolCall { RelativePath = path });
        Assert.True(result.DeniedByPolicy);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/tmp/evil.txt")]
    public async Task Replace_rejects_path_traversal(string path)
    {
        var result = await Files().ReplaceAsync(new ReplaceFileToolCall { RelativePath = path, NewContent = "x" });
        Assert.True(result.DeniedByPolicy);
    }

    [Fact]
    public async Task Patch_rejects_path_traversal()
    {
        var result = await Patches().ApplyAsync(new ApplyPatchToolCall
        {
            RelativePath = "../outside.cs",
            UnifiedDiff = "@@ -1,1 +1,1 @@\n-a\n+b\n",
        });
        Assert.True(result.DeniedByPolicy);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("config/secrets.json")]
    [InlineData("appsettings.Production.json")]
    public async Task Secret_files_cannot_be_read(string path)
    {
        WriteFile(path, "SECRET=1");
        var result = await Files().ReadAsync(new ReadFileToolCall { RelativePath = path });
        Assert.True(result.DeniedByPolicy);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("config/secrets.json")]
    public async Task Secret_files_cannot_be_edited(string path)
    {
        var replace = await Files().ReplaceAsync(new ReplaceFileToolCall { RelativePath = path, NewContent = "x" });
        Assert.True(replace.DeniedByPolicy);

        var patch = await Patches().ApplyAsync(new ApplyPatchToolCall
        {
            RelativePath = path,
            UnifiedDiff = "@@ -1,1 +1,1 @@\n-a\n+b\n",
        });
        Assert.True(patch.DeniedByPolicy);
    }

    [Fact]
    public async Task Deployment_files_are_denied_by_default_but_allowed_by_policy()
    {
        WriteFile("deploy/Dockerfile", "FROM x\n");

        var denied = await Files(allowDeploy: false)
            .ReplaceAsync(new ReplaceFileToolCall { RelativePath = "deploy/Dockerfile", NewContent = "FROM y\n" });
        Assert.True(denied.DeniedByPolicy);

        var allowed = await Files(allowDeploy: true)
            .ReplaceAsync(new ReplaceFileToolCall { RelativePath = "deploy/Dockerfile", NewContent = "FROM y\n" });
        Assert.False(allowed.DeniedByPolicy);
        Assert.True(allowed.Success);
    }

    [Fact]
    public async Task Replace_writes_file_and_reports_diff()
    {
        WriteFile("src/Program.cs", "old\n");

        var result = await Files().ReplaceAsync(new ReplaceFileToolCall
        {
            RelativePath = "src/Program.cs",
            NewContent = "new\n",
        });

        Assert.True(result.Success);
        Assert.Equal("src/Program.cs", result.ChangedFile);
        Assert.False(string.IsNullOrEmpty(result.Diff));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(_root, "src/Program.cs")));
    }

    [Fact]
    public async Task Read_returns_file_content()
    {
        WriteFile("a.txt", "hello world");
        var result = await Files().ReadAsync(new ReadFileToolCall { RelativePath = "a.txt" });

        Assert.True(result.Success);
        Assert.Equal("hello world", result.Output);
    }

    [Fact]
    public async Task List_returns_entries_inside_workspace()
    {
        WriteFile("src/A.cs", "");
        WriteFile("src/B.cs", "");

        var result = await Files().ListAsync(new ListFilesToolCall { RelativePath = "src" });

        Assert.True(result.Success);
        Assert.Contains("src/A.cs", result.Output);
        Assert.Contains("src/B.cs", result.Output);
    }
}
