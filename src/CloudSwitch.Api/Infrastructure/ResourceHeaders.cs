namespace CloudSwitch.Api.Infrastructure;

internal static class ResourceHeaders
{
    public static long? ExpectedRevision(HttpRequest request)
        => request.Headers.IfMatch.ToString() switch
        {
            "" => null,
            var value when long.TryParse(value.Trim('"'), out var revision) && value == $"\"{revision}\"" => revision,
            _ => -1
        };

    public static void ETag(HttpResponse response, long revision)
        => response.Headers.ETag = $"\"{revision}\"";
}
