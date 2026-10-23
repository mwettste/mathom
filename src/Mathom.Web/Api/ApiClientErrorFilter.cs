using Mathom.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Mathom.Web.Api;

// [ApiController] turns bodiless 404/415 results into ProblemDetails. Under /api those must be
// { error, code } like every other API error; web paths keep the framework's response.
public sealed class ApiClientErrorFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/api")) return;
        if (context.Result is not ObjectResult { Value: ProblemDetails problem }) return;
        context.Result = problem.Status switch
        {
            StatusCodes.Status404NotFound => ApiError.Result(StatusCodes.Status404NotFound, "not_found", "Not found."),
            StatusCodes.Status415UnsupportedMediaType => ApiError.Result(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "Unsupported media type."),
            _ => context.Result,
        };
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
