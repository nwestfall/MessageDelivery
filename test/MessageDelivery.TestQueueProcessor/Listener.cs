using System;
using System.Net;
using System.Threading.Tasks;
using System.Threading;

using Microsoft.Extensions.Logging;
using Amazon.SQS;

namespace MessageDelivery.TestQueueProcessor
{
    public class Listener
    {
        readonly ILogger<Listener> _logger;

        readonly IAmazonSQS _amazonSQSClient;

        CancellationTokenSource cts;

        public Listener(ILogger<Listener> logger)
        {
            _logger = logger;

            _amazonSQSClient = new AmazonSQSClient(Environment.GetEnvironmentVariable("AWS_KEY"), Environment.GetEnvironmentVariable("AWS_SECRET"));
            PrintEnvironmentVariables();
            cts = new CancellationTokenSource();
            StartListening(cts.Token);
        }

        void PrintEnvironmentVariables()
        {
            _logger.LogTrace("Printing Environment Variables");
            _logger.LogTrace($"AWS_QUEUE_NAME={Environment.GetEnvironmentVariable("AWS_QUEUE_NAME")}");
            _logger.LogTrace($"AWS_LOG_GROUP={Environment.GetEnvironmentVariable("AWS_LOG_GROUP")}");
            _logger.LogTrace($"AWS_REGION={Environment.GetEnvironmentVariable("AWS_REGION")}");
            _logger.LogTrace("Done printing environment variables");
        }
        
        void StartListening(CancellationToken ct)
        {
            Task.Factory.StartNew(async () => 
            {
                var queueName = Environment.GetEnvironmentVariable("AWS_QUEUE_NAME");
                var queueResponse = await _amazonSQSClient.GetQueueUrlAsync(queueName);
                if(queueResponse.HttpStatusCode == HttpStatusCode.OK)
                {
                    _logger.LogDebug($"Starting to listen to {queueResponse.QueueUrl}");
                    do
                    {
                        var receiveMessageResponse = await _amazonSQSClient.ReceiveMessageAsync(queueResponse.QueueUrl, ct);
                        if(receiveMessageResponse.HttpStatusCode == HttpStatusCode.OK)
                        {
                            if(receiveMessageResponse.Messages.Count > 0)
                            {
                                _logger.LogInformation($"{receiveMessageResponse.Messages.Count} message(s) received!");
                                foreach(var message in receiveMessageResponse.Messages)
                                {
                                    _logger.LogInformation($"Message ID - {message.MessageId}");
                                    _logger.LogInformation($"Message Body - {message.Body}");

                                    _logger.LogTrace("Deleting message");
                                    await _amazonSQSClient.DeleteMessageAsync(queueResponse.QueueUrl, message.ReceiptHandle, ct);
                                }
                            }
                            else
                                _logger.LogInformation("No new messages");
                        }
                        else
                            _logger.LogError("Unable to receive messages");
                    }
                    while(!ct.IsCancellationRequested);
                }
                else
                    _logger.LogCritical("Unable to find QueueURL");
            }, ct);
        }

        public void StopListening() =>
            cts.Cancel();
    }
}