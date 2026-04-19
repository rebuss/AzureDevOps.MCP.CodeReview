using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Services.PrEnrichment;

namespace REBUSS.Pure.Tests.Services.PrEnrichment;

public class WorkflowOptionsTests
{
    [Fact]
    public void Defaults_AreTwentyEightThousandMs()
    {
        var opts = new WorkflowOptions();
        Assert.Equal(28_000, opts.MetadataInternalTimeoutMs);
        Assert.Equal(28_000, opts.ContentInternalTimeoutMs);
    }

    [Fact]
    public void BindsFromEmptyConfig_KeepsDefaults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.Configure<WorkflowOptions>(config.GetSection(WorkflowOptions.SectionName));
        var opts = services.BuildServiceProvider().GetRequiredService<IOptions<WorkflowOptions>>().Value;

        Assert.Equal(28_000, opts.MetadataInternalTimeoutMs);
        Assert.Equal(28_000, opts.ContentInternalTimeoutMs);
    }

    [Fact]
    public void BindsFromConfig_OverridesDefaults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workflow:MetadataInternalTimeoutMs"] = "12345",
            ["Workflow:ContentInternalTimeoutMs"] = "54321",
        }).Build();
        var services = new ServiceCollection();
        services.Configure<WorkflowOptions>(config.GetSection(WorkflowOptions.SectionName));
        var opts = services.BuildServiceProvider().GetRequiredService<IOptions<WorkflowOptions>>().Value;

        Assert.Equal(12_345, opts.MetadataInternalTimeoutMs);
        Assert.Equal(54_321, opts.ContentInternalTimeoutMs);
    }
}
