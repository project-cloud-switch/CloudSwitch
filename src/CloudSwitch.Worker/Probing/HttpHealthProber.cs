using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CloudSwitch.Worker.Probing;

public sealed class HttpHealthProberOptions
{
    public bool AllowPrivateRanges { get; set; } = false;
    public HashSet<string> AllowedHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1" };
    public int MaxRedirects { get; set; } = 3;
    public int MaxResponseBodyBytes { get; set; } = 4096;
}

public sealed class HttpHealthProber : IHttpHealthProber, IDisposable
{
    private readonly HttpHealthProberOptions _options;
    private readonly HttpClient _httpClient;

    public HttpHealthProber(IOptions<HttpHealthProberOptions>? options = null)
        : this(options?.Value ?? new HttpHealthProberOptions())
    {
    }

    public HttpHealthProber(HttpHealthProberOptions options)
    {
        _options = options ?? new HttpHealthProberOptions();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // We handle redirects manually to enforce SSRF policy on every step
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        _httpClient = new HttpClient(handler, disposeHandler: true);
    }

    public async Task<ProbeResult> ProbeAsync(
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != "http" && endpoint.Scheme != "https"))
        {
            return ProbeResult.Failure(ProbeStatusCategory.SecurityPolicyViolation,
                $"Invalid URI scheme '{endpoint.Scheme}'. Only HTTP and HTTPS are permitted.");
        }

        var stopwatch = Stopwatch.StartNew();
        var currentUri = endpoint;
        var redirectsCount = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
            cts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                // 1. Destination policy / SSRF check for currentUri
                var policyCheck = await ValidateDestinationPolicyAsync(currentUri, cts.Token);
                if (policyCheck != null)
                {
                    stopwatch.Stop();
                    return policyCheck;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                request.Headers.Add("User-Agent", "CloudSwitch-Prober/1.0");

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    stopwatch.Stop();
                    return ProbeResult.Failure(ProbeStatusCategory.Cancelled, "Probe cancelled by caller.", latency: stopwatch.Elapsed);
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    return ProbeResult.Failure(ProbeStatusCategory.Timeout, $"Probe timed out after {timeout.TotalSeconds:F1}s.", latency: stopwatch.Elapsed);
                }
                catch (HttpRequestException ex) when (ex.InnerException is SocketException sockEx && sockEx.SocketErrorCode == SocketError.HostNotFound)
                {
                    stopwatch.Stop();
                    return ProbeResult.Failure(ProbeStatusCategory.DnsFailure, $"DNS resolution failed for {currentUri.Host}: {ex.Message}", latency: stopwatch.Elapsed);
                }
                catch (HttpRequestException ex)
                {
                    stopwatch.Stop();
                    return ProbeResult.Failure(ProbeStatusCategory.ConnectionFailure, $"Connection failed to {currentUri}: {ex.Message}", latency: stopwatch.Elapsed);
                }

                using (response)
                {
                    var statusCode = (int)response.StatusCode;

                    // Handle redirects manually up to max
                    if (statusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        redirectsCount++;
                        if (redirectsCount > _options.MaxRedirects)
                        {
                            stopwatch.Stop();
                            return ProbeResult.Failure(ProbeStatusCategory.HttpFailure,
                                $"Maximum redirect limit ({_options.MaxRedirects}) exceeded.",
                                statusCode: statusCode, latency: stopwatch.Elapsed);
                        }

                        var location = response.Headers.Location;
                        if (location is null)
                        {
                            stopwatch.Stop();
                            return ProbeResult.Failure(ProbeStatusCategory.HttpFailure,
                                "Redirect response did not provide a Location header.",
                                statusCode: statusCode, latency: stopwatch.Elapsed);
                        }

                        currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                        continue;
                    }

                    // Read bounded response body (up to MaxResponseBodyBytes)
                    string? snippet = null;
                    if (response.Content is not null)
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                        var buffer = new byte[_options.MaxResponseBodyBytes];
                        var bytesRead = await ReadBoundedStreamAsync(stream, buffer, cts.Token);
                        if (bytesRead > 0)
                        {
                            snippet = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        }
                    }

                    stopwatch.Stop();
                    var latency = stopwatch.Elapsed;

                    // Check HTTP status code
                    if (statusCode < 200 || statusCode > 299)
                    {
                        return ProbeResult.Failure(ProbeStatusCategory.HttpFailure,
                            $"Endpoint returned non-success HTTP status {statusCode}.",
                            statusCode: statusCode, latency: latency);
                    }

                    // Extract observed revision from headers or body
                    var observedRevision = ExtractRevision(response, snippet);

                    // If expected revision was requested, verify identity
                    if (!string.IsNullOrWhiteSpace(expectedRevision) && !string.IsNullOrWhiteSpace(observedRevision))
                    {
                        if (!string.Equals(expectedRevision.Trim(), observedRevision.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            return ProbeResult.Failure(ProbeStatusCategory.HttpFailure,
                                $"Application revision mismatch: expected '{expectedRevision}', observed '{observedRevision}'.",
                                statusCode: statusCode, latency: latency);
                        }
                    }

                    return ProbeResult.Success(statusCode, latency, observedRevision, snippet);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ProbeResult.Failure(ProbeStatusCategory.Cancelled, "Probe cancelled by caller.", latency: stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return ProbeResult.Failure(ProbeStatusCategory.Timeout, $"Probe timed out after {timeout.TotalSeconds:F1}s.", latency: stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Failure(ProbeStatusCategory.ConnectionFailure, $"Unexpected probe error: {ex.Message}", latency: stopwatch.Elapsed);
        }
    }

    private async Task<ProbeResult?> ValidateDestinationPolicyAsync(Uri uri, CancellationToken ct)
    {
        var host = uri.Host;

        // If host is explicitly whitelisted for testing, allow
        if (_options.AllowedHosts.Contains(host))
            return null;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var parsedIp))
        {
            addresses = [parsedIp];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (Exception ex)
            {
                return ProbeResult.Failure(ProbeStatusCategory.DnsFailure, $"DNS resolution failed for {host}: {ex.Message}");
            }
        }

        if (addresses.Length == 0)
            return ProbeResult.Failure(ProbeStatusCategory.DnsFailure, $"No IP addresses found for {host}.");

        foreach (var ip in addresses)
        {
            if (IsBlockedIp(ip, host))
            {
                return ProbeResult.Failure(ProbeStatusCategory.SecurityPolicyViolation,
                    $"Destination IP '{ip}' for host '{host}' is prohibited by destination security policy (SSRF protection).");
            }
        }

        return null;
    }

    private bool IsBlockedIp(IPAddress ip, string host)
    {
        // Whitelisted host overrides loopback/private checks
        if (_options.AllowedHosts.Contains(host))
            return false;

        // Cloud metadata address 169.254.169.254 and link-local range 169.254.0.0/16 are ALWAYS blocked!
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254)
                return true; // Link-local / metadata service
            if (bytes[0] == 0)
                return true; // 0.0.0.0/8

            if (!_options.AllowPrivateRanges)
            {
                if (IPAddress.IsLoopback(ip))
                    return true;
                if (bytes[0] == 10) // 10.0.0.0/8
                    return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) // 172.16.0.0/12
                    return true;
                if (bytes[0] == 192 && bytes[1] == 168) // 192.168.0.0/16
                    return true;
            }
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (!_options.AllowPrivateRanges)
            {
                if (IPAddress.IsLoopback(ip))
                    return true;
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
                    return true;
            }
        }

        return false;
    }

    private static async Task<int> ReadBoundedStreamAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }

    private static string? ExtractRevision(HttpResponseMessage response, string? body)
    {
        // 1. Check custom headers
        if (response.Headers.TryGetValues("X-CloudSwitch-Revision", out var csVals))
            return csVals.FirstOrDefault();
        if (response.Headers.TryGetValues("X-App-Revision", out var appVals))
            return appVals.FirstOrDefault();
        if (response.Headers.ETag != null)
            return response.Headers.ETag.Tag.Trim('"');

        // 2. Try parse JSON body for "revision" or "version"
        if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("revision", out var revProp))
                    return revProp.ToString();
                if (doc.RootElement.TryGetProperty("version", out var verProp))
                    return verProp.ToString();
            }
            catch
            {
                // Not valid JSON or parsing error; ignore body extraction
            }
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
