using System;
using System.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Amazon.CloudWatchLogs;
using AWS.Logger.AspNetCore;
using AWS.Logger;
using Amazon.Runtime;

namespace MessageDelivery.TestQueueProcessor
{
    class Program
    {
        static ManualResetEvent _quitEvent = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);

            var serviceProvider = serviceCollection.BuildServiceProvider();

            var listener = serviceProvider.GetService<Listener>();

            Console.CancelKeyPress += (sender, eArgs) => {
                listener.StopListening();
				_quitEvent.Set();
				eArgs.Cancel = true;
			};

            _quitEvent.WaitOne();

            serviceProvider.GetService<ILogger>().LogInformation("Shutting down!");
        }

        static void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(configure =>
            {
                configure.SetMinimumLevel(LogLevel.Trace);
                configure.AddConsole();
                configure.AddProvider(new AWSLoggerProvider(new AWSLoggerConfig()
                                        {
                                            LogGroup = Environment.GetEnvironmentVariable("AWS_LOG_GROUP"),
                                            Region = Environment.GetEnvironmentVariable("AWS_REGION"),
                                            Credentials = new BasicAWSCredentials(Environment.GetEnvironmentVariable("AWS_KEY"), Environment.GetEnvironmentVariable("AWS_SECRET"))
                                        }, LogLevel.Trace));
            }).AddSingleton<Listener>();
        }
    }
}
