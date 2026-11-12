using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using CloudSwitch.Contracts;
using CloudSwitch.Persistence;
using CloudSwitch.Api.Contracts;
using CloudSwitch.Api.Infrastructure;

namespace CloudSwitch.Api.Services;

internal static partial class RequestValidation
{
    public static void ThrowIfInvalid(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
            throw new RequestValidationException(errors);
    }

    public static Dictionary<string, string[]> Deployment(DeploymentRequest input, EndpointOptions endpoints)
    {
        var errors = new Dictionary<string, string[]>();
        Check(!string.IsNullOrWhiteSpace(input.Name) && input.Name.Length <= 120, "name", "A name of at most 120 characters is required.");
        if (input.Definition is not { } definition)
        {
            errors["definition"] = ["Deployment definition is required."];
            return errors;
        }
        Check(!string.IsNullOrWhiteSpace(definition.Environment) && definition.Environment.Length <= 64, "definition.environment", "Environment is required (maximum 64 characters).");
        Check(Identifier().IsMatch(definition.SourceProvider ?? ""), "definition.sourceProvider", "A provider identifier is required.");
        Check(AllowedEndpoint(definition.SourceEndpoint, endpoints), "definition.sourceEndpoint", "Use an absolute HTTP(S) URL on an operator-approved host, without credentials, query or fragment.");
        if (definition.Application is not { } app)
            errors["definition.application"] = ["Application configuration is required."];
        else
        {
            Check(!string.IsNullOrWhiteSpace(app.Image) && PinnedImage().IsMatch(app.Image) && !app.Image.EndsWith(":latest", StringComparison.OrdinalIgnoreCase), "definition.application.image", "Use an explicit image tag or a complete SHA-256 digest.");
            Check(app.ImagePullSecretReference is null || SecretReference().IsMatch(app.ImagePullSecretReference),
                "definition.application.imagePullSecretReference", "Use a vault://path#key registry credential reference.");
            Check(app.Port is >= 1 and <= 65535, "definition.application.port", "Port must be between 1 and 65535.");
            Check(app.CpuMillicores is >= 100 and <= 64000, "definition.application.cpuMillicores", "CPU must be between 100 and 64000 millicores.");
            Check(app.MemoryMegabytes is >= 64 and <= 262144, "definition.application.memoryMegabytes", "Memory must be between 64 and 262144 MB.");
            Check(ValidMap(app.Settings, 8192), "definition.application.settings", "Use at most 100 named settings with values of at most 8192 characters.");
            Check(ValidSecrets(app.SecretReferences), "definition.application.secretReferences", "Use named vault://path#key references, never credential values.");
            if (app.Settings is not null && app.SecretReferences is not null)
                Check(!app.Settings.Keys.Intersect(app.SecretReferences.Keys).Any(), "definition.application", "A setting cannot also be a secret reference.");
        }

        ValidateHealthCheck(definition.HealthCheck, "definition.healthCheck", errors);
        ValidateRecovery(definition.Recovery, "definition.recovery", errors);
        if (definition.Traffic is not { } traffic)
            errors["definition.traffic"] = ["Traffic configuration is required."];
        else
        {
            Check(traffic.Driver is "simulation" or "cloudflare-dns", "definition.traffic.driver", "Supported traffic drivers are simulation and cloudflare-dns.");
            Check(traffic.RecordName is { Length: <= 253 } && Uri.CheckHostName(traffic.RecordName) == UriHostNameType.Dns,
                "definition.traffic.recordName", "A DNS entrypoint name is required.");
            Check(traffic.Driver != "cloudflare-dns" || !string.IsNullOrWhiteSpace(traffic.ZoneReference)
                && SecretReference().IsMatch(traffic.CredentialReference ?? ""), "definition.traffic", "DNS traffic control requires a zone and vault://path#key credential reference.");
        }
        return errors;
        void Check(bool valid, string field, string message) { if (!valid) errors[field] = [message]; }
    }

    public static Dictionary<string, string[]> Target(TargetRequest input, ApprovedModules modules)
    {
        var errors = new Dictionary<string, string[]>();
        var module = modules.Find(input.ModuleId ?? "", input.Provider ?? "");
        if (module is null)
            errors["moduleId"] = ["Select an approved module for this provider from /api/v1/modules."];
        if (!ValidMap(input.Inputs, 1024) || module is not null && input.Inputs.Keys.Except(module.AllowedInputs).Any())
            errors["inputs"] = ["Only inputs explicitly allowed by the selected module may be supplied."];
        if (!ValidSecrets(input.SecretReferences))
            errors["secretReferences"] = ["Use named vault://path#key references, never credential values."];
        return errors;
    }

    public static Dictionary<string, string[]> Monitoring(MonitoringUpdateRequest input)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateHealthCheck(input.HealthCheck, "healthCheck", errors);
        ValidateRecovery(input.Recovery, "recovery", errors);
        return errors;
    }

    private static void ValidateHealthCheck(HealthCheckPolicy? health, string field, Dictionary<string, string[]> errors)
    {
        if (health is null)
        {
            errors[field] = ["A health check policy is required."];
            return;
        }
        Check(health.Path is { Length: > 0 and <= 512 } && health.Path.StartsWith('/') && !health.Path.StartsWith("//")
            && !health.Path.Contains('\\') && !health.Path.Contains('#') && !health.Path.Contains('?'),
            $"{field}.path", "Use an absolute application path without query, fragment or authority.");
        Check(health.IntervalSeconds is >= 1 and <= 3600 && health.TimeoutSeconds is >= 1 and <= 60
            && health.TimeoutSeconds <= health.IntervalSeconds,
            field, "Use interval 1–3600 seconds and timeout 1–60 seconds, no greater than the interval.");
        Check(health.FailureThreshold is >= 1 and <= 100 && health.RecoveryThreshold is >= 1 and <= 100,
            $"{field}.thresholds", "Probe thresholds must be between 1 and 100.");
        void Check(bool valid, string key, string message) { if (!valid) errors[key] = [message]; }
    }

    private static void ValidateRecovery(RecoveryPolicy? recovery, string field, Dictionary<string, string[]> errors)
    {
        var valid = recovery is not null && Enum.IsDefined(recovery.Mode)
            && (recovery.Mode != RecoveryMode.Automatic
                || recovery.TargetConfigurationId is { } id && id != Guid.Empty);
        if (!valid)
            errors[field] = ["Automatic recovery requires an approved target; NotifyOnly is the default."];
    }

    private static bool AllowedEndpoint(Uri? endpoint, EndpointOptions options) => endpoint is { IsAbsoluteUri: true, Scheme: "http" or "https" }
        && string.IsNullOrEmpty(endpoint.UserInfo)
        && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)
        && options.AllowedHosts.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase);

    private static bool ValidMap([NotNullWhen(true)] Dictionary<string, string>? map, int maxValueLength) => map is not null && map.Count <= 100
        && map.All(pair => Identifier().IsMatch(pair.Key) && pair.Value is not null && pair.Value.Length <= maxValueLength);
    private static bool ValidSecrets(Dictionary<string, string>? map) => ValidMap(map, 512)
        && map.Values.All(value => SecretReference().IsMatch(value));

    [GeneratedRegex(@"\A[a-zA-Z_][a-zA-Z0-9_.-]{0,63}\z")]
    private static partial Regex Identifier();
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]*(?:@sha256:[a-fA-F0-9]{64}|:[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127})\z")]
    private static partial Regex PinnedImage();
    [GeneratedRegex(@"\Avault://[a-zA-Z0-9][a-zA-Z0-9_./-]{0,450}#[a-zA-Z0-9_.-]{1,64}\z")]
    private static partial Regex SecretReference();
}
