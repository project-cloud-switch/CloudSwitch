using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CloudSwitch.Api.OpenApi;

internal sealed class ApiOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (operation.OperationId is "UpdateDeployment" or "DeleteDeployment" or "UpdateTarget" or "DeleteTarget")
            AddHeader(operation, "If-Match", true, "The exact quoted ETag returned by the resource, such as \"1\".");
        if (operation.OperationId == "SubmitMigration")
        {
            AddHeader(operation, "Idempotency-Key", true, "A key of 1–128 characters. Reuse it when retrying the same submission.");
            AddHeader(operation, "X-Correlation-ID", false, "An optional nonempty UUID. The API generates one when omitted.");
        }

        foreach (var (status, response) in operation.Responses ?? [])
        {
            if (!int.TryParse(status, out var code) || code < 400 || response.Content is not { Count: > 0 } content)
                continue;
            var media = content.Values.First();
            content.Clear();
            content["application/problem+json"] = media;
        }
        return Task.CompletedTask;
    }

    private static void AddHeader(OpenApiOperation operation, string name, bool required, string description)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name, In = ParameterLocation.Header, Required = required, Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
}
