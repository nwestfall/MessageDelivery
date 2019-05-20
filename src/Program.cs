using System;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.SQS.Model;

namespace MessageDelivery
{
    class Program
    {
        const string TAG_REGEX = @"##MessageDelivery.Tag.(\w+)##";

        static IAmazonSQS _sqsClient;

        static IAmazonECS _ecsClient;

        static IDictionary<string, Amazon.ECS.Model.Task> _runningECSTasks = new Dictionary<string, Amazon.ECS.Model.Task>();

        static readonly List<string> _desiredAttributes = new List<string> { "ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible" };

        static async System.Threading.Tasks.Task Main(string[] args)
        {
            Console.WriteLine("Welcome to MessageDelivery!");
            Console.WriteLine("Goal - Watch your FIFO SQS Queues and start up docker containers in ECS only when you have messages to process");
            Console.WriteLine("Why? - AWS Lambda does not support FIFO queues.  In some cases, you might not get messages for hours and don't want to keep a container or other application running while you wait for these messages.");
            Console.WriteLine("How? - Simple.  We get a list of all your SQS Queues in a region.  If they contain a certain tag, we will add them to our process to monitor.  Once the messages available count is greater than your desired threshold, we will start an ECS task if one isn't already running.");

            Console.WriteLine("\n\nLet's get started!");

            // Load Settings
            Settings.LoadSettings();

            // Wire up ECS
            var ecsClient = new AmazonECSClient(Settings.AWSKey, Settings.AWSSecret, Settings.AWSRegion);
            ecsClient.ExceptionEvent += AmazonClient_ExceptionEvent;
            _ecsClient = ecsClient;

            // Wire up SQS
            var sqsClient = new AmazonSQSClient(Settings.AWSKey, Settings.AWSSecret, Settings.AWSRegion);
            sqsClient.ExceptionEvent += AmazonClient_ExceptionEvent;
            _sqsClient = sqsClient;

            do
            {
                try
                {
                    Console.WriteLine("Getting list of queues (limit 1000)");
                    var queues = await sqsClient.ListQueuesAsync(Settings.QueuePrefix);
                    CancellationTokenSource monitorCancellationToken = new CancellationTokenSource();
                    if(queues.HttpStatusCode == HttpStatusCode.OK)
                    {
                        Console.WriteLine($"Got {queues.QueueUrls.Count} queues");
                        var fifoQueues = queues.QueueUrls.Where(u => u.EndsWith(".fifo")).ToList();
                        Console.WriteLine($"{fifoQueues.Count} are going to be monitored (the rest aren't FIFO queues)");
                        foreach(var queueUrl in queues.QueueUrls)
                        {
                            MonitorQueue(queueUrl, monitorCancellationToken.Token);
                        }
                    }

                    Console.WriteLine("All queues are being monitoring.  Waiting for next queue refresh.");
                    await System.Threading.Tasks.Task.Delay(Settings.QueueUrlRefreshInMinutes * 60 * 1000);
                    Console.WriteLine("Refreshing queues...");
                    monitorCancellationToken.Cancel();
                }
                catch(Exception ex)
                {
                    Console.WriteLine($"Error while monitoring queues: {ex.Message}");
                }
            } while(true);
        }

        static void MonitorQueue(string queueUrl, CancellationToken token)
        {
            Console.WriteLine($"Starting monitor for {queueUrl}");
            System.Threading.Tasks.Task.Factory.StartNew(async () =>
            {
                try
                {
                    bool ecsStarted = false;
                    var attributes = await _sqsClient.GetQueueAttributesAsync(queueUrl, _desiredAttributes, token);
                    if(attributes.HttpStatusCode == HttpStatusCode.OK)
                    {
                        if(attributes.ApproximateNumberOfMessages >= Settings.MessageThreshold
                            && !_runningECSTasks.ContainsKey(queueUrl))
                        {
                            var startTag = $"MDS-{DateTime.UtcNow.Ticks}";
                            Console.WriteLine($"Messages are available in {queueUrl}!  Starting related ECS with tag {startTag}");
                            var ecsRunTaskRequest = new RunTaskRequest();
                            ecsRunTaskRequest.Cluster = Settings.ECSClusterARN;
                            ecsRunTaskRequest.Count = 1; // I would hope since it's a FIFO item, you want just 1 running.  Open to configuration tho
                            ecsRunTaskRequest.LaunchType = Settings.ECSTaskLaunchType;
                            ecsRunTaskRequest.StartedBy = startTag;
                            ecsRunTaskRequest.TaskDefinition = Settings.ECSTaskDefinitionARN;
                            // If we have environment variable overrides, set in the run task overrides
                            if(Settings.ECSTaskEnvironmentVariableOverride.Count > 0)
                            {
                                var taskOverride = new TaskOverride();
                                var containerOverride = new ContainerOverride();
                                containerOverride.Name = Settings.ECSTaskOverrideContainerName;
                                // get tags for queue
                                var queueTags = await _sqsClient.ListQueueTagsAsync(new ListQueueTagsRequest() { QueueUrl = queueUrl }, token);
                                foreach(var environmentVariableOverride in Settings.ECSTaskEnvironmentVariableOverride)
                                {
                                    containerOverride.Environment.Add(new Amazon.ECS.Model.KeyValuePair()
                                    {
                                       Name = environmentVariableOverride.Key,
                                       Value = ParseAndBuildEnvironmentVariable(environmentVariableOverride.Value, queueTags) 
                                    });
                                }
                                taskOverride.ContainerOverrides = new List<ContainerOverride>() { containerOverride };
                                ecsRunTaskRequest.Overrides = taskOverride;
                            }
                            var runTaskResponse = await _ecsClient.RunTaskAsync(ecsRunTaskRequest, token);
                            if(runTaskResponse.HttpStatusCode == HttpStatusCode.OK)
                            {
                                if(runTaskResponse.Failures.Count > 0)
                                {
                                    Console.WriteLine($"Unable to start task on ECS! ({queueUrl}) - {runTaskResponse.Failures.FirstOrDefault().Reason}");
                                }
                                else if(runTaskResponse.Tasks.Count > 0)
                                {
                                    Console.WriteLine($"{runTaskResponse.Tasks.Count} tasks running for {queueUrl}");
                                    foreach(var task in runTaskResponse.Tasks)
                                    {
                                        _runningECSTasks.Add(queueUrl, task);
                                    }
                                    ecsStarted = true;
                                }
                                else
                                    Console.WriteLine($"Unable to start task on ECS! ({queueUrl})");
                            }
                            else
                                Console.WriteLine($"Unable to start task on ECS! ({queueUrl})");
                        }
                        else if(attributes.ApproximateNumberOfMessages >= Settings.MessageThreshold
                                && _runningECSTasks.ContainsKey(queueUrl))
                        {
                            Console.WriteLine($"ECS Task is allready running for {queueUrl}");
                            ecsStarted = true;
                        }
                        else if(attributes.ApproximateNumberOfMessages == 0 
                                && attributes.ApproximateNumberOfMessagesNotVisible == 0
                                && _runningECSTasks.ContainsKey(queueUrl))
                        {
                            var task = _runningECSTasks[queueUrl];
                            Console.WriteLine($"Messages are done processing for {queueUrl}!  Stopping Task {task.TaskArn}");
                            var stopResponse = await _ecsClient.StopTaskAsync(new StopTaskRequest()
                            {
                                Cluster = task.ClusterArn,
                                Task = task.TaskArn,
                                Reason = "Message count at 0"
                            });
                            if(stopResponse.HttpStatusCode == HttpStatusCode.OK)
                            {
                                Console.WriteLine($"Task {task.TaskArn} stopped for {queueUrl}!");
                                _runningECSTasks.Remove(queueUrl);
                            }
                            else
                            {
                                Console.WriteLine($"Unable to stop task {task.TaskArn} for {queueUrl} - {stopResponse.HttpStatusCode}");
                            }
                        }
                    }
                    
                    if(!ecsStarted)
                    {
                        Console.WriteLine($"Checking {queueUrl} again in {Settings.QueueMessageCountCheckIfBlankInSeconds} seconds");
                        await System.Threading.Tasks.Task.Delay(Settings.QueueMessageCountCheckIfBlankInSeconds * 1000);
                        MonitorQueue(queueUrl, token);
                    }
                    else
                    {
                        Console.WriteLine($"Checking {queueUrl} again in {Settings.QueueMessageCountCheckIfActiveInMinutes} minutes");
                        await System.Threading.Tasks.Task.Delay(Settings.QueueMessageCountCheckIfActiveInMinutes * 60 * 1000);
                        MonitorQueue(queueUrl, token);
                    }
                }
                catch(Exception ex)
                {
                    Console.WriteLine($"Error while monitoring queue {queueUrl}: {ex.Message}");
                }
            }, token);
            Console.WriteLine($"Done monitoring {queueUrl}");
        }

        static string ParseAndBuildEnvironmentVariable(string environmentVariable, ListQueueTagsResponse queueTags)
        {
            if(!environmentVariable.Contains("##MessageDelivery"))
                return environmentVariable;
            // Get Tag matches
            var tagMatches = Regex.Matches(environmentVariable, TAG_REGEX);
            foreach(var tagMatch in tagMatches.AsEnumerable())
            {
                if(tagMatch.Groups.Count == 2)
                {
                    var desiredTagKey = tagMatch.Groups[1].Value;
                    if(queueTags.Tags.ContainsKey(desiredTagKey))
                    {
                        environmentVariable = environmentVariable.Replace(tagMatch.Value, queueTags.Tags[desiredTagKey]);
                    }
                }
            }
            // TODO Support other matching patterns

            return environmentVariable;
        }

        static void AmazonClient_ExceptionEvent(object sender, ExceptionEventArgs e)
        {
            Console.WriteLine($"An Amazon Client Exception occurred: {e.ToString()}");
        }
    }
}
