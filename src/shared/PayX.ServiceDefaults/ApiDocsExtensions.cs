using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace PayX.ServiceDefaults;

/// <summary>
/// Marks an endpoint that requires an <c>Idempotency-Key</c> header, so the
/// API docs show the header. Implemented by each service's own
/// <c>[Idempotent]</c> attribute (Payment, PSP simulator).
/// </summary>
public interface IRequiresIdempotencyKey;

/// <summary>
/// Interactive API docs for debugging (Development only): OpenAPI at
/// <c>/openapi/v1.json</c> and a Scalar page at <c>/scalar</c> on every
/// service, where requests can be sent from the browser. Same tooling as the
/// sibling SuggestX project.
/// </summary>
internal static class ApiDocsExtensions
{
    public static void AddPayXApiDocs(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            // A "Bearer" scheme, so Scalar offers one box for the access token
            // and sends it with every request.
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "An access token from POST /api/users/login (Identity, or the Gateway).",
                };
                document.Security ??= [];
                document.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
                });
                return Task.CompletedTask;
            });

            // The Idempotency-Key header, on the endpoints that require it.
            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IRequiresIdempotencyKey>().Any())
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "Idempotency-Key",
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "Same key = same request: a retry gets the first answer instead of running again.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    });
                }
                return Task.CompletedTask;
            });
        });

    public static void MapPayXApiDocs(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        // Anonymous: the docs themselves need no token (services deny by default).
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference(options => options.WithTitle(app.Environment.ApplicationName)).AllowAnonymous();
    }
}
