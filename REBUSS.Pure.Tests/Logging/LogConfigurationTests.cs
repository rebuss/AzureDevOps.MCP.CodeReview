using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.Tests.Logging;

public class LogConfigurationTests
{
    private static ILoggerFactory BuildLoggerFactoryFromConfig(
        Dictionary<string, string?>? overrides = null)
    {
        var configBuilder = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                optional: false);

        if (overrides is not null)
            configBuilder.AddInMemoryCollection(overrides);

        var configuration = configBuilder.Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddConsole(); // Need at least one provider for IsEnabled to work
        });

        return services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
    }

    [Theory]
    [InlineData("Microsoft.Extensions.Http")]
    [InlineData("Polly")]
    public void DefaultConfig_FrameworkCategories_SuppressBelowWarning(string categoryName)
    {
        using var factory = BuildLoggerFactoryFromConfig();
        var logger = factory.CreateLogger(categoryName);

        // Framework categories configured at Warning level must not emit Debug or Information
        Assert.False(logger.IsEnabled(LogLevel.Debug),
            $"{categoryName} should not emit Debug at default config");
        Assert.False(logger.IsEnabled(LogLevel.Information),
            $"{categoryName} should not emit Information at default config");
    }

    [Fact]
    public void DefaultConfig_ApplicationCategories_DoNotEmitDebug()
    {
        using var factory = BuildLoggerFactoryFromConfig();
        var logger = factory.CreateLogger("REBUSS.Pure.Services.SomeService");

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

        using var factory = BuildLoggerFactoryFromConfig(overrides);

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
