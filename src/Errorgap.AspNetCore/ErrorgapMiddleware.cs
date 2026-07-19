using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Errorgap;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Errorgap.AspNetCore;

public sealed class ErrorgapMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ErrorgapClient _client;
    private readonly ErrorgapApm _apm;

    public ErrorgapMiddleware(RequestDelegate next, ErrorgapClient client, ErrorgapApm apm)
    {
        _next = next;
        _client = client;
        _apm = apm;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;
        using var scope = _apm.BeginTransactionScope();
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            failure = ex;
            var route = NormalizedRoute(context);
            var options = new NoticeOptions
            {
                Context = new Dictionary<string, object?>
                {
                    ["source"] = "Errorgap.AspNetCore",
                    ["url"] = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}",
                    ["component"] = route,
                    ["action"] = context.Request.Method,
                },
                Environment = new Dictionary<string, object?>
                {
                    ["method"] = context.Request.Method,
                    ["path"] = (string)context.Request.Path,
                    ["route"] = route,
                    ["user_agent"] = context.Request.Headers.UserAgent.ToString(),
                    ["remote_addr"] = context.Connection.RemoteIpAddress?.ToString(),
                },
                Session = new Dictionary<string, object?>
                {
                    ["request_id"] = context.TraceIdentifier,
                },
                Params = await RequestParams(context),
            };
            _client.Notify(ex, options);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            _client.NotifyTransaction(new ApmTransaction
            {
                Kind = "web",
                Method = context.Request.Method,
                Path = NormalizedRoute(context),
                PathRaw = context.Request.Path,
                StatusCode = failure is null ? context.Response.StatusCode : 500,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                Spans = scope.Complete(),
            });
        }
    }

    private static string NormalizedRoute(HttpContext context)
    {
        return (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
               ?? context.Request.Path.Value
               ?? "/";
    }

    private static async Task<IDictionary<string, object?>> RequestParams(HttpContext context)
    {
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value.ToString());
        var parameters = new Dictionary<string, object?> { ["query"] = query };
        if (context.Request.HasFormContentType)
        {
            try
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                parameters["form"] = form.ToDictionary(
                    pair => pair.Key,
                    pair => (object?)pair.Value.ToString());
            }
            catch (Exception)
            {
                // Request bodies may already be unavailable after a downstream failure.
            }
        }
        return parameters;
    }
}

public static class ErrorgapApplicationBuilderExtensions
{
    public static IApplicationBuilder UseErrorgap(this IApplicationBuilder app)
        => app.UseMiddleware<ErrorgapMiddleware>();
}
