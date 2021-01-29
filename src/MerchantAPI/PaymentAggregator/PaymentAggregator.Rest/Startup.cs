// Copyright (c) 2020 Bitcoin Association

using System.Linq;
using MerchantAPI.Common;
using MerchantAPI.Common.Authentication;
using MerchantAPI.Common.Clock;
using MerchantAPI.Common.Database;
using MerchantAPI.Common.EventBus;
using MerchantAPI.PaymentAggregator.Rest.Swagger;
using MerchantAPI.PaymentAggregator.Consts;
using MerchantAPI.PaymentAggregator.Domain;
using MerchantAPI.PaymentAggregator.Domain.Actions;
using MerchantAPI.PaymentAggregator.Domain.Client;
using MerchantAPI.PaymentAggregator.Domain.Models;
using MerchantAPI.PaymentAggregator.Domain.Repositories;
using MerchantAPI.PaymentAggregator.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using MerchantAPI.PaymentAggregator.Rest.Actions;
using MerchantAPI.Common.Startup;

namespace MerchantAPI.PaymentAggregator.Rest
{
  public class Startup
  {
    IWebHostEnvironment HostEnvironment { get; set; }

    public Startup(IConfiguration configuration, IWebHostEnvironment hostEnvironment)
    {
      Configuration = configuration;
      this.HostEnvironment = hostEnvironment;
    }

    public IConfiguration Configuration { get; }

    // This method gets called by the runtime. Use this method to add services to the container.
    public virtual void ConfigureServices(IServiceCollection services)
    {
      // time in database is UTC so it is automatically mapped to Kind=UTC
      Dapper.SqlMapper.AddTypeHandler(new Common.TypeHandlers.DateTimeHandler());

      services.AddOptions<IdentityProviders>()
        .Bind(Configuration.GetSection("IdentityProviders"))
        .ValidateDataAnnotations();

      services.AddOptions<AppSettings>()
        .Bind(Configuration.GetSection("AppSettings"))
        .ValidateDataAnnotations();


      services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AppSettings>, AppSettingValidator>());
      services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<IdentityProviders>, IdentityProvidersValidator>());

      services.AddAuthentication(options =>
      {
        options.DefaultAuthenticateScheme = ApiKeyAuthenticationOptions.DefaultScheme;
        options.DefaultChallengeScheme = ApiKeyAuthenticationOptions.DefaultScheme;
        options.AddScheme(ApiKeyAuthenticationOptions.DefaultScheme, a => a.HandlerType = typeof(ApiKeyAuthenticationHandler<AppSettings>));
      });


      services.AddControllers().AddJsonOptions(options => { options.JsonSerializerOptions.WriteIndented = true; });

      services.AddSingleton<IEventBus, InMemoryEventBus>();

      services.AddTransient<IGateways, Gateways>();
      services.AddTransient<IAggregator, Aggregator>();
      services.AddTransient<IGatewayRepository, GatewayRepositoryPostgres>();
      services.AddTransient<IAccountRepository, AccountRepositoryPostgres>();
      services.AddTransient<ISubscriptionRepository, SubscriptionRepositoryPostgres>();
      services.AddTransient<IServiceLevelRepository, ServiceLevelRepositoryPostgres>();
      services.AddTransient<IServiceRequestRepository, ServiceRequestRepositoryPostgres>();

      services.AddTransient<ICreateDB, CreateDB>();
      services.AddTransient<IStartupChecker, StartupChecker>();

      services.AddHttpClient("APIGatewayClient");
      services.AddTransient<IApiGatewayClientFactory, ApiGatewayClientFactory>();
      services.AddTransient<IApiGatewayMultiClient, ApiGatewayMultiClient>();

      if (HostEnvironment.EnvironmentName != "Testing")
      {
        services.AddTransient<IClock, Clock>();
        services.AddHostedService<CleanUpServiceRequestHandler>();
      }
      else
      {
        // We register clock as singleton, so that we can set time in individual tests
        services.AddSingleton<IClock, MockedClock>();
      }

      services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();

      services.AddSingleton<IdentityProviderStore>();
      services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
          options.RefreshOnIssuerKeyNotFound = false;
          // We validate audience and issuer through IdentityProviders
          options.TokenValidationParameters.ValidateAudience = false;
          options.TokenValidationParameters.ValidateIssuer = false;
          // The rest of the options are configured in ConfigureJwtBearerOptions
        }
        );

      services.AddCors(options =>
      {
        options.AddDefaultPolicy(
            builder =>
            {
              builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            });
      });

      services.AddSwaggerGen(c =>
      {
        c.SwaggerDoc(SwaggerGroup.API, new OpenApiInfo { Title = "Payment Aggregator", Version = Const.PAYMENT_AGGREGATOR_API_VERSION });
        c.SwaggerDoc(SwaggerGroup.Admin, new OpenApiInfo { Title = "Payment Aggregator Admin", Version = Const.PAYMENT_AGGREGATOR_API_VERSION });
        c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());

        // Add MAPI authorization options
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
          In = ParameterLocation.Header,
          Description = "Please enter JWT with Bearer needed to access MAPI into field. Authorization: Bearer JWT",
          Name = "Authorization",
          Type = SecuritySchemeType.ApiKey
        });

        c.AddSecurityRequirement(new OpenApiSecurityRequirement {
          {
            new OpenApiSecurityScheme
            {
              Reference = new OpenApiReference
              {
                Type = ReferenceType.SecurityScheme,
                Id = "Bearer"
              }
            },
            new string[] { }
          }
        });

        // Add Admin authorization options.
        c.AddSecurityDefinition(ApiKeyAuthenticationHandler<AppSettings>.ApiKeyHeaderName, new OpenApiSecurityScheme
        {
          Description = @"Please enter API key needed to access admin endpoints into field. Api-Key: My_API_Key",
          In = ParameterLocation.Header,
          Name = ApiKeyAuthenticationHandler<AppSettings>.ApiKeyHeaderName,
          Type = SecuritySchemeType.ApiKey,
        });

        c.AddSecurityRequirement(new OpenApiSecurityRequirement {
          {
            new OpenApiSecurityScheme
            {
              Name = ApiKeyAuthenticationHandler<AppSettings>.ApiKeyHeaderName,
              Type = SecuritySchemeType.ApiKey,
              In = ParameterLocation.Header,
              Reference = new OpenApiReference
              {
                Type = ReferenceType.SecurityScheme,
                Id = ApiKeyAuthenticationHandler<AppSettings>.ApiKeyHeaderName
              },
            },
            new string[] {}
          }
        });

        // Set the comments path for the Swagger JSON and UI.
        var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, xmlFile);
        c.IncludeXmlComments(xmlPath);
      });
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
      if (env.IsDevelopment())
      {
        app.UseExceptionHandler("/error-development");
      }
      else
      {
        app.UseExceptionHandler("/error");
      }

      app.Use(async (context, next) =>
      {
        // Prevent sensitive information from being cached.
        context.Response.Headers.Add("cache-control", "no-store");
        // To protect against drag-and-drop style clickjacking attacks.
        context.Response.Headers.Add("Content-Security-Policy", "frame-ancestors 'none'");
        // To prevent browsers from performing MIME sniffing, and inappropriately interpreting responses as HTML.
        context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
        // To protect against drag-and-drop style clickjacking attacks.
        context.Response.Headers.Add("X-Frame-Options", "DENY");
        // To require connections over HTTPS and to protect against spoofed certificates.
        context.Response.Headers.Add("Strict-Transport-Security", "max-age=63072000; includeSubDomains; preload");
        await next();
      });

      app.UseHttpsRedirection();

      app.UseSwagger();
      app.UseSwaggerUI(c =>
      {
        c.SwaggerEndpoint($"/swagger/{SwaggerGroup.API}/swagger.json", "Payment Aggregator API");
        c.SwaggerEndpoint($"/swagger/{SwaggerGroup.Admin}/swagger.json", "Payment Aggregator API Admin");
      });

      app.UseRouting();
      app.UseCors();

      app.UseAuthentication();
      app.UseAuthorization();

      app.UseEndpoints(endpoints =>
      {
        endpoints.MapControllers();
      });
    }
  }
}
