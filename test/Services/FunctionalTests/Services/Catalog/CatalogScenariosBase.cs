﻿using HMS.FunctionalTests.Services.Identity;
using IdentityServer4.AccessTokenValidation;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.BuildingBlocks.IntegrationEventLogEF;
using HMS.Catalog.API;
using HMS.Catalog.API.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using HMS.Catalog.API.Model;
using HMS.Common.API;
using HMS.Catalog.DTO;

namespace HMS.FunctionalTests.Services.Catalog
{
    public class CatalogScenariosBase : IDisposable
    {
		private TestServer _testServer { get; set; }

		public CatalogScenariosBase(IdentityServer idServer)
		{
			CreateServer(idServer);
		}

		public CatalogClient CreateClient()
		{
			return new CatalogClient(_testServer.CreateHandler()) { BaseAddress = _testServer.BaseAddress };
		}

		private void CreateServer(IdentityServer idServer)
        {
			TestStartup.BackChannelHandler = idServer.CreateHandler();
			IWebHostBuilder webHostBuilder = WebHost.CreateDefaultBuilder();
            webHostBuilder.UseContentRoot(Directory.GetCurrentDirectory() + "\\Services\\Catalog");
            webHostBuilder.UseStartup<TestStartup>();

            _testServer = new TestServer(webHostBuilder);

            _testServer.Host
                .MigrateDbContext<CatalogContext>((context, services) =>
                {
					IHostingEnvironment env = services.GetService<IHostingEnvironment>();
					IOptions<CatalogSettings> settings = services.GetService<IOptions<CatalogSettings>>();
					ILogger<CatalogContextSeed> logger = services.GetService<ILogger<CatalogContextSeed>>();

                    new CatalogContextSeed()
                    .SeedAsync(context, env, settings, logger)
                    .Wait();
                })
                .MigrateDbContext<IntegrationEventLogContext>((_, __) => { });
        }

		public void Dispose()
		{
			_testServer.Dispose();
		}

		public static class Get
        {
            public static string Orders = "api/v1/orders";

            public static string Page = "api/v1/Products/page";
			public static string Item = "api/v1/Products";

			public static string ProductByName(string name)
            {
                return $"api/v1/products/items?name/{name}";
            }
        }

        public static class Put
        {
            public static string UpdateCatalogProduct = "api/v1/products/";
        }

		public class TestStartup : Startup
		{
			public TestStartup(IConfiguration configuration) : base(configuration) { }

			public static HttpMessageHandler BackChannelHandler { get; set; }

			public override void SetIS4Options(IdentityServerAuthenticationOptions options)
			{
				base.SetIS4Options(options);
				options.IntrospectionBackChannelHandler = BackChannelHandler;
				options.IntrospectionDiscoveryHandler = BackChannelHandler;
				options.JwtBackChannelHandler = BackChannelHandler;
			}

		}
	}

	public class CatalogClient : HttpClient
	{
		public CatalogClient(HttpMessageHandler handler) : base(handler) { }

		public async Task<PaginatedItemsViewModel<ProductDTO>> GetCatalogAsync()
		{
			HttpResponseMessage response = await GetAsync(CatalogScenariosBase.Get.Page);
			string items = await response.Content.ReadAsStringAsync();
			return JsonConvert.DeserializeObject<PaginatedItemsViewModel<ProductDTO>>(items);
		}

		public async Task<HttpResponseMessage> UpdateProduct(ProductDTO product)
		{
			StringContent content = new StringContent(JsonConvert.SerializeObject(product), UTF8Encoding.UTF8, "application/json");
			return await PutAsync(CatalogScenariosBase.Put.UpdateCatalogProduct + $"{product.Id}", content);
		}

		public async Task<ProductDTO> GetCatalogItemAsync(int id)
		{
			HttpResponseMessage response = await GetAsync(CatalogScenariosBase.Get.Item + $"/{id}");
			string items = await response.Content.ReadAsStringAsync();
			return JsonConvert.DeserializeObject<ProductDTO>(items);
		}

	}
}
