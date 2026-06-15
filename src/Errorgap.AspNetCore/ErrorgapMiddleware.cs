using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Errorgap;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Errorgap.AspNetCore;

public sealed class ErrorgapMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ErrorgapClient _client;

    public ErrorgapMiddleware(RequestDelegate next, ErrorgapClient client)
    {
        _next = next;
        _client = client;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var options = new NoticeOptions
            {
                Context = new Dictionary<string, object?>
                {
                    ["source"] = "Errorgap.AspNetCore",
                    ["url"] = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}",
                    ["component"] = (string)context.Request.Path,
                    ["action"] = context.Request.Method,
                },
                Environment = new Dictionary<string, object?>
                {
                    ["method"] = context.Request.Method,
                    ["path"] = (string)context.Request.Path,
                    ["query_string"] = context.Request.QueryString.ToString(),
                    ["user_agent"] = context.Request.Headers.UserAgent.ToString(),
                    ["remote_addr"] = context.Connection.RemoteIpAddress?.ToString(),
                },
            };
            _client.Notify(ex, options);
            throw;
        }
    }
}

public static class ErrorgapApplicationBuilderExtensions
{
    public static IApplicationBuilder UseErrorgap(this IApplicationBuilder app)
        => app.UseMiddleware<ErrorgapMiddleware>();
}
