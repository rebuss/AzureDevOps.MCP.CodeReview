using REBUSS.Pure.Cli;

namespace REBUSS.Pure.Tests.Cli;

public class CliArgumentParserTests
{
    [Fact]
    public void Parse_NoArgs_ReturnsServerModeWithNullOptions()
    {
        var result = CliArgumentParser.Parse([]);

        Assert.True(result.IsServerMode);
        Assert.Null(result.CommandName);
        Assert.Null(result.RepoPath);
        Assert.Null(result.Pat);
        Assert.Null(result.Organization);
        Assert.Null(result.Project);
        Assert.Null(result.Repository);
    }

    [Fact]
    public void Parse_RepoArg_ReturnsServerModeWithRepoPath()
    {
        var result = CliArgumentParser.Parse(["--repo", @"C:\Projects\MyApp"]);

        Assert.True(result.IsServerMode);
        Assert.Null(result.CommandName);
        Assert.Equal(@"C:\Projects\MyApp", result.RepoPath);
    }

    [Fact]
    public void Parse_RepoArgCaseInsensitive_ReturnsRepoPath()
    {
        var result = CliArgumentParser.Parse(["--REPO", "/home/user/repo"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("/home/user/repo", result.RepoPath);
    }

    [Fact]
    public void Parse_InitCommand_ReturnsCliModeWithNullOptions()
    {
        var result = CliArgumentParser.Parse(["init"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("init", result.CommandName);
        Assert.Null(result.RepoPath);
        Assert.Null(result.Pat);
        Assert.Null(result.Organization);
        Assert.Null(result.Project);
        Assert.Null(result.Repository);
    }

    [Fact]
    public void Parse_InitCommandCaseInsensitive_ReturnsCliMode()
    {
        var result = CliArgumentParser.Parse(["INIT"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("init", result.CommandName);
    }

    [Fact]
    public void Parse_RepoArgWithoutValue_ReturnsNullRepoPath()
    {
        var result = CliArgumentParser.Parse(["--repo"]);

        Assert.True(result.IsServerMode);
        Assert.Null(result.RepoPath);
    }

    [Fact]
    public void Parse_UnknownArg_ReturnsServerModeWithNoRepoPath()
    {
        var result = CliArgumentParser.Parse(["--unknown"]);

        Assert.True(result.IsServerMode);
        Assert.Null(result.RepoPath);
    }

    [Fact]
    public void Parse_RepoArgAmongOtherArgs_ExtractsRepoPath()
    {
        var result = CliArgumentParser.Parse(["--verbose", "--repo", "/path/to/repo", "--debug"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("/path/to/repo", result.RepoPath);
    }

    // --- PAT argument ---

    [Fact]
    public void Parse_PatArg_ReturnsServerModeWithPat()
    {
        var result = CliArgumentParser.Parse(["--pat", "my-secret-token"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("my-secret-token", result.Pat);
    }

    [Fact]
    public void Parse_PatArgCaseInsensitive_ReturnsPat()
    {
        var result = CliArgumentParser.Parse(["--PAT", "token123"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("token123", result.Pat);
    }

    [Fact]
    public void Parse_PatArgWithoutValue_ReturnsNullPat()
    {
        var result = CliArgumentParser.Parse(["--pat"]);

        Assert.True(result.IsServerMode);
        Assert.Null(result.Pat);
    }

    // --- Organization argument ---

    [Fact]
    public void Parse_OrgArg_ReturnsServerModeWithOrganization()
    {
        var result = CliArgumentParser.Parse(["--org", "my-org"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("my-org", result.Organization);
    }

    [Fact]
    public void Parse_OrgArgCaseInsensitive_ReturnsOrganization()
    {
        var result = CliArgumentParser.Parse(["--ORG", "MyOrg"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("MyOrg", result.Organization);
    }

    [Fact]
    public void Parse_OrgArgWithoutValue_ReturnsNullOrganization()
    {
        var result = CliArgumentParser.Parse(["--org"]);

        Assert.True(result.IsServerMode);
        Assert.Null(result.Organization);
    }

    // --- Project argument ---

    [Fact]
    public void Parse_ProjectArg_ReturnsServerModeWithProject()
    {
        var result = CliArgumentParser.Parse(["--project", "my-project"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("my-project", result.Project);
    }

    [Fact]
    public void Parse_ProjectArgCaseInsensitive_ReturnsProject()
    {
        var result = CliArgumentParser.Parse(["--PROJECT", "MyProject"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("MyProject", result.Project);
    }

    [Fact]
    public void Parse_ProjectArgWithoutValue_ReturnsNullProject()
    {
        var result = CliArgumentParser.Parse(["--project"]);

        Assert.True(result.IsServerMode);
        Assert.Null(result.Project);
    }

    // --- Repository argument ---

    [Fact]
    public void Parse_RepositoryArg_ReturnsServerModeWithRepository()
    {
        var result = CliArgumentParser.Parse(["--repository", "my-repo"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("my-repo", result.Repository);
    }

    [Fact]
    public void Parse_RepositoryArgCaseInsensitive_ReturnsRepository()
    {
        var result = CliArgumentParser.Parse(["--REPOSITORY", "MyRepo"]);

        Assert.True(result.IsServerMode);
        Assert.Equal("MyRepo", result.Repository);
    }

    [Fact]
    public void Parse_RepositoryArgWithoutValue_ReturnsNullRepository()
    {
        var result = CliArgumentParser.Parse(["--repository"]);

        Assert.True(result.IsServerMode);
        Assert.Null(result.Repository);
    }

    // --- Combined arguments ---

    [Fact]
    public void Parse_AllArgs_ExtractsAllValues()
    {
        var result = CliArgumentParser.Parse([
            "--repo", "/path/to/repo",
            "--pat", "my-token",
            "--org", "my-org",
            "--project", "my-project",
            "--repository", "my-repo"
        ]);

        Assert.True(result.IsServerMode);
        Assert.Equal("/path/to/repo", result.RepoPath);
        Assert.Equal("my-token", result.Pat);
        Assert.Equal("my-org", result.Organization);
        Assert.Equal("my-project", result.Project);
        Assert.Equal("my-repo", result.Repository);
    }

    // --- Global flag for init ---

    [Fact]
    public void Parse_InitWithGlobalShortFlag_ReturnsIsGlobalTrue()
    {
        var result = CliArgumentParser.Parse(["init", "-g"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("init", result.CommandName);
        Assert.True(result.IsGlobal);
    }

    [Fact]
    public void Parse_InitWithGlobalLongFlag_ReturnsIsGlobalTrue()
    {
        var result = CliArgumentParser.Parse(["init", "--global"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("init", result.CommandName);
        Assert.True(result.IsGlobal);
    }

    [Fact]
    public void Parse_InitWithGlobalFlagCaseInsensitive_ReturnsIsGlobalTrue()
    {
        var result = CliArgumentParser.Parse(["init", "--GLOBAL"]);

        Assert.False(result.IsServerMode);
        Assert.True(result.IsGlobal);
    }

    [Fact]
    public void Parse_InitWithGlobalAndPat_ReturnsBothValues()
    {
        var result = CliArgumentParser.Parse(["init", "-g", "--pat", "my-token"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("init", result.CommandName);
        Assert.True(result.IsGlobal);
        Assert.Equal("my-token", result.Pat);
    }

    [Fact]
    public void Parse_InitWithoutGlobal_ReturnsIsGlobalFalse()
    {
        var result = CliArgumentParser.Parse(["init"]);

        Assert.False(result.IsGlobal);
    }

    // --- IDE argument for init ---

    [Fact]
    public void Parse_InitWithIdeVscode_ReturnsIdeVscode()
    {
        var result = CliArgumentParser.Parse(["init", "--ide", "vscode"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("init", result.CommandName);
        Assert.Equal("vscode", result.Ide);
    }

    [Fact]
    public void Parse_InitWithIdeVs_ReturnsIdeVs()
    {
        var result = CliArgumentParser.Parse(["init", "--ide", "vs"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("init", result.CommandName);
        Assert.Equal("vs", result.Ide);
    }

    [Fact]
    public void Parse_InitWithIdeClaude_ReturnsIdeClaude()
    {
        var result = CliArgumentParser.Parse(["init", "--ide", "claude"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("init", result.CommandName);
        Assert.Equal("claude", result.Ide);
    }

    [Fact]
    public void Parse_InitWithIdeCaseInsensitive_ReturnsIde()
    {
        var result = CliArgumentParser.Parse(["init", "--IDE", "vscode"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("vscode", result.Ide);
    }

    [Fact]
    public void Parse_InitWithoutIde_ReturnsNullIde()
    {
        var result = CliArgumentParser.Parse(["init"]);

        Assert.Null(result.Ide);
    }

    [Fact]
    public void Parse_InitWithIdeWithoutValue_ReturnsNullIde()
    {
        var result = CliArgumentParser.Parse(["init", "--ide"]);

        Assert.Null(result.Ide);
    }

    [Fact]
    public void Parse_InitWithIdeAndPatAndGlobal_ReturnsAllValues()
    {
        var result = CliArgumentParser.Parse(["init", "--ide", "vs", "--pat", "my-token", "-g"]);

        Assert.False(result.IsServerMode);
        Assert.Equal("init", result.CommandName);
        Assert.Equal("vs", result.Ide);
        Assert.Equal("my-token", result.Pat);
        Assert.True(result.IsGlobal);
    }

}
