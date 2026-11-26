using System.Diagnostics;
using System.Net;
using System.Text;
using CloudSwitch.Worker.Infrastructure;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public sealed class OutputSanitizerTests
{
    private const string Secret = "S3cr3t-Value-9f2c";

    [Theory]
    [InlineData("apply failed: password=" + Secret)]
    [InlineData("password: \"" + Secret + " with spaces\"")]
    [InlineData("{\"client_secret\":\"" + Secret + "\"}")]
    [InlineData("api_key = '" + Secret + "'")]
    [InlineData("Authorization: Bearer " + Secret)]
    [InlineData("curl -H 'Authorization: Token " + Secret + "'")]
    [InlineData("https://admin:" + Secret + "@db.internal/x?token=" + Secret)]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0." + Secret)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\n" + Secret + "\n-----END RSA PRIVATE KEY-----")]
    [InlineData("stored in vault://users/abc/app#" + Secret)]
    public void Common_secret_forms_are_removed(string input)
    {
        var output = OutputSanitizer.Sanitize(input);
        Assert.DoesNotContain(Secret, output);
    }

    [Fact]
    public void Basic_credentials_are_removed_together_with_their_base64_form()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + Secret));
        var output = OutputSanitizer.Sanitize("Authorization: Basic " + encoded);
        Assert.DoesNotContain(encoded, output);
        Assert.DoesNotContain(Secret, output);
    }

    [Fact]
    public void Explicit_secrets_are_removed_in_raw_url_encoded_and_base64_forms()
    {
        const string value = "p@ss word/+=9z";
        var text = string.Join(" | ", value, value.ToUpperInvariant(), Uri.EscapeDataString(value),
            WebUtility.UrlEncode(value), Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
        var output = OutputSanitizer.Sanitize(text, [value]);
        Assert.DoesNotContain(value, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Uri.EscapeDataString(value), output);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), output);
    }

    [Fact]
    public void Longer_explicit_secrets_win_over_shorter_ones_they_contain()
    {
        var output = OutputSanitizer.Sanitize("value=abcdef-extra", ["abc", "abcdef-extra"]);
        Assert.DoesNotContain("extra", output);
    }

    [Fact]
    public void Control_characters_cannot_forge_log_lines()
    {
        var output = OutputSanitizer.Sanitize("line1\nINFO fake entry\r\nline3\t\u0007end");
        Assert.DoesNotContain('\n', output);
        Assert.DoesNotContain('\r', output);
        Assert.DoesNotContain('\u0007', output);
    }

    [Fact]
    public void Output_is_truncated_after_redaction()
    {
        var output = OutputSanitizer.Sanitize(new string('x', 5000), maxLength: 100);
        Assert.EndsWith("... [TRUNCATED]", output);
        Assert.True(output.Length <= 100 + "... [TRUNCATED]".Length);
    }

    [Fact]
    public void Ordinary_terraform_output_is_unchanged()
    {
        const string text = "Terraform apply complete! Resources: 3 added, 0 changed, 0 destroyed.";
        Assert.Equal(text, OutputSanitizer.Sanitize(text));
        Assert.Equal(string.Empty, OutputSanitizer.Sanitize(null));
    }

    [Fact]
    public void Pathological_input_finishes_quickly_and_never_leaks()
    {
        var input = new string('a', 100_000) + "password=" + Secret;
        var timer = Stopwatch.StartNew();
        var output = OutputSanitizer.Sanitize(input);
        timer.Stop();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(Secret, output);
    }
}