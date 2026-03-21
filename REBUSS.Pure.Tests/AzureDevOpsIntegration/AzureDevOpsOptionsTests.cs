using REBUSS.Pure.AzureDevOps.Configuration;
using Microsoft.Extensions.Options;

namespace REBUSS.Pure.Tests.AzureDevOpsIntegration;

public class AzureDevOpsOptionsValidatorTests
{
    private readonly AzureDevOpsOptionsValidator _validator = new();

    private static AzureDevOpsOptions CreateValid() => new()
    {
        OrganizationName = "Org",
        ProjectName = "Proj",
        RepositoryName = "Repo",
        PersonalAccessToken = "pat"
    };

    [Fact]
    public void Validate_Succeeds_WhenAllFieldsProvided()
    {
        var result = _validator.Validate(null, CreateValid());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_WhenAllFieldsEmpty()
    {
        var result = _validator.Validate(null, new AzureDevOpsOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_WhenOnlyPatProvided()
    {
        var options = new AzureDevOpsOptions { PersonalAccessToken = "my-pat" };
        var result = _validator.Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(nameof(AzureDevOpsOptions.OrganizationName))]
    [InlineData(nameof(AzureDevOpsOptions.ProjectName))]
    [InlineData(nameof(AzureDevOpsOptions.RepositoryName))]
    public void Validate_Fails_WhenFieldContainsSpaces(string fieldName)
    {
        var options = CreateValid();
        typeof(AzureDevOpsOptions)
            .GetProperty(fieldName)!
            .SetValue(options, "invalid value");

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(fieldName, result.FailureMessage);
    }
}
