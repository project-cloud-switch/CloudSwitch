using CloudSwitch.Persistence.Domain;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class VaultReferenceTests
{
    [Theory]
    [InlineData("vault://demo#api-key")]
    [InlineData("vault://users/7f1c0000/app.v2#token")]
    public void Valid_references_have_one_canonical_form(string value)
    {
        Assert.True(VaultReference.TryParse(value, out var reference));
        Assert.Equal(value, reference!.Canonical);
    }

    [Theory]
    [InlineData("vault://demo/../other#key")]
    [InlineData("vault://demo/./x#key")]
    [InlineData("vault://demo//x#key")]
    [InlineData("vault://demo/x/#key")]
    [InlineData("vault:///demo#key")]
    [InlineData("VAULT://demo#key")]
    [InlineData("vault://demo")]
    [InlineData("vault://#key")]
    [InlineData("vault://demo#")]
    [InlineData("vault://demo#a#b")]
    [InlineData("vault://demo#..")]
    [InlineData("vault://demo%2F..%2Fx#key")]
    [InlineData("vault://demo\\x#key")]
    [InlineData("vault://demo/x#key\n")]
    [InlineData("vault://dеmo#key")] // кириллическая «е»
    public void Unsafe_or_non_canonical_references_are_rejected(string value)
        => Assert.False(VaultReference.TryParse(value, out _));

    [Theory]
    [InlineData("Example.TEST.", "example.test")]
    [InlineData("app.example.test", "app.example.test")]
    public void Dns_names_are_canonicalized(string input, string expected)
    {
        Assert.True(DnsNames.TryCanonicalize(input, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("example")]
    [InlineData("*.example.test")]
    [InlineData("exa mple.test")]
    [InlineData("ex_ample.test")]
    [InlineData("-a.example.test")]
    [InlineData("пример.test")]
    [InlineData("example.test\n")]
    [InlineData("\u212Aexample.test")] // знак Кельвина
    public void Unsafe_dns_names_are_rejected(string input)
        => Assert.False(DnsNames.TryCanonicalize(input, out _));

    [Theory]
    [InlineData("example.test", "example.test", true)]
    [InlineData("app.example.test", "example.test", true)]
    [InlineData("evilexample.test", "example.test", false)]
    [InlineData("example.test", "app.example.test", false)]
    public void Records_must_lie_inside_the_zone(string record, string zone, bool inside)
        => Assert.Equal(inside, DnsNames.IsInZone(record, zone));
}