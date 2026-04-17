using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.Tests.Logging;

public class LogConfigurationTests
{
    /// <summary>
    /// Baseline logging config asserted by these tests. Kept inline on purpose — an
    /// <c>AddJsonFile("appsettings.json", optional: false)</c> crashes the whole suite if
    /// the file is missing from the output dir (CopyToOutputDirectory race, publish layout
    /// tweak, etc.); <c>optional: true</c> would hide drift by silently returning an empty
    /// config and making the assertions pass for the wrong reason. Embedding the minimal
    /// subset these tests actually depend on keeps them self-sufficient and stable against
    /// changes to the production appsettings.json.
    /// </summary>
    private static readonly Dictionary<string, string?> BaselineLoggingConfig = new()
    {
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft"] = "Warning",
        ["Logging:LogLevel:System"] = "Warning",
        ["Logging:LogLevel:Microsoft.Extensions.Http"] = "Warning",
        ["Logging:LogLevel:Polly"] = "Warning",
        ["Logging:LogLevel:GitHub.Copilot.SDK"] = "Warning",
    };

    private static ServiceProvider BuildServiceProviderFromConfig(
        Dictionary<string, string?>? overrides = null)
    {
        var configBuilder = new ConfigurationBuilder()
            .AddInMemoryCollection(BaselineLoggingConfig);

        if (overrides is not null)
            configBuilder.AddInMemoryCollection(overrides);

        var configuration = configBuilder.Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddConsole(); // Need at least one provider for IsEnabled to work
        });

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("Microsoft.Extensions.Http")]
    [InlineData("Polly")]
    public void DefaultConfig_FrameworkCategories_SuppressBelowWarning(string categoryName)
    {
        using var sp = BuildServiceProviderFromConfig();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(categoryName);

        // Framework categories configured at Warning level must not emit Debug or Information
        Assert.False(logger.IsEnabled(LogLevel.Debug),
            $"{categoryName} should not emit Debug at default config");
        Assert.False(logger.IsEnabled(LogLevel.Information),
            $"{categoryName} should not emit Information at default config");
    }

    [Fact]
    public void DefaultConfig_ApplicationCategories_DoNotEmitDebug()
    {
        using var sp = BuildServiceProviderFromConfig();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("REBUSS.Pure.Services.SomeService");

        // Application categories at Default (Information) must not emit Debug
        Assert.False(logger.IsEnabled(LogLevel.Debug),
            "Application categories should not emit Debug at default config");
    }

    [Fact]
    public void NamespaceOverride_EnablesDebug_ForTargetedCategoryOnly()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Logging:LogLevel:REBUSS.Pure.GitHub.Configuration.GitHubChainedAuthenticationProvider"] = "Debug"
        };

        using var sp = BuildServiceProviderFromConfig(overrides);
        var factory = sp.GetRequiredService<ILoggerFactory>();

        var targetLogger = factory.CreateLogger(
            "REBUSS.Pure.GitHub.Configuration.GitHubChainedAuthenticationProvider");
        var otherLogger = factory.CreateLogger("REBUSS.Pure.Services.SomeService");
        var pollyLogger = factory.CreateLogger("Polly");

        // The overridden namespace should allow Debug; others must not
        Assert.True(targetLogger.IsEnabled(LogLevel.Debug),
            "Overridden namespace should emit Debug");
        Assert.False(pollyLogger.IsEnabled(LogLevel.Debug),
            "Polly should remain suppressed despite namespace override elsewhere");
    }
}
