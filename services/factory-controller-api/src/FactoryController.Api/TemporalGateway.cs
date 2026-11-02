using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FactoryController.Api;

public sealed class TemporalUnavailableException(Exception inner)
    : Exception("Temporal is unavailable", inner);

/// <summary>The only Temporal operations the API needs; faked in unit tests.</summary>
public interface ITemporalGateway
{
    /// <summary>Starts the workflow; returns false when the delivery was already recorded.</summary>
    Task<bool> StartForgejoWorkflowAsync(
        string workflowId, ForgejoEnvelope envelope, CancellationToken cancellationToken);

    Task CheckReadyAsync(CancellationToken cancellationToken);
}

public sealed class TemporalGateway(ITemporalClient client, ApiSettings settings) : ITemporalGateway
{
    public async Task<bool> StartForgejoWorkflowAsync(
        string workflowId, ForgejoEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            await client.StartWorkflowAsync(
                "ForgejoEventWorkflow",
                [envelope],
                new WorkflowOptions(workflowId, settings.TemporalTaskQueue)
                {
                    IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
                    IdConflictPolicy = WorkflowIdConflictPolicy.Fail,
                    Rpc = new RpcOptions
                    {
                        Timeout = TimeSpan.FromSeconds(3),
                        CancellationToken = cancellationToken,
                    },
                });
            return true;
        }
        catch (WorkflowAlreadyStartedException)
        {
            return false;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            throw new TemporalUnavailableException(error);
        }
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.Connection.WorkflowService.DescribeNamespaceAsync(
                new DescribeNamespaceRequest { Namespace = settings.TemporalNamespace },
                new RpcOptions
                {
                    Timeout = TimeSpan.FromSeconds(2),
                    CancellationToken = cancellationToken,
                });
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            throw new TemporalUnavailableException(error);
        }
    }

    private static bool IsUnavailable(Exception error) =>
        error is RpcException or TimeoutException or InvalidOperationException;
}
