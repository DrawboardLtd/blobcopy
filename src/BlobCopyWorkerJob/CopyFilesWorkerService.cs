using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;


namespace BlobCopyWorkerJob
{
	public class CopyFilesWorkerService: BackgroundService
    {
	    private readonly IDatabase _database;
	    private readonly IHostApplicationLifetime _hostingLifetime;
	    private readonly ILogger<CopyFilesWorkerService> _logger;
	    private readonly ConcurrentDictionary<string, JobTally> _memoryCache;

	    public CopyFilesWorkerService(IDatabase database, IHostApplicationLifetime hostingLifetime, ILogger<CopyFilesWorkerService> logger)
	    {
		    _database = database;
		    _hostingLifetime = hostingLifetime;
		    _logger = logger;
		    _memoryCache = new ConcurrentDictionary<string, JobTally>();
	    }

	    public class JobTally
	    {
		    public int Success, Fail;

		    public DateTime LastUpdate;
	    }

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			try
			{
				var socketsHandler = new SocketsHttpHandler
				{
					PooledConnectionLifetime = TimeSpan.FromMinutes(10),
					PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
					MaxConnectionsPerServer = 100,
					EnableMultipleHttp2Connections = true
				};

				var copyRequests = new List<Task>();
				var activeCopyRequests = 0;
				var filesProcessed = 0L;
				var filesRead = 0L;
				var continuesHit = 0L;

				//cache clean up loop
				//also flush any final job tally
				var task = Task.Run(async () =>
				{
					do
					{
						_logger.LogInformation("{filesProcessed} {filesRead} {continuesHit}", filesProcessed, filesRead, continuesHit);

						//foreach (var ca in _memoryCache.ToArray())
						//{
						//	_logger.LogInformation("{key} @{tally}", ca.Key, ca.Value);
						//}

						var keys = _memoryCache.ToArray()
							.Where(x => x.Value.LastUpdate < DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(1)))
							.Select(x => x.Key)
							.ToArray();

						foreach (var key in keys)
						{
							if (_memoryCache.TryRemove(key, out var tally))
							{
								var noSuccess = Interlocked.Exchange(ref tally.Success, 0);
								var noFails = Interlocked.Exchange(ref tally.Fail, 0);
								_logger.LogInformation("Flushing and Removing " + key + " " + noSuccess + " " + noFails);

								_database.HashIncrement(key, "success", noSuccess, flags: CommandFlags.FireAndForget);
								_database.HashIncrement(key, "failure", noFails, flags: CommandFlags.FireAndForget);
							}
						}

						await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
					} while (!stoppingToken.IsCancellationRequested);
				});


				while (!stoppingToken.IsCancellationRequested)
				{
					var files = (string[]) await _database.ScriptEvaluateAsync(
						@"
local commands = redis.call('lrange', KEYS[1], 0, 499);
local resultSize = table.getn(commands);
redis.call('ltrim', KEYS[1], resultSize, -1)
return commands;", new[] {(RedisKey) "copyjob_files"});

					if (files.Length == 0)
					{
						if (copyRequests.Count > 0)
						{
							await Task.WhenAll(copyRequests);
						}
						else
						{
							await Task.Delay(50, stoppingToken);
						}

						continue;
					}

					filesRead += files.Length;

					foreach (var file in files)
					{
						//sourceaccounts|sourceSas|sourceContainer|destinationaccount|desinationsas|destinationContainer|jobid|blobName|lastModified
						var fileParts = file.Split('|');
						var sourceAccount = fileParts[0];
						var sourceSas = fileParts[1];
						var sourceContainer = fileParts[2];
						var destinationAccount = fileParts[3];
						var destinationSas = fileParts[4];
						var destinationContainer = fileParts[5];
						var jobId = fileParts[6];
						var blobName = fileParts[7];
						var lastModified = fileParts[8];
						var originalEntry = file;

						var fileTask = Task.Run(async () =>
						{
							using var httpClient2 = new HttpClient(socketsHandler, false);
							var fileMessage = new HttpRequestMessage(HttpMethod.Put,
								new Uri(
									$"https://{destinationAccount}.blob.core.windows.net/{sourceContainer}/{blobName}?{destinationSas}"));
							fileMessage.Headers.Add("x-ms-copy-source",
								$"https://{sourceAccount}.blob.core.windows.net/{destinationContainer}/{blobName}?{sourceSas}");
							fileMessage.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R"));
							fileMessage.Headers.Add("If-Unmodified-Since", lastModified);
							return (file, await httpClient2.SendAsync(fileMessage, stoppingToken));
						}, stoppingToken);

						var ct = fileTask.ContinueWith((task) =>
						{
							Interlocked.Increment(ref continuesHit);
							Interlocked.Decrement(ref activeCopyRequests);
							if (task.IsCompletedSuccessfully && (task.Result.Item2.IsSuccessStatusCode
							                                     || task.Result.Item2.StatusCode ==
							                                     HttpStatusCode
								                                     .PreconditionFailed //file exists already with same modified date
							                                     || task.Result.Item2.StatusCode ==
							                                     HttpStatusCode
								                                     .Conflict //theres already a request to copy this file in play
								))
							{
								var tally = _memoryCache.GetOrAdd(jobId, (_) => new JobTally());
								Interlocked.Increment(ref tally.Success);
								tally.LastUpdate = DateTime.UtcNow;
							}
							else
							{
								_logger.LogError(task.Exception,
									(task.Exception?.Message ?? task.Result.Item2.ReasonPhrase ?? "Unknown failure") +
									" - " +
									task.Result.file);
								var tally = _memoryCache.GetOrAdd(jobId, (_) => new JobTally());
								Interlocked.Increment(ref tally.Fail);
								tally.LastUpdate = DateTime.UtcNow;
								//_database.ListRightPush("copyjob_files", originalEntry,
								//	flags: CommandFlags.FireAndForget);
							}

							copyRequests.Remove(fileTask);
						}, TaskContinuationOptions.None);

						copyRequests.Add(ct);

						Interlocked.Increment(ref activeCopyRequests);
						filesProcessed++;

						if (filesProcessed % 250 == 0)
						{
							foreach (var tally in _memoryCache.ToArray())
							{
								var val = tally.Value;
								var noSuccess = Interlocked.Exchange(ref val.Success, 0);
								var noFails = Interlocked.Exchange(ref val.Fail, 0);

								_logger.LogInformation("Flushing " + tally.Key + " " + noSuccess + " " + noFails);
								_database.HashIncrement(tally.Key, "success", noSuccess,
									flags: CommandFlags.FireAndForget);
								_database.HashIncrement(tally.Key, "failure", noFails,
									flags: CommandFlags.FireAndForget);
							}

							//_logger.LogInformation("Processed {filesProcessed} files", filesProcessed);
							filesProcessed = 0;
						}

						while (activeCopyRequests > socketsHandler.MaxConnectionsPerServer)
						{
							await Task.Delay(50, stoppingToken);
						}
					}
				}

				_hostingLifetime.StopApplication();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Unhandled failure.");
			}
		}
    }
}
