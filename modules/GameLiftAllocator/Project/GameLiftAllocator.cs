using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon;
using Amazon.GameLift;
using Amazon.GameLift.Model;
using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Unity.Services.CloudCode.Apis;
using Unity.Services.CloudCode.Core;
using Unity.Services.CloudCode.Apis.Matchmaker;
using IExecutionContext = Unity.Services.CloudCode.Core.IExecutionContext;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace GameLiftAllocatorModule;

/// <summary>
/// Module configuration for dependency injection.
/// Registers IGameApiClient as a singleton for accessing Unity services like Secret Manager.
/// </summary>
public class ModuleConfig : ICloudCodeSetup
{
    public void Setup(ICloudCodeConfig config)
    {
        config.Dependencies.AddSingleton(GameApiClient.Create());
        config.Dependencies.AddScoped<IGameLiftFactory, GameLiftFactory>();
    }
}

public class GameLiftAllocator(IGameApiClient gameApiClient, IGameLiftFactory gameLiftFactory, ILogger<GameLiftAllocator> logger) : IMatchmakerAllocator
{
    // Configuration - users should modify these constants for their setup
    //  private string GameSessionQueueName = "GladiatorQueue"; // TODO: Replace with actual queue name
    private int _defaultMaximumPlayerSessionCount = 100;
    private const string DefaultAwsRegion = "us-west-2";

    // Secret names - these must match the secrets stored in Unity Dashboard
    private const string AwsAccessKeyIdSecretName = "AWS_ACCESS_KEY_ID";
    private const string AwsSecretAccessKeySecretName = "AWS_SECRET_ACCESS_KEY";

    [CloudCodeFunction("Matchmaker_AllocateServer")]
    public async Task<AllocateResponse> Allocate(IExecutionContext context, AllocateRequest request)
    {
        // Determine AWS region from match properties or use default
        var region = request.MatchmakingResults.MatchProperties.GetValueOrDefault("region")?.ToString() ?? DefaultAwsRegion;
        var gameSessionQueueName = request.MatchmakingResults.QueueName;
        gameSessionQueueName += "_" + context.EnvironmentName; // Append environment name to queue name for isolation ex : GladiatorQueue_development
        logger.LogInformation("[Allocator]Using Game Session Queue Name: {GameSessionQueueName}", gameSessionQueueName);

        try
        {
            var cloudSave = gameApiClient.CloudSaveData;
            var savedData = await cloudSave.GetCustomItemsAsync(context, context.AccessToken, context.ProjectId, "queue", new List<string>() { gameSessionQueueName });
            var results = savedData.Data.Results;
            var resultValue = results[0].Value as string;
            _defaultMaximumPlayerSessionCount = int.TryParse(resultValue, out var maxPlayers) ? maxPlayers : _defaultMaximumPlayerSessionCount;
            logger.LogInformation("[Allocator]Maximum Player Session Count: {MaxPlayerSessionCount}", _defaultMaximumPlayerSessionCount);
        }
        catch (Exception e)
        {
            logger.LogError(e, "[Allocator]Failed to retrieve maximum player session count from Cloud Save, using default: {DefaultMaxPlayerSessionCount}", _defaultMaximumPlayerSessionCount);
        }


        try
        {
            // Retrieve AWS credentials from Unity Secret Manager
            var accessKeyId = await gameApiClient.SecretManager.GetSecret(context, AwsAccessKeyIdSecretName);
            var secretAccessKey = await gameApiClient.SecretManager.GetSecret(context, AwsSecretAccessKeySecretName);

            // Create GameLift client with credentials from secrets

            logger.LogInformation("[Allocator]Creating GameLift client for region {Region}", region);
            using var client = gameLiftFactory.Create(accessKeyId.Value, secretAccessKey.Value, region);

            logger.LogInformation("[Allocator]Starting game session placement for match {MatchId}", request.MatchId);
            // Serialize match data for the game server
            var gameSessionData = JsonConvert.SerializeObject(request.MatchmakingResults);

            logger.LogInformation("[Allocator]Game session data: {GameSessionData}", gameSessionData);
            // Start game session placement
            var placementRequest = new StartGameSessionPlacementRequest
            {
                PlacementId = request.MatchId, // Use matchId for idempotency
                GameSessionQueueName = gameSessionQueueName,
                MaximumPlayerSessionCount = _defaultMaximumPlayerSessionCount,
                GameSessionData = gameSessionData
            };

            logger.LogInformation("[Allocator]Sending StartGameSessionPlacement request: {@PlacementRequest}", placementRequest);
            var response = await client.StartGameSessionPlacementAsync(placementRequest);

            logger.LogInformation("[Allocator]Game session placement started: {@Response}", response);

            var placement = response.GameSessionPlacement;

            logger.LogInformation("[Allocator]Placement ID: {PlacementId}, Status: {Status}", placement.PlacementId, placement.Status.Value);

            return new AllocateResponse(AllocateStatus.Created)
            {
                AllocationData = new Dictionary<string, object>
                {
                    { "placementId", placement.PlacementId },
                    { "awsRegion", region },
                    { "startTime", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    { "matchId", request.MatchId }
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting game session placement");

            return new AllocateResponse(AllocateStatus.Error)
            {
                Message = $"Failed to start game session placement: {ex}"
            };
        }
    }

    [CloudCodeFunction("Matchmaker_PollAllocation")]
    public async Task<PollResponse> Poll(IExecutionContext context, PollRequest request)
    {
        var placementId = request.AllocationData["placementId"].ToString();
        var region = request.AllocationData["awsRegion"].ToString();

        if (region == null)
        {
            logger.LogError("Poll region was not specified in allocation data");
            return new PollResponse(PollStatus.Error)
            {
                Message = "Poll region was not specified"
            };
        }

        try
        {
            // Retrieve AWS credentials from Unity Secret Manager
            var accessKeyId = await gameApiClient.SecretManager.GetSecret(context, AwsAccessKeyIdSecretName);
            var secretAccessKey = await gameApiClient.SecretManager.GetSecret(context, AwsSecretAccessKeySecretName);

            // Create GameLift client with credentials from secrets
            logger.LogInformation("[Poll]Creating GameLift client for region {Region}", region);
            using var client = gameLiftFactory.Create(accessKeyId.Value, secretAccessKey.Value, region);

            var describeRequest = new DescribeGameSessionPlacementRequest
            {
                PlacementId = placementId
            };

            logger.LogInformation("[Poll]Describing game session placement for PlacementId: {PlacementId}", placementId);

            var response = await client.DescribeGameSessionPlacementAsync(describeRequest);
            logger.LogInformation("[Poll]DescribeGameSessionPlacement response: {@Response}", response);
            var placement = response.GameSessionPlacement;

            logger.LogInformation("[Poll]Placement ID: {PlacementId}, Status: {Status}", placement.PlacementId, placement.Status.Value);

            switch (placement.Status.Value)
            {
                case "PENDING":
                    logger.LogInformation("[Poll]Game session placement is still pending");
                    return new PollResponse(PollStatus.Pending);
                case "FULFILLED":
                    logger.LogInformation("[Poll]Game session placement fulfilled: IP {IpAddress}, Port {Port}", placement.IpAddress, placement.Port);
                    return new PollResponse(PollStatus.Allocated) { AssignmentData = AssignmentData.IpPort(placement.IpAddress, placement.Port), };
                case "TIMED_OUT":
                    logger.LogInformation("[Poll]Game session placement timed out");
                    return new PollResponse(PollStatus.Error) { Message = "Game session placement timed out" };
                case "CANCELLED":
                    logger.LogInformation("[Poll]Game session placement was cancelled");
                    return new PollResponse(PollStatus.Error) { Message = "Game session placement was cancelled" };
                case "FAILED":
                    logger.LogError("[Poll]Game session placement failed");
                    return new PollResponse(PollStatus.Error) { Message = "Game session placement failed" };
                default:
                    logger.LogWarning("Unknown game session placement status: {Status}", placement.Status.Value);
                    return new PollResponse(PollStatus.Error) { Message = $"Unknown placement status: {placement.Status.Value}" };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to describe game session placement");

            return new PollResponse(PollStatus.Error)
            {
                Message = $"Failed to describe game session placement: {ex.Message}"
            };
        }
    }
}

/// <summary>
/// Factory for creating Amazon GameLift clients.
/// </summary>
public interface IGameLiftFactory
{
    /// <summary>
    /// Creates an Amazon GameLift client with the specified credentials and region.
    /// </summary>
    /// <param name="accessKeyId">The access key ID for the AWS account.</param>
    /// <param name="secretAccessKey">The secret access key for the AWS account.</param>
    /// <param name="region">The AWS region to use.</param>
    /// <returns>An instance of <see cref="IAmazonGameLift"/>.</returns>
    IAmazonGameLift Create(string accessKeyId, string secretAccessKey, string region);
}

/// <summary>
/// Implementation of <see cref="IGameLiftFactory"/> that creates Amazon GameLift clients.
/// </summary>
public class GameLiftFactory : IGameLiftFactory
{
    /// <summary>
    /// Creates an Amazon GameLift client with the specified credentials and region.
    /// </summary>
    /// <param name="accessKeyId">The access key ID for the AWS account.</param>
    /// <param name="secretAccessKey">The secret access key for the AWS account.</param>
    /// <param name="region">The AWS region to use.</param>
    /// <returns>An instance of <see cref="IAmazonGameLift"/>.</returns>
    public IAmazonGameLift Create(string accessKeyId, string secretAccessKey, string region)
    {
        var credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
        var config = new AmazonGameLiftConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region)
        };
        return new AmazonGameLiftClient(credentials, config);
    }
}