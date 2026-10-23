using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.Api.Http;

/// <summary>
/// Serves JSON derived from a snapshot with an ETag built from the snapshot version and the caller's
/// permission fingerprint. Unchanged data costs a 304 with no body; users with equal permissions
/// share the serialized payload instead of re-filtering it.
/// </summary>
public sealed class ConditionalJson(IMemoryCache cache, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    public IResult Respond<T>(HttpContext http, string kind, string cluster, ulong version, string permissionFingerprint, Func<T> build)
    {
        var etag = $"\"{kind}-{version:x16}-{permissionFingerprint}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, no-cache";

        if (http.Request.Headers.IfNoneMatch.Contains(etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var bytes = cache.GetOrCreate((cluster, etag), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return JsonSerializer.SerializeToUtf8Bytes(build(), json.Value.SerializerOptions);
        })!;
        return Results.Bytes(bytes, "application/json");
    }
}
