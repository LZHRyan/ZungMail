using ZungMail.Update.Protocol;
using ZungMail.UpdateServer.Security;

namespace ZungMail.UpdateServer.Endpoints;

/// <summary>管理接口统一鉴权：只认 HttpOnly Cookie 会话，缺失或过期返回 401。</summary>
public sealed class AdminSessionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var auth = http.RequestServices.GetRequiredService<AdminAuthService>();
        var token = http.Request.Cookies[AdminAuthService.CookieName];
        if (!auth.ValidateSession(token))
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "未登录或会话已过期，请重新登录。" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}
