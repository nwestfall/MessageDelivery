using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Amazon;
using Amazon.ECS;

namespace MessageDelivery
{
    internal static class Settings
    {
        public static string AWSKey { get; private set; }

        public static string AWSSecret { get; private set; }

        public static RegionEndpoint AWSRegion { get; private set; } = RegionEndpoint.USEast1;

        public static int MinimumLoggingLevel { get; private set; } = 3;

        public static bool JsonFormatLogging { get; private set; } = false;

        public static int MessageThreshold { get; private set; } = 1;

        public static string QueuePrefix { get; private set; } = string.Empty;

        public static int QueueUrlRefreshInMinutes { get; private set; }= 30;

        public static int QueueMessageCountCheckIfBlankInSeconds { get; private set; } = 30;

        public static int QueueMessageCountCheckIfActiveInMinutes { get; private set; } = 5;
        
        public static string QueueTagToSkip { get; private set; } = string.Empty;

        public static string ECSClusterARN { get; private set; }

        public static string ECSTaskDefinitionARN { get; private set; }

        public static LaunchType ECSTaskLaunchType { get; private set; }= LaunchType.EC2;

        public static string ECSTaskOverrideContainerName { get; private set; }

        public static IReadOnlyDictionary<string, string> ECSTaskEnvironmentVariableOverride { get; private set; }= new Dictionary<string, string>();

        public static void LoadSettings()
        {
            AWSKey = Environment.GetEnvironmentVariable("MD_AWS_KEY") ?? throw new ArgumentNullException("MD_AWS_KEY is required");
            AWSSecret = Environment.GetEnvironmentVariable("MD_AWS_SECRET") ?? throw new ArgumentNullException("MD_AWS_SECRET is required");
            var awsRegion = Environment.GetEnvironmentVariable("MD_AWS_REGION");
            if(!string.IsNullOrEmpty(awsRegion))
                AWSRegion = RegionEndpoint.GetBySystemName(awsRegion);
            if(int.TryParse(Environment.GetEnvironmentVariable("MD_MIN_LOG_LEVEL"), out int minimumLoggingLevel))
                MinimumLoggingLevel = minimumLoggingLevel;
            if(bool.TryParse(Environment.GetEnvironmentVariable("MD_JSON_LOGGING"), out bool jsonLogging))
                JsonFormatLogging = jsonLogging;
            QueuePrefix = Environment.GetEnvironmentVariable("MD_QUEUE_PREFIX") ?? string.Empty;
            if(int.TryParse(Environment.GetEnvironmentVariable("MD_MESSAGE_THRESHOLD"), out int messageThreshold))
                MessageThreshold = messageThreshold;
            if(int.TryParse(Environment.GetEnvironmentVariable("MD_QUEUE_URL_REFRESH"), out int queueUrlRefresh))
                QueueUrlRefreshInMinutes = queueUrlRefresh;
            if(int.TryParse(Environment.GetEnvironmentVariable("MD_QUEUE_BLANK_MESSAGE_CHECK"), out int queueBlankMessageCheck))
                QueueMessageCountCheckIfBlankInSeconds = queueBlankMessageCheck;
            if(int.TryParse(Environment.GetEnvironmentVariable("MD_QUEUE_ACTIVE_MESSAGE_REFRESH"), out int queueActiveMessageRefresh))
                QueueMessageCountCheckIfActiveInMinutes = queueActiveMessageRefresh;
            QueueTagToSkip = Environment.GetEnvironmentVariable("MD_QUEUE_TAG_TO_SKIP");
            ECSClusterARN = Environment.GetEnvironmentVariable("MD_ECS_CLUSTER_ARN") ?? throw new ArgumentNullException("MD_ECS_CLUSTER_ARN is required");
            ECSTaskDefinitionARN = Environment.GetEnvironmentVariable("MD_ECS_TASK_ARN") ?? throw new ArgumentNullException("MD_ECS_TASK_ARN is required");;
            var launchType = Environment.GetEnvironmentVariable("MD_ECS_TASK_LAUNCH");
            if(!string.IsNullOrEmpty(launchType))
                ECSTaskLaunchType = launchType;
            var environmentVariableOverrides = Environment.GetEnvironmentVariable("MD_ECS_TASK_CONTAINER_ENVIRONMENT");
            var containerName = Environment.GetEnvironmentVariable("MD_ECS_TASK_CONTAINER_NAME");
            if(!string.IsNullOrEmpty(containerName))
                ECSTaskOverrideContainerName = containerName;
            if(string.IsNullOrEmpty(containerName) && !string.IsNullOrEmpty(environmentVariableOverrides))
                throw new ArgumentNullException("MD_ECS_TASK_CONTAINER_NAME is required since MD_ECS_TASK_CONTAINER_ENVIRONMENT is set");
            
            // Parse environment variable
            if(!string.IsNullOrEmpty(environmentVariableOverrides))
                ParseEnvironmentVariables(environmentVariableOverrides);
        }

        static void ParseEnvironmentVariables(string environmentVariableOverrides)
        {
            // ? Format - ENV_VARIABLE=VALUE:OTHER_ENV_VARIABLE=OTHER_VALUE
            var environmentDictionary = new Dictionary<string, string>();
            var parts = Regex.Matches(environmentVariableOverrides, "'(.+?)'|[^:]+");
            if(parts.Count == 0)
                throw new ArgumentException("MD_ECS_TASK_CONTAINER_ENVIRONMENT is formatted incorrectly");
            for(var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var variables = Regex.Matches(GetCorrectMatch(ref part), "\"(.+?)\"|[^=]+");
                if(variables.Count > 2)
                    throw new ArgumentException("MD_ECS_TASK_CONTAINER_ENVIRONMENT is formatted incorrectly");
                var key = variables[0];
                var val = variables[1];
                if(!environmentDictionary.TryAdd(GetCorrectMatch(ref key), GetCorrectMatch(ref val)))
                    throw new ArgumentException("Environment Variable already defined in overrides");
            }

            ECSTaskEnvironmentVariableOverride = environmentDictionary;
        }

        static string GetCorrectMatch(ref Match match)
        {
            var matchString = match.Value;
            if(match.Groups.Count == 2)
            {
                if(!string.IsNullOrEmpty(match.Groups[1].Value))
                    matchString = match.Groups[1].Value;
            }

            return matchString;
        }
    }
}