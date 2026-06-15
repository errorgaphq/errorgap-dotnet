using System;
using Errorgap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Errorgap.AspNetCore;

public static class ErrorgapServiceCollectionExtensions
{
    public static IServiceCollection AddErrorgap(
        this IServiceCollection services,
        Action<ErrorgapConfiguration>? configure = null)
    {
        services.AddSingleton(sp =>
        {
            var config = new ErrorgapConfiguration();
            var ic = sp.GetService<IConfiguration>();
            if (ic is not null)
            {
                ic.GetSection("Errorgap").Bind(config);
            }
            configure?.Invoke(config);
            return config;
        });
        services.AddSingleton<ErrorgapClient>(sp =>
            new ErrorgapClient(sp.GetRequiredService<ErrorgapConfiguration>()));
        return services;
    }
}
