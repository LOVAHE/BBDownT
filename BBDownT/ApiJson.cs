using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BBDownT;

record struct MyOptionBindingResult<T>(T? Result, Exception? Exception)
{
    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        TypeInfoResolver = SourceGenerationContext.Default.WithAddedModifier(KeepDefaultForNull)
    };

    public bool IsValid => Exception is null;

    public static async ValueTask<MyOptionBindingResult<T>> BindAsync(HttpContext httpContext)
    {
        try
        {
            if (!RequestOptions.TryGetTypeInfo(typeof(T), out var jsonTypeInfo))
            {
                return new(default, new InvalidOperationException($"Cannot find TypeInfo for type {typeof(T)}"));
            }
            var item = await httpContext.Request.ReadFromJsonAsync(jsonTypeInfo);

            if (item is null) return new(default, new NoNullAllowedException());

            return new((T)item, null);
        }
        catch (Exception ex)
        {
            return new(default, ex);
        }
    }

    private static void KeepDefaultForNull(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.PropertyType.IsValueType || property.Set is not { } set) continue;
            property.Set = (target, value) =>
            {
                if (value is not null) set(target, value);
            };
        }
    }
}

[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(DownloadTask))]
[JsonSerializable(typeof(List<DownloadTask>))]
[JsonSerializable(typeof(DownloadTaskCollection))]
[JsonSerializable(typeof(TaskSubmissionResult))]
public partial class AppJsonSerializerContext : JsonSerializerContext
{

}

[JsonSerializable(typeof(MyOption))]
[JsonSerializable(typeof(ServeRequestOptions))]
internal partial class SourceGenerationContext : JsonSerializerContext
{

}
