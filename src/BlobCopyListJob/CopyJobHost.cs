using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BlobCopyListJob
{
    public class CopyJobHost: BackgroundService
    {
	    private readonly ServiceBusClient _serviceBusClient;
	    private readonly IDatabase _database;
	    private readonly ILogger<CopyJobHost> _logger;
	    private readonly SocketsHttpHandler _socketsHandler;

	    public CopyJobHost(ServiceBusClient serviceBusClient, IDatabase database, ILogger<CopyJobHost> logger)
	    {
		    _serviceBusClient = serviceBusClient;
		    _socketsHandler = new SocketsHttpHandler
		    {
			    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
			    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
			    MaxConnectionsPerServer = 100,
			    EnableMultipleHttp2Connections = true
		    };

		    _database = database;
		    _logger = logger;
	    }

	    public async Task Do(ProcessMessageEventArgs args)
	    {
		    string sourceSas = (string)args.Message.ApplicationProperties["SourceSas"];
		    string sourceAccountName = (string)args.Message.ApplicationProperties["SourceAccountName"];
		    string sourceContainer = (string)args.Message.ApplicationProperties["SourceContainer"]; ;
		    string destinationSas = (string)args.Message.ApplicationProperties["DestinationSas"]; ;
		    string destinationContainer = (string)args.Message.ApplicationProperties["DestinationContainer"];
		    string destinationAccountName = (string)args.Message.ApplicationProperties["DestinationAccountName"];
		    string nextMarker = null;
		    string jobId = (string)args.Message.ApplicationProperties["JobId"];

		    _logger.LogInformation("Job Received");
		    
			using (var httpClient = new HttpClient(_socketsHandler, false))
			{
				var count = 0;
			    do
			    {
				    using (var request = new HttpRequestMessage(HttpMethod.Get,
					    $"https://{sourceAccountName}.blob.core.windows.net/{sourceContainer}?restype=container&marker={nextMarker}&comp=list&include=metadata&{sourceSas}")
				    )
				    {
					    nextMarker = null;
					    var response = await httpClient.SendAsync(request);
					    using (var reader = XmlReader.Create(await response.Content.ReadAsStreamAsync(),
						    new XmlReaderSettings {Async = true}))
					    {
						    var values = new List<string>();
						    while (await reader.ReadAsync())
						    {
							    if (reader.Name == "Name" && reader.NodeType == XmlNodeType.Element)
							    {
								    count++;
									await reader.ReadAsync();
								    values.Add(
									    $"{sourceAccountName}|{sourceSas}|{sourceContainer}|{destinationAccountName}|{destinationSas}|{destinationContainer}|{jobId}|{reader.Value}");
							    }

							    if (reader.Name == "NextMarker" && reader.NodeType == XmlNodeType.Element)
							    {
								    await reader.ReadAsync();
								    nextMarker = reader.Value;
							    }

							    if (values.Count > 0 && values.Count % 200 == 0)
							    {
								    _logger.LogInformation("Flushing files - Done {Count}", count);
									_database.ListRightPush($"copyjob_files",
									    values.Select(x => (RedisValue) x).ToArray(),
									    flags: CommandFlags.FireAndForget);
								    values.Clear();
							    }
						    }

						    if (values.Any())
						    {
							    _logger.LogInformation("Flushing files - Done {Count}", count);
								_database.ListRightPush($"copyjob_files", values.Select(x => (RedisValue) x).ToArray(),
								    flags: CommandFlags.FireAndForget);
						    }
					    }
				    }
					_logger.LogInformation("Next marker is {NextMarker}", nextMarker);
			    } while (!string.IsNullOrWhiteSpace(nextMarker));
		    }

			await args.CompleteMessageAsync(args.Message, args.CancellationToken);

			_logger.LogInformation("Job Done");
		}

	    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	    {
			//job source listener?
			var receiver = _serviceBusClient.CreateProcessor("copyjobs", new ServiceBusProcessorOptions()
			{
				AutoCompleteMessages = false
			});

			receiver.ProcessMessageAsync += Do;
			receiver.ProcessErrorAsync += Receiver_ProcessErrorAsync;
			await receiver.StartProcessingAsync(stoppingToken);

			await Task.Run(() =>
			{
				WaitHandle.WaitAny(new[] {stoppingToken.WaitHandle});
			}, stoppingToken);
	    }

		private Task Receiver_ProcessErrorAsync(ProcessErrorEventArgs arg)
		{
			_logger.LogError(arg.Exception, "Error");

			return Task.CompletedTask;
		}
	}
}
