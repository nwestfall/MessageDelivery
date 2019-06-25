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
using Serilog;
using Serilog.Formatting.Json;

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
            // Load Settings
            Settings.LoadSettings();

            // Setup Logging
            var logConfiguration = new LoggerConfiguration()
                                    .MinimumLevel.Is((Serilog.Events.LogEventLevel)Settings.MinimumLoggingLevel);
            if(Settings.JsonFormatLogging)
                logConfiguration = logConfiguration.WriteTo.Console(new JsonFormatter());
            else
                logConfiguration = logConfiguration.WriteTo.Console();
            Log.Logger = logConfiguration.CreateLogger();

            Log.Information("Welcome to MessageDelivery!");
            Log.Verbose("Goal - Watch your FIFO SQS Queues and start up docker containers in ECS only when you have messages to process");
            Log.Verbose("Why? - AWS Lambda does not support FIFO queues.  In some cases, you might not get messages for hours and don't want to keep a container or other application running while you wait for these messages.");
            Log.Verbose("How? - Simple.  We get a list of all your SQS Queues in a region.  If they contain a certain tag, we will add them to our process to monitor.  Once the messages available count is greater than your desired threshold, we will start an ECS task if one isn't already running.");

            Log.Verbose("\n\nLet's get started!");

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
                    Log.Information("Getting list of queues (limit 1000)");
                    var queues = await sqsClient.ListQueuesAsync(Settings.QueuePrefix);
                    CancellationTokenSource monitorCancellationToken = new CancellationTokenSource();
                    if(queues.HttpStatusCode == HttpStatusCode.OK)
                    {
                        Log.Information($"Got {queues.QueueUrls.Count} queues");
                        var fifoQueues = queues.QueueUrls.Where(u => u.EndsWith(".fifo")).ToList();
                        Log.Debug($"{fifoQueues.Count} are going to be monitored (the rest aren't FIFO queues)");
                        foreach(var queueUrl in queues.QueueUrls)
                        {
                            MonitorQueue(queueUrl, true, monitorCancellationToken.Token);
                        }
                    }

                    Log.Information("All queues are being monitoring.  Waiting for next queue refresh.");
                    await System.Threading.Tasks.Task.Delay(Settings.QueueUrlRefreshInMinutes * 60 * 1000);
                    Log.Information("Refreshing queues...");
                    monitorCancellationToken.Cancel();
                }
                catch(Exception ex)
                {
                    Log.Error(ex, "Error while monitoring queues");
                }
            } while(true);
        }

        static void MonitorQueue(string queueUrl, bool firstPass, CancellationToken token)
        {
            Log.Information($"Starting monitor for {queueUrl}");
            System.Threading.Tasks.Task.Factory.StartNew(async () =>
            {
                try
                {
                    bool ecsStarted = false;
                    // Get name
                    string queueName = string.Empty;
                    var queueUrlParts = queueUrl.Split('/');
                    queueName = queueUrlParts[queueUrlParts.Length - 1];

                    var attributes = await _sqsClient.GetQueueAttributesAsync(queueUrl, _desiredAttributes, token).ConfigureAwait(false);
                    if(attributes.HttpStatusCode == HttpStatusCode.OK)
                    {
                        if(attributes.ApproximateNumberOfMessages >= Settings.MessageThreshold
                                && _runningECSTasks.ContainsKey(queueUrl))
                        {
                            Log.Warning($"ECS Task is already running for {queueUrl}.  Confirming...");
                            ecsStarted = true;
                            var task = _runningECSTasks[queueUrl];
                            var taskResponse = await _ecsClient.DescribeTasksAsync(new DescribeTasksRequest
                            {
                                Cluster = task.ClusterArn,
                                Tasks = new List<string>() { task.TaskArn }
                            }, token).ConfigureAwait(false);
                            if(taskResponse.HttpStatusCode == HttpStatusCode.OK)
                            {
                                if(taskResponse.Failures.Count > 0)
                                    Log.Error($"Unable to get task definition for {task.TaskArn} ({queueUrl}) - {taskResponse.Failures.FirstOrDefault().Reason}");
                                else
                                {
                                    var updatedTask = taskResponse.Tasks.FirstOrDefault();
                                    if(updatedTask != null)
                                    {
                                        if(updatedTask.DesiredStatus == "STOPPED")
                                        {
                                            _runningECSTasks.Remove(queueUrl);
                                            ecsStarted = false;
                                        }
                                        else
                                            Log.Information($"Task {task.TaskArn} is desired {updatedTask.DesiredStatus} - ({queueUrl})");
                                    }
                                    else
                                        Log.Error($"Unable to get updated task information for {task.TaskArn} ({queueUrl})");
                                }
                            }
                            else
                                Log.Error($"Unable to get a task defition for {task.TaskArn} - {taskResponse.HttpStatusCode} ({queueUrl})");;
                        }
                        else if(((attributes.ApproximateNumberOfMessages == 0
                                && attributes.ApproximateNumberOfMessagesNotVisible == 0)
                                || (attributes.ApproximateNumberOfMessages >= Settings.MessageThreshold))
                                && !_runningECSTasks.ContainsKey(queueUrl)
                                && firstPass)
                        {
                            string nextToken = null;
                            do
                            {
                                var tasksResponse = await _ecsClient.ListTasksAsync(new ListTasksRequest()
                                {
                                    Cluster = Settings.ECSClusterARN,
                                    NextToken = nextToken
                                }, token).ConfigureAwait(false);
                                if(tasksResponse.HttpStatusCode == HttpStatusCode.OK)
                                {
                                    var startTag = $"MDS-{queueName}";
                                    if(startTag.Length > 32)
                                        startTag = startTag.Substring(0, 32);
                                    foreach(var taskArn in tasksResponse.TaskArns)
                                    {
                                        var taskResponse = await _ecsClient.DescribeTasksAsync(new DescribeTasksRequest()
                                        {
                                            Cluster = Settings.ECSClusterARN,
                                            Tasks = new List<string>() { taskArn }
                                        }).ConfigureAwait(false);
                                        if(taskResponse.HttpStatusCode == HttpStatusCode.OK)
                                        {
                                            if(taskResponse.Failures.Any())
                                            {
                                                Log.Error($"Unable to get task information for {taskArn}: {taskResponse.Failures.FirstOrDefault()?.Reason}");
                                            }
                                            else
                                            {
                                                var task = taskResponse.Tasks.FirstOrDefault();
                                                if(task.StartedBy.Equals(startTag, StringComparison.InvariantCultureIgnoreCase) && task.DesiredStatus != "STOPPED")
                                                {
                                                    Log.Information("Found task running that isn't tracked (probably from previous service)");
                                                    // Found a running task for the queue while number of messages is 0
                                                    _runningECSTasks.Add(queueUrl, task);
                                                    break;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            Log.Error($"Unable to get task information for {taskArn}");
                                        }
                                    }
                                }
                                else
                                {
                                    Log.Error("Unable to get tasks from cluster");
                                }
                            }
                            while(!string.IsNullOrEmpty(nextToken) && !_runningECSTasks.ContainsKey(queueUrl));
                        }

                        if(attributes.ApproximateNumberOfMessages >= Settings.MessageThreshold
                            && !_runningECSTasks.ContainsKey(queueUrl))
                        {
                            var startTag = $"MDS-{queueName}";
                            if(startTag.Length > 32)
                                startTag = startTag.Substring(0, 32);
                            Log.Information($"Messages are available in {queueUrl}!  Starting related ECS with tag {startTag}");
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
                            var runTaskResponse = await _ecsClient.RunTaskAsync(ecsRunTaskRequest, token).ConfigureAwait(false);
                            if(runTaskResponse.HttpStatusCode == HttpStatusCode.OK)
                            {
                                if(runTaskResponse.Failures.Count > 0)
                                {
                                    Log.Error($"Unable to start task on ECS! ({queueUrl}) - {runTaskResponse.Failures.FirstOrDefault().Reason}");
                                }
                                else if(runTaskResponse.Tasks.Count > 0)
                                {
                                    Log.Information($"{runTaskResponse.Tasks.Count} tasks running for {queueUrl}");
                                    foreach(var task in runTaskResponse.Tasks)
                                    {
                                        _runningECSTasks.Add(queueUrl, task);
                                    }
                                    ecsStarted = true;
                                }
                                else
                                    Log.Error($"Unable to start task on ECS! ({queueUrl})");
                            }
                            else
                                Log.Error($"Unable to start task on ECS! ({queueUrl})");
                        }
                        else if(attributes.ApproximateNumberOfMessages == 0 
                                && attributes.ApproximateNumberOfMessagesNotVisible == 0
                                && _runningECSTasks.ContainsKey(queueUrl))
                        {
                            var task = _runningECSTasks[queueUrl];
                            Log.Information($"Messages are done processing for {queueUrl}!  Stopping Task {task.TaskArn}");
                            var stopResponse = await _ecsClient.StopTaskAsync(new StopTaskRequest()
                            {
                                Cluster = task.ClusterArn,
                                Task = task.TaskArn,
                                Reason = "Message count at 0"
                            }, token).ConfigureAwait(false);
                            if(stopResponse.HttpStatusCode == HttpStatusCode.OK)
                            {
                                Log.Information($"Task {task.TaskArn} stopped for {queueUrl}!");
                                _runningECSTasks.Remove(queueUrl);
                            }
                            else
                            {
                                Log.Error($"Unable to stop task {task.TaskArn} for {queueUrl} - {stopResponse.HttpStatusCode}");
                            }
                        }
                    }
                    
                    if(!ecsStarted)
                    {
                        Log.Debug($"Checking {queueUrl} again in {Settings.QueueMessageCountCheckIfBlankInSeconds} seconds");
                        await System.Threading.Tasks.Task.Delay(Settings.QueueMessageCountCheckIfBlankInSeconds * 1000).ConfigureAwait(false);
                        MonitorQueue(queueUrl, false, token);
                    }
                    else
                    {
                        Log.Debug($"Checking {queueUrl} again in {Settings.QueueMessageCountCheckIfActiveInMinutes} minutes");
                        await System.Threading.Tasks.Task.Delay(Settings.QueueMessageCountCheckIfActiveInMinutes * 60 * 1000).ConfigureAwait(false);
                        MonitorQueue(queueUrl, false, token);
                    }
                }
                catch(Exception ex)
                {
                    Log.Error(ex, $"Error while monitoring queue {queueUrl}");
                }
            }, token);
            Log.Information($"Done monitoring {queueUrl}");
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
            Log.Error($"An Amazon Client Exception occurred: {e.ToString()}");
        }
    }
}
