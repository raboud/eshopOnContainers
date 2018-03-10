﻿using Autofac;
using Autofac.Extensions.DependencyInjection;
using HMS.Catalog.API.Infrastructure;
using HMS.Catalog.API.Infrastructure.Exceptions;
using HMS.Catalog.API.IntegrationEvents;
using HMS.Catalog.API.IntegrationEvents.EventHandling;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.ServiceFabric;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.BuildingBlocks.EventBus;
using Microsoft.BuildingBlocks.EventBus.Abstractions;
using Microsoft.BuildingBlocks.EventBusRabbitMQ;
using Microsoft.BuildingBlocks.EventBusServiceBus;
using Microsoft.BuildingBlocks.Infrastructure.Filters;
using Microsoft.BuildingBlocks.IntegrationEventLogEF;
using Microsoft.BuildingBlocks.IntegrationEventLogEF.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Serialization;
using RabbitMQ.Client;
using System;
using System.Data.Common;
using System.Reflection;
using System.Collections.Generic;
using Swashbuckle.AspNetCore.Swagger;
using Microsoft.AspNetCore.Http;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using IdentityServer4.AccessTokenValidation;
using HMS.Catalog.API.Infrastructure.Middlewares;
using HMS.Catalog.API.Services;
using AutoMapper;
using HMS.IntegrationEvents.Events;

namespace HMS.Catalog.API
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
			RegisterAppInsights(services);

			// Add framework services.
			services.AddMvc(options =>
			{
				options.Filters.Add(typeof(HttpGlobalExceptionFilter<CatalogDomainException>));
			}).AddControllersAsServices()
			.AddJsonOptions(
				options => {
					options.SerializerSettings.ContractResolver = new DefaultContractResolver();
					options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
				}
			);

			services.AddMvcCore()
			.AddAuthorization();

			ConfigureAuthService(services);

			services.AddHealthChecks(checks =>
            {
				int minutes = 1;
                if (int.TryParse(Configuration["HealthCheck:Timeout"], out int minutesParsed))
                {
                    minutes = minutesParsed;
                }
                checks.AddSqlCheck("CatalogDb", Configuration["ConnectionString"], TimeSpan.FromMinutes(minutes));

				string accountName = Configuration.GetValue<string>("AzureStorageAccountName");
				string accountKey = Configuration.GetValue<string>("AzureStorageAccountKey");
                if (!string.IsNullOrEmpty(accountName) && !string.IsNullOrEmpty(accountKey))
                {
                    checks.AddAzureBlobStorageCheck(accountName, accountKey);
                }
            });

            services.AddDbContext<CatalogContext>(options =>
            {
                options.UseSqlServer(Configuration["ConnectionString"],
                                     sqlServerOptionsAction: sqlOptions =>
                                     {
                                         sqlOptions.MigrationsAssembly(typeof(Startup).GetTypeInfo().Assembly.GetName().Name);
                                         //Configuring Connection Resiliency: https://docs.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency 
                                         sqlOptions.EnableRetryOnFailure(maxRetryCount: 10, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                                     });

                // Changing default behavior when client evaluation occurs to throw. 
                // Default in EF Core would be to log a warning when client evaluation is performed.
                options.ConfigureWarnings(warnings => warnings.Throw(RelationalEventId.QueryClientEvaluationWarning));
                //Check Client vs. Server evaluation: https://docs.microsoft.com/en-us/ef/core/querying/client-eval
            });

            services.AddDbContext<IntegrationEventLogContext>(options =>
            {
                options.UseSqlServer(Configuration["ConnectionString"],
                                     sqlServerOptionsAction: sqlOptions =>
                                     {
                                         sqlOptions.MigrationsAssembly(typeof(Startup).GetTypeInfo().Assembly.GetName().Name);
                                         //Configuring Connection Resiliency: https://docs.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency 
                                         sqlOptions.EnableRetryOnFailure(maxRetryCount: 10, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                                     });
            });

            services.Configure<CatalogSettings>(Configuration);

            // Add framework services.
            services.AddSwaggerGen(options =>
            {
                options.DescribeAllEnumsAsStrings();
                options.SwaggerDoc("v1", new Swashbuckle.AspNetCore.Swagger.Info
                {
                    Title = "HMS - Catalog HTTP API",
                    Version = "v1",
                    Description = "The Catalog Microservice HTTP API. This is a Data-Driven/CRUD microservice sample",
                    TermsOfService = "Terms Of Service"
                });

				options.AddSecurityDefinition("oauth2", new OAuth2Scheme
				{
					Type = "oauth2",
					Flow = "implicit",
					AuthorizationUrl = $"{Configuration.GetValue<string>("IdentityUrlExternal")}/connect/authorize",
					TokenUrl = $"{Configuration.GetValue<string>("IdentityUrlExternal")}/connect/token",
					Scopes = new Dictionary<string, string>()
					{
						{ "catalog", "Catalog API" }
					}
				});

				options.OperationFilter<AuthorizeCheckOperationFilter>();
			});

            services.AddCors(options =>
            {
                options.AddPolicy("CorsPolicy",
                    builder => builder.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials());
            });
			services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
			services.AddTransient<IIdentityService, IdentityService>();

			services.AddTransient<Func<DbConnection, IIntegrationEventLogService>>(
                sp => (DbConnection c) => new IntegrationEventLogService(c));

            services.AddTransient<ICatalogIntegrationEventService, CatalogIntegrationEventService>();

            if (Configuration.GetValue<bool>("AzureServiceBusEnabled"))
            {
                services.AddSingleton<IServiceBusPersisterConnection>(sp =>
                {
					CatalogSettings settings = sp.GetRequiredService<IOptions<CatalogSettings>>().Value;
					ILogger<DefaultServiceBusPersisterConnection> logger = sp.GetRequiredService<ILogger<DefaultServiceBusPersisterConnection>>();

					ServiceBusConnectionStringBuilder serviceBusConnection = new ServiceBusConnectionStringBuilder(settings.EventBusConnection);

                    return new DefaultServiceBusPersisterConnection(serviceBusConnection, logger);
                });
            }
            else
            {
                services.AddSingleton<IRabbitMQPersistentConnection>(sp =>
                {
					CatalogSettings settings = sp.GetRequiredService<IOptions<CatalogSettings>>().Value;
					ILogger<DefaultRabbitMQPersistentConnection> logger = sp.GetRequiredService<ILogger<DefaultRabbitMQPersistentConnection>>();

					ConnectionFactory factory = new ConnectionFactory()
                    {
                        HostName = Configuration["EventBusConnection"]
                    };

                    if (!string.IsNullOrEmpty(Configuration["EventBusUserName"]))
                    {
                        factory.UserName = Configuration["EventBusUserName"];
                    }

                    if (!string.IsNullOrEmpty(Configuration["EventBusPassword"]))
                    {
                        factory.Password = Configuration["EventBusPassword"];
                    }

					int retryCount = 5;
                    if (!string.IsNullOrEmpty(Configuration["EventBusRetryCount"]))
                    {
                        retryCount = int.Parse(Configuration["EventBusRetryCount"]);
                    }

                    return new DefaultRabbitMQPersistentConnection(factory, logger, retryCount);
                });
            }
			AutoMapper.ServiceCollectionExtensions.UseStaticRegistration = false;
			services.AddAutoMapper();

            RegisterEventBus(services);

			ContainerBuilder container = new ContainerBuilder();
            container.Populate(services);
            return new AutofacServiceProvider(container.Build());

        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            //Configure logs
            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            loggerFactory.AddDebug();
            loggerFactory.AddAzureWebAppDiagnostics();
            loggerFactory.AddApplicationInsights(app.ApplicationServices, LogLevel.Trace);

			string pathBase = Configuration["PATH_BASE"];
            if (!string.IsNullOrEmpty(pathBase))
            {
                loggerFactory.CreateLogger("init").LogDebug($"Using PATH BASE '{pathBase}'");
                app.UsePathBase(pathBase);
            }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
            app.Map("/liveness", lapp => lapp.Run(async ctx => ctx.Response.StatusCode = 200));
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously

            app.UseCors("CorsPolicy");

			ConfigureAuth(app);

			app.UseMvcWithDefaultRoute();

            app.UseSwagger()
              .UseSwaggerUI(c =>
              {
                  c.SwaggerEndpoint($"{ (!string.IsNullOrEmpty(pathBase) ? pathBase : string.Empty) }/swagger/v1/swagger.json", "Catalog.API V1");
				  //				  c.ConfigureOAuth2("catalogswaggerui", "", "", "Catalog Swagger UI");
				  c.OAuthClientId("catalogswaggerui");
				  c.OAuthAppName("Catalog Swagger UI");
			  });

            ConfigureEventBus(app);
        }

        private void RegisterAppInsights(IServiceCollection services)
        {
            services.AddApplicationInsightsTelemetry(Configuration);
			string orchestratorType = Configuration.GetValue<string>("OrchestratorType");

            if (orchestratorType?.ToUpper() == "K8S")
            {
                // Enable K8s telemetry initializer
                services.EnableKubernetes();
            }
            if (orchestratorType?.ToUpper() == "SF")
            {
                // Enable SF telemetry initializer
                services.AddSingleton<ITelemetryInitializer>((serviceProvider) =>
                    new FabricTelemetryInitializer());
            }
        }

		private void ConfigureAuthService(IServiceCollection services)
		{
			// prevent from mapping "sub" claim to nameidentifier.
			JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

			services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
				.AddIdentityServerAuthentication(options =>
				{
					SetIS4Options(options);
				});
		}

		virtual public void SetIS4Options(IdentityServerAuthenticationOptions options)
		{
			string identityUrl = Configuration.GetValue<string>("IdentityUrl");

			options.Authority = identityUrl;
			options.RequireHttpsMetadata = false;
			options.SupportedTokens = SupportedTokens.Jwt;
			options.ApiName = "catalog";
		}

		protected virtual void ConfigureAuth(IApplicationBuilder app)
		{
			if (Configuration.GetValue<bool>("UseLoadTest"))
			{
				app.UseMiddleware<ByPassAuthMiddleware>();
			}

			app.UseAuthentication();
		}


		private void RegisterEventBus(IServiceCollection services)
        {
			string subscriptionClientName = Configuration["SubscriptionClientName"];

            if (Configuration.GetValue<bool>("AzureServiceBusEnabled"))
            {
                services.AddSingleton<IEventBus, EventBusServiceBus>(sp =>
                {
					IServiceBusPersisterConnection serviceBusPersisterConnection = sp.GetRequiredService<IServiceBusPersisterConnection>();
					ILifetimeScope iLifetimeScope = sp.GetRequiredService<ILifetimeScope>();
					ILogger<EventBusServiceBus> logger = sp.GetRequiredService<ILogger<EventBusServiceBus>>();
					IEventBusSubscriptionsManager eventBusSubcriptionsManager = sp.GetRequiredService<IEventBusSubscriptionsManager>();

                    return new EventBusServiceBus(serviceBusPersisterConnection, logger,
                        eventBusSubcriptionsManager, subscriptionClientName, iLifetimeScope);
                });

            }
            else
            {
                services.AddSingleton<IEventBus, EventBusRabbitMQ>(sp =>
                {
					IRabbitMQPersistentConnection rabbitMQPersistentConnection = sp.GetRequiredService<IRabbitMQPersistentConnection>();
					ILifetimeScope iLifetimeScope = sp.GetRequiredService<ILifetimeScope>();
					ILogger<EventBusRabbitMQ> logger = sp.GetRequiredService<ILogger<EventBusRabbitMQ>>();
					IEventBusSubscriptionsManager eventBusSubcriptionsManager = sp.GetRequiredService<IEventBusSubscriptionsManager>();

					int retryCount = 5;
                    if (!string.IsNullOrEmpty(Configuration["EventBusRetryCount"]))
                    {
                        retryCount = int.Parse(Configuration["EventBusRetryCount"]);
                    }

                    return new EventBusRabbitMQ(rabbitMQPersistentConnection, logger, iLifetimeScope, eventBusSubcriptionsManager, subscriptionClientName, retryCount);
                });
            }

            services.AddSingleton<IEventBusSubscriptionsManager, InMemoryEventBusSubscriptionsManager>();
            services.AddTransient<OrderStatusChangedToAwaitingValidationIntegrationEventHandler>();
            services.AddTransient<OrderStatusChangedToPaidIntegrationEventHandler>();
        }
        protected virtual void ConfigureEventBus(IApplicationBuilder app)
        {
			IEventBus eventBus = app.ApplicationServices.GetRequiredService<IEventBus>();
            eventBus.Subscribe<OrderStatusChangedToAwaitingValidationIntegrationEvent, OrderStatusChangedToAwaitingValidationIntegrationEventHandler>();
            eventBus.Subscribe<OrderStatusChangedToPaidIntegrationEvent, OrderStatusChangedToPaidIntegrationEventHandler>();
        }
    }
}
