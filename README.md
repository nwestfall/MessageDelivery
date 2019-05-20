# Message Delivery

TODO: Icons

MessageDelivery is designed to help manage messages across many FIFO SQS in AWS.  Sometimes you have a use case where you need many difference queues, all while being FIFO.  If you want to do this with a serverless approach, it's currently not possible.  Running Lambda from a message in SQS is only supported if the queue is not FIFO.  This project is meant to run in ECS/EKS (or really wherever, it is just a docker image) and manage the processing of messages across all these queues by spinning up other containers in ECS (future support for EKS is being considered).  This allows you to have many different queues, but not require leaving on a bunch of docker images if you don't need to.

## How it works
You configure the `MessageDelivery` docker image to support your AWS environment.  You can do this by using environment variables when starting the image.

Once the image is running, it will monitor up to 1000 FIFO SQS queues.  If at any time the message threshold is higher than what you have configured (default: 1), then it will spin up a docker container in ECS using the task definition and cluster you have defined.  It will then check back into this queue later.  Once the queue reaches 0 messages and 0 messages in flight, it will stop the container in ECS.

## Configuration
All the configuration is currently setup with Environment Variables.  This container is designed to run in ECS, so this should be an easy 1 tmie setup.

 - `MD_AWS_KEY` (required) - AWS Key
 - `MD_AWS_SECRET` (required) - AWS Secret
 - `MD_AWS_REGION` (default: us-east-1) - AWS Region
 - `MD_QUEUE_PREFIX` (default: "") - If you would only want to monitor FIFO queues with a certain prefix, define that prefix here
 - `MD_MESSAGE_THRESHOLD` (default: 1) - The number of messages that need to be in the queue before starting an ECS task
 - `MD_QUEUE_URL_REFRESH` (default: 30 minutes) - The number of minutes between getting a new list of queues.  This should be set very high unless you expect to be creating/deleting queues often
 - `MD_QUEUE_BLANK_MESSAGE_CHECK` (default: 30 seconds) - The number of seconds until you recheck the queue for message
 - `MD_QUEUE_ACTIVE_MESSAGE_REFRESH` (default: 5 minutes) - The number of minutes until you recheck if all the messages have been reprocessed
 - `MD_ECS_CLUSTER_ARN` (required) - The AWS ECS ARN for the cluster you want to run the tasks in
 - `MD_ECS_TASK_ARN` (required) - The AWS ECS Task ARN for the task definition you want to run when the message threshold is reached
 - `MD_ECS_TASK_LAUNCH` (default: EC2) - The launch type for the task definition (can be `EC2` or `Fargate`)
 - `MD_ECS_TASK_CONTAINER_ENVIRONMENT` (optional) - A json object (example below) of environment variables you want to set when the task is started
 - `MD_ECS_TASK_CONTAINER_NAME` (required only if `MD_ECS_TASK_CONTAINER_ENVIRONMENT` is set) - The name of the container you want to override with different environment variables 

## Environment Variable Example
You can override the list of environment variables for a task when a queue triggers it to start.  This can be helpful if you have settings specific to the queue.

Let's look at an example

```
AWS_LOGGING_GROUP_NAME="##MessageDelivery.Tag.appName##":DB_CONNECTION_STRING="Server=127.0.0.1;Database=##MessageDelivery.Tag.dbName##;Integrated Security=true;"
```

We read in the value, using the following format

`ENV_VARIABLE=VALUE:OTHER_ENV_VARIABLE=OTHER_VALUE`

If your `VALUE` contains `:`, please be sure to wrap the value in quotes (`"VAL:UE"`)

This example shows how to pull `Tags` from the queue and use them as or in your environment variables.  You can do this by defining `##MessageDelivery.Tag.YOUR_TAG_KEY##` anywhere in the value.  Note that it is case-sensitive, including the tag key.

**If the tag cannot be found, it will put a blank string as the default value**

## Example
I have an EC2 ECS cluster running in AWS.  I then created a task definition for `MessageDelivery` with my configuration.  I run this definition as a `Service` inside the cluster.  This way, `MessageDelivery` is always running and will start other tasks inside the cluster when appropiate.

![AWS Example](example.gif)

## Future Development
 - Have a web-based dashboard to see what you are monitoring and when tasks are started/stopped
 - Send notifications to SNS when a task is started/stopped or an exception occurs
 - Support more "dynamic" variables when using task overrides
 - Option to send logs to CloudWatch, including user-defined verbosity to better debug problems and provide auditing
