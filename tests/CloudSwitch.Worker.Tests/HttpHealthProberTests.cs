using System.Net;
using System.Text;
using CloudSwitch.Contracts;
using CloudSwitch.Worker.Orchestration;
using CloudSwitch.Worker.Probing;
using Xunit;

namespace CloudSwitch.Worker.Tests;

public sealed class HttpHealthProberTests : IDisposable
{
    private readonly HttpListener _listener = null!;
    private readonly string _baseAddress;
    private readonly int _port;

    public HttpHealthProberTests()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            _port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            try
            {
                _listener.Start();
                break;
            }
            catch
            {
                try { _listener.Close(); } catch { }
                if (attempt == 9) throw;
            }
        }
        _baseAddress = $"http://127.0.0.1:{_port}/";
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

    [Fact]
    public async Task Healthy_endpoint_returns_success_and_measures_latency()
    {
        _ = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            var buffer = Encoding.UTF8.GetBytes("{\"status\":\"healthy\"}");
            await ctx.Response.OutputStream.WriteAsync(buffer);
            ctx.Response.Close();
        });

        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowedHosts = ["127.0.0.1"]
        });

        var result = await prober.ProbeAsync(new Uri(_baseAddress + "healthz"), TimeSpan.FromSeconds(3));

        Assert.True(result.IsHealthy);
        Assert.Equal(ProbeStatusCategory.Healthy, result.Category);
        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Latency > TimeSpan.Zero);
        Assert.Contains("healthy", result.ResponseSnippet);
    }

    [Fact]
    public async Task Http_failure_status_code_is_captured()
    {
        _ = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 503;
            var buffer = Encoding.UTF8.GetBytes("service unavailable");
            await ctx.Response.OutputStream.WriteAsync(buffer);
            ctx.Response.Close();
        });

        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowedHosts = ["127.0.0.1"]
        });

        var result = await prober.ProbeAsync(new Uri(_baseAddress + "healthz"), TimeSpan.FromSeconds(3));

        Assert.False(result.IsHealthy);
        Assert.Equal(ProbeStatusCategory.HttpFailure, result.Category);
        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public async Task Response_body_is_bounded_to_configured_bytes()
    {
        _ = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            // Send 10KB of data
            var largeBuffer = Encoding.UTF8.GetBytes(new string('X', 10240));
            await ctx.Response.OutputStream.WriteAsync(largeBuffer);
            ctx.Response.Close();
        });

        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowedHosts = ["127.0.0.1"],
            MaxResponseBodyBytes = 1024
        });

        var result = await prober.ProbeAsync(new Uri(_baseAddress + "data"), TimeSpan.FromSeconds(3));

        Assert.True(result.IsHealthy);
        Assert.NotNull(result.ResponseSnippet);
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(result.ResponseSnippet));
    }

    [Fact]
    public async Task Slow_endpoint_triggers_timeout()
    {
        _ = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            await Task.Delay(2000);
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowedHosts = ["127.0.0.1"]
        });

        var result = await prober.ProbeAsync(new Uri(_baseAddress + "slow"), TimeSpan.FromMilliseconds(200));

        Assert.False(result.IsHealthy);
        Assert.Equal(ProbeStatusCategory.Timeout, result.Category);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_stops_probe_immediately()
    {
        _ = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            await Task.Delay(2000);
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowedHosts = ["127.0.0.1"]
        });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var result = await prober.ProbeAsync(new Uri(_baseAddress + "cancel"), TimeSpan.FromSeconds(5), cts.Token);

        Assert.False(result.IsHealthy);
        Assert.Equal(ProbeStatusCategory.Cancelled, result.Category);
    }

    [Fact]
    public async Task Cloud_metadata_ip_is_blocked_by_ssrf_policy()
    {
        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowPrivateRanges = true // Even if private ranges are enabled, 169.254 metadata is ALWAYS blocked!
        });

        var result = await prober.ProbeAsync(new Uri("http://169.254.169.254/latest/meta-data"), TimeSpan.FromSeconds(2));

        Assert.False(result.IsHealthy);
        Assert.Equal(ProbeStatusCategory.SecurityPolicyViolation, result.Category);
        Assert.Contains("SSRF protection", result.ErrorMessage);
    }

    [Fact]
    public async Task Unapproved_private_range_is_blocked_when_disallowed()
    {
        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowPrivateRanges = false,
            AllowedHosts = [] // Empty allowed hosts
        });

        var result = await prober.ProbeAsync(new Uri("http://10.240.0.5:8080/health"), TimeSpan.FromSeconds(2));

        Assert.False(result.IsHealthy);
        Assert.Equal(ProbeStatusCategory.SecurityPolicyViolation, result.Category);
    }

    [Fact]
    public async Task Redirects_are_followed_up_to_limit()
    {
        _ = Task.Run(async () =>
        {
            var ctx1 = await _listener.GetContextAsync();
            ctx1.Response.StatusCode = 302;
            ctx1.Response.Headers["Location"] = _baseAddress + "target";
            ctx1.Response.Close();

            var ctx2 = await _listener.GetContextAsync();
            ctx2.Response.StatusCode = 200;
            var buffer = Encoding.UTF8.GetBytes("final destination");
            await ctx2.Response.OutputStream.WriteAsync(buffer);
            ctx2.Response.Close();
        });

        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowedHosts = ["127.0.0.1"],
            MaxRedirects = 2
        });

        var result = await prober.ProbeAsync(new Uri(_baseAddress + "start"), TimeSpan.FromSeconds(3));

        Assert.True(result.IsHealthy);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("final destination", result.ResponseSnippet);
    }

    [Fact]
    public async Task Redirect_to_cloud_metadata_is_blocked_by_policy()
    {
        _ = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers["Location"] = "http://169.254.169.254/meta";
            ctx.Response.Close();
        });

        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowedHosts = ["127.0.0.1"]
        });

        var result = await prober.ProbeAsync(new Uri(_baseAddress + "redirect-to-metadata"), TimeSpan.FromSeconds(3));

        Assert.False(result.IsHealthy);
        Assert.Equal(ProbeStatusCategory.SecurityPolicyViolation, result.Category);
    }

    [Fact]
    public async Task Application_revision_identity_is_verified()
    {
        _ = Task.Run(async () =>
        {
            var ctx1 = await _listener.GetContextAsync();
            ctx1.Response.StatusCode = 200;
            ctx1.Response.Headers["X-CloudSwitch-Revision"] = "v2.1.0";
            ctx1.Response.Close();

            var ctx2 = await _listener.GetContextAsync();
            ctx2.Response.StatusCode = 200;
            ctx2.Response.Headers["X-CloudSwitch-Revision"] = "v1.0.0";
            ctx2.Response.Close();
        });

        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowedHosts = ["127.0.0.1"]
        });

        // Test matching revision
        var resultMatching = await prober.ProbeAsync(
            new Uri(_baseAddress + "test1"), TimeSpan.FromSeconds(3), expectedRevision: "v2.1.0");
        Assert.True(resultMatching.IsHealthy);
        Assert.Equal("v2.1.0", resultMatching.ObservedRevision);

        // Test mismatched revision
        var resultMismatch = await prober.ProbeAsync(
            new Uri(_baseAddress + "test2"), TimeSpan.FromSeconds(3), expectedRevision: "v2.1.0");
        Assert.False(resultMismatch.IsHealthy);
        Assert.Equal(ProbeStatusCategory.HttpFailure, resultMismatch.Category);
        Assert.Contains("mismatch", resultMismatch.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Target_health_validator_honors_consecutive_thresholds()
    {
        var probeCount = 0;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    probeCount++;
                    // First probe fails, subsequent probes succeed
                    ctx.Response.StatusCode = probeCount == 1 ? 503 : 200;
                    ctx.Response.Close();
                }
                catch
                {
                    break;
                }
            }
        });

        using var prober = new HttpHealthProber(new HttpHealthProberOptions
        {
            AllowedHosts = ["127.0.0.1"]
        });

        var validator = new HttpTargetHealthValidator(prober);
        var policy = new HealthCheckPolicy
        {
            Path = "/healthz",
            IntervalSeconds = 1,
            TimeoutSeconds = 2,
            FailureThreshold = 3,
            RecoveryThreshold = 2
        };

        var validationResult = await validator.ValidateAsync(_baseAddress, policy);

        Assert.True(validationResult.IsHealthy);
        Assert.Equal(2, validationResult.SuccessfulProbes);
    }
}
