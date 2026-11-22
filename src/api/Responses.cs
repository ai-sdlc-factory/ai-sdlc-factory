namespace AiSdlcFactory.Api;

internal static class Responses
{
    /// <summary>Error body shared by every endpoint: <c>{"detail": "..."}</c>.</summary>
    public static IResult Detail(int status, string detail) =>
        Results.Json(new { detail }, statusCode: status);
}
