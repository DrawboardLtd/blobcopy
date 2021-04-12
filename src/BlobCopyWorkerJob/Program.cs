using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace BlobCopyWorkerJob
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
					services.AddSingleton(hostingContext.Configuration);

					var redisHost = hostingContext.Configuration.GetValue<string>("RedisHost");
					var redisPassword = hostingContext.Configuration.GetValue<string>("RedisPassword");
					var redisPrefix = hostingContext.Configuration.GetValue<string>("RedisPrefix");

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

					services.AddHostedService<CopyFilesWorkerService>();
				})
				.UseConsoleLifetime();

			await builder.RunConsoleAsync();
		}
	}
}