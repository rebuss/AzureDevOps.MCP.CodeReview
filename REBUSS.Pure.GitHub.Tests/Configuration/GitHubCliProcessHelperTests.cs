using System.Runtime.InteropServices;
using REBUSS.Pure.GitHub.Configuration;

namespace REBUSS.Pure.GitHub.Tests.Configuration;

public class GitHubCliProcessHelperTests
{
    [Fact]
    public void GetProcessStartArgs_ReturnsGhArguments()
    {
        var (fileName, arguments) = GitHubCliProcessHelper.GetProcessStartArgs("auth token");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal("cmd.exe", fileName);
            Assert.Equal("/c gh auth token", arguments);
        }
        else
        {
            Assert.Equal("gh", fileName);
            Assert.Equal("auth token", arguments);
        }
    }

    [Fact]
    public void GetProcessStartArgs_AuthLogin_ReturnsCorrectFormat()
    {
        var (fileName, arguments) = GitHubCliProcessHelper.GetProcessStartArgs("auth login --web");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal("cmd.exe", fileName);
            Assert.Equal("/c gh auth login --web", arguments);
        }
        else
        {
            Assert.Equal("gh", fileName);
            Assert.Equal("auth login --web", arguments);
        }
    }

    [Fact]
    public void GetProcessStartArgs_WithCustomPath_UsesFullPath()
    {
        var customPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? @"C:\Program Files\GitHub CLI\gh.exe"
            : "/usr/bin/gh";

        var (fileName, arguments) = GitHubCliProcessHelper.GetProcessStartArgs("--version", customPath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal("cmd.exe", fileName);
            Assert.Equal($"/c \"{customPath}\" --version", arguments);
        }
        else
        {
            Assert.Equal(customPath, fileName);
            Assert.Equal("--version", arguments);
        }
    }

    [Fact]
    public void TryFindGhCliOnWindows_ReturnsNull_OnNonWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // skip on Windows — result depends on whether gh is installed

        var result = GitHubCliProcessHelper.TryFindGhCliOnWindows();

        Assert.Null(result);
    }
}
