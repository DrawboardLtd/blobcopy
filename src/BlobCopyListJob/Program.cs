using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace BlobCopyListJob
{
	class Program
	{
		static async Task Main(string[] args)
		{
			var builder = new HostBuilder()
				.ConfigureAppConfiguration((hostingContext, config) =>
				{
					config
						.AddJsonFile("appsettings.json", false, true)
						.AddEnvironmentVariables();
				})
				.ConfigureLogging((_, logging) =>
				{
					logging.SetMinimumLevel(LogLevel.Trace);
					logging.AddSimpleConsole(options =>
						{
							options.IncludeScopes = true;
							options.SingleLine = true;
							options.TimestampFormat = "hh:mm:ss ";
						})
						.AddFilter("Microsoft.Hosting.LifeTime", LogLevel.None);
				})
				.ConfigureServices((hostingContext, services) =>
				{
					var redisHost = hostingContext.Configuration.GetValue<string>("RedisHost");
					var redisPassword = hostingContext.Configuration.GetValue<string>("RedisPassword");
					var redisPrefix = hostingContext.Configuration.GetValue<string>("RedisPrefix");
					var servicebus = hostingContext.Configuration.GetValue<string>("ServiceBus");
					var serviceBusClient =
						new ServiceBusClient(servicebus);

					services.AddSingleton(serviceBusClient);

					var redisOptions = new ConfigurationOptions()
					{
						EndPoints = { redisHost },
						Ssl = true,
						AllowAdmin = false,
						AbortOnConnectFail = false,
						Password = redisPassword
					};

					services.AddSingleton((_) => ConnectionMultiplexer.Connect(redisOptions));
					services.AddTransient((provider) =>
						provider.GetService<ConnectionMultiplexer>().GetDatabase().WithKeyPrefix(redisPrefix));

					services.AddSingleton(hostingContext.Configuration);
					services.AddHostedService<CopyJobHost>();
				})
				.UseConsoleLifetime();

			await builder.RunConsoleAsync();
		}
	}
}