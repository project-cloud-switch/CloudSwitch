using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSwitch.Api.Infrastructure;
using CloudSwitch.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class ModuleCatalogTests
{
    private const string Module = """{"id":"stateless-v1","provider":"local","source":"modules/local/stateless","revision":"v1","allowedInputs":[]}""";

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("""[{"id":"stateless-v1","provider":"local","source":"modules/local/stateless","revision":"v1","allowedInputs":null}]""")]
    public void Null_catalog_values_are_rejected(string modules)
    {
        var json = """{"modules":""" + modules + "}";
        var options = Assert.IsType<ModuleCatalogOptions>(JsonSerializer.Deserialize<ModuleCatalogOptions>(json, JsonSerializerOptions.Web));
        using var services = ServicesFor("[]");
        var validator = Assert.Single(services.GetServices<IValidateOptions<ModuleCatalogOptions>>());
        Assert.True(validator.Validate(Options.DefaultName, options).Failed);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("provider")]
    [InlineData("source")]
    [InlineData("revision")]
    public void Required_module_fields_reject_null_whitespace_and_missing_values(string field)
    {
        var module = Assert.IsType<JsonObject>(JsonNode.Parse(Module));
        foreach (var value in new[] { null, " " })
        {
            module[field] = value;
            using var services = ServicesFor($"[{module.ToJsonString()}]");
            Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<ModuleCatalogOptions>>().Value);
        }
        module.Remove(field);
        using var missing = ServicesFor($"[{module.ToJsonString()}]");
        Assert.Throws<OptionsValidationException>(() => missing.GetRequiredService<IOptions<ModuleCatalogOptions>>().Value);
    }

    [Fact]
    public void Duplicate_module_and_provider_pairs_are_rejected()
    {
        using var services = ServicesFor($"[{Module},{Module}]");
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<ModuleCatalogOptions>>().Value);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[" + Module + "]")]
    [InlineData("""[{"id":"stateless-v1","provider":"local","source":"modules/local/stateless","revision":"v1","allowedInputs":null}]""")]
    public void Empty_catalogs_and_empty_input_allowlists_are_valid(string modules)
    {
        using var services = ServicesFor(modules);
        var catalog = services.GetRequiredService<ApprovedModules>();
        Assert.All(catalog.All, module => Assert.Empty(module.AllowedInputs));
    }

    private static ServiceProvider ServicesFor(string modules)
    {
        var json = """{"Terraform":{"Modules":""" + modules + "}}";
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        return new ServiceCollection().AddLogging().AddApiInfrastructure(configuration).BuildServiceProvider();
    }
}
