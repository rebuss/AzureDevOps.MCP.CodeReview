using System.Runtime.InteropServices;
using REBUSS.Pure.AzureDevOps.Configuration;

namespace REBUSS.Pure.Tests.AzureDevOpsIntegration;

public class AzureCliProcessHelperTests
{
    [Fact]
    public void GetProcessStartArgs_ReturnsAzArguments()
    {
        var (fileName, arguments) = AzureCliProcessHelper.GetProcessStartArgs("account get-access-token --output json");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal("cmd.exe", fileName);
            Assert.Equal("/c az account get-access-token --output json", arguments);
        }
        else
        {
            Assert.Equal("az", fileName);
            Assert.Equal("account get-access-token --output json", arguments);
        }
    }

    [Fact]
    public void GetProcessStartArgs_Login_ReturnsCorrectFormat()
    {
        var (fileName, arguments) = AzureCliProcessHelper.GetProcessStartArgs("login");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal("cmd.exe", fileName);
            Assert.Equal("/c az login", arguments);
        }
        else
        {
            Assert.Equal("az", fileName);
            Assert.Equal("login", arguments);
        }
    }

    [Fact]
    public void GetProcessStartArgs_WithCustomPath_UsesFullPath()
    {
        var customPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
            : "/usr/bin/az";

        var (fileName, arguments) = AzureCliProcessHelper.GetProcessStartArgs("--version", customPath);

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
    public void TryFindAzCliOnWindows_ReturnsNull_OnNonWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // skip on Windows — result depends on whether az is installed

        var result = AzureCliProcessHelper.TryFindAzCliOnWindows();

        Assert.Null(result);
    }
}
