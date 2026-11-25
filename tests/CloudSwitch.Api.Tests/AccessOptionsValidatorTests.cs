using CloudSwitch.Persistence;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class AccessOptionsValidatorTests
{
    private static readonly Guid A = Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");
    private static readonly Guid B = Guid.Parse("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e");

    private static DnsZoneGrant Grant(string zone, params Guid[] users) => new() { Zone = zone, UserIds = [.. users] };
    private static bool Valid(params DnsZoneGrant[] grants)
        => new AccessOptionsValidator().Validate(null, new AccessOptions { DnsZones = [.. grants] }).Succeeded;

    [Fact] 
    public void Empty_and_single_grants_are_valid() { 
        Assert.True(Valid()); 
        Assert.True(Valid(Grant("example.test", A))); 
    }
    [Fact] 
    public void Parent_and_child_zone_for_the_same_user_are_valid() 
        => Assert.True(Valid(Grant("example.test", A), Grant("app.example.test", A)));
    [Fact] 
    public void Unrelated_zones_for_different_users_are_valid() 
        => Assert.True(Valid(Grant("one.test", A), Grant("two.test", B)));
    [Fact] 
    public void The_same_zone_for_different_users_is_rejected() 
        => Assert.False(Valid(Grant("example.test", A), Grant("EXAMPLE.test.", B)));
    [Fact] 
    public void Overlapping_zones_for_different_users_are_rejected() 
        => Assert.False(Valid(Grant("example.test", A), Grant("app.example.test", B)));
    [Fact] 
    public void A_zone_with_several_users_is_rejected() 
        => Assert.False(Valid(Grant("example.test", A, B)));
    [Fact] 
    public void A_zone_without_users_or_with_an_invalid_name_is_rejected()
    {
        Assert.False(Valid(Grant("example.test")));
        Assert.False(Valid(Grant("*.example.test", A)));
    }
}