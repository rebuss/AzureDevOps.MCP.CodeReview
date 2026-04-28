using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Cli.AgentSetup;

/// <summary>
/// Wires up the GitHub Copilot CLI: spins up a narrow throwaway DI container that
/// exposes only <see cref="ICopilotVerificationProbe"/>, then runs
/// <see cref="CopilotCliSetupStep"/>. Any construction failure or run-time exception is
/// soft-exited so the init exit code is unaffected.
/// </summary>
internal sealed class CopilotAgentSetupStep : IAgentSetupStep
{
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;
    private readonly string? _ghCliPathOverride;

    public CopilotAgentSetupStep(
        TextWriter output,
        TextReader input,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner,
        string? ghCliPathOverride)
    {
        _output = output;
        _input = input;
        _processRunner = processRunner;
        _ghCliPathOverride = ghCliPathOverride;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ServiceProvider? copilotProbeServices = null;
        ICopilotVerificationProbe? verificationProbe = null;
        try
        {
            copilotProbeServices = BuildCopilotProbeServices();
            verificationProbe = copilotProbeServices.GetRequiredService<ICopilotVerificationProbe>();
        }
        catch (Exception ex)
        {
            await _output.WriteLineAsync(
                $"Warning: could not construct Copilot verification probe ({ex.Message}). Skipping verification step.");
        }

        try
        {
            var copilotStep = new CopilotCliSetupStep(
                _output, _input, _processRunner, _ghCliPathOverride,
                verificationProbe: verificationProbe);
            await copilotStep.RunAsync(cancellationToken);
        }
        catch
        {
            // Defense in depth — CopilotCliSetupStep is already catch-all internally.
        }
        finally
        {
            copilotProbeServices?.Dispose();
        }
    }

    /// <summary>
    /// Feature 018 T032: builds a narrow, throwaway service provider that registers
    /// only the types needed by <see cref="ICopilotVerificationProbe"/> — this init
    /// flow runs outside of the MCP host DI graph. The provider is disposed as soon
    /// as <see cref="CopilotCliSetupStep.RunAsync"/> returns.
    /// </summary>
    private static ServiceProvider BuildCopilotProbeServices()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.Configure<CopilotReviewOptions>(
            configuration.GetSection(CopilotReviewOptions.SectionName));
        services.AddSingleton<ICopilotTokenResolver, CopilotTokenResolver>();
        services.AddSingleton<CopilotVerificationRunner>();
        services.AddSingleton<ICopilotVerificationProbe>(
            sp => sp.GetRequiredService<CopilotVerificationRunner>());

        return services.BuildServiceProvider();
    }
}
