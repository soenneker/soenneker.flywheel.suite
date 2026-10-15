using Microsoft.AspNetCore.Components;
using Soenneker.Dtos.Results.Operation;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Dashboard.Communication;
using Soenneker.Flywheel.Dashboard.Consumers.Abstract;
using Soenneker.Quark;

namespace Soenneker.Flywheel.Dashboard;

public partial class JobActions
{
    /// <summary>Execution targeted by queue actions, or the latest execution of a definition.</summary>
    [Parameter] public string? Id { get; set; }
    /// <summary>Job name used for the accessible menu label.</summary>
    [Parameter] public string Name { get; set; } = "job";
    /// <summary>Current execution display state.</summary>
    [Parameter] public string? State { get; set; }
    /// <summary>Whether cancellation has already been requested.</summary>
    [Parameter] public bool CancelRequested { get; set; }
    /// <summary>Disables the menu while its parent is performing an action.</summary>
    [Parameter] public bool Disabled { get; set; }
    /// <summary>Shows creation of a fresh execution for a manual or recurring definition.</summary>
    [Parameter] public bool IsDefinition { get; set; }
    /// <summary>Starts a fresh execution for a definition, or overrides running a completed job again.</summary>
    [Parameter] public EventCallback RunRequested { get; set; }
    /// <summary>Refreshes the containing view after a mutation or stale-state conflict.</summary>
    [Parameter] public EventCallback Changed { get; set; }

    [Inject] private IFlywheelDashboardConsumer Consumer { get; set; } = null!;
    [Inject] private ISonnerService Sonner { get; set; } = null!;
    [Inject] private DashboardSessionState Session { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private DashboardNavigationOptions DashboardNavigation { get; set; } = null!;
    private bool _busy;

    private Task Queue(JobQueueAction action) => Execute(
        () => Consumer.UpdateQueue(Id!, action, CancellationToken), action switch
        {
            JobQueueAction.MoveToTop => "Moved to top of queue",
            JobQueueAction.MoveToBottom => "Moved to bottom of queue",
            _ => "Job is ready to run"
        });

    private Task Cancel() => Execute(() => Consumer.CancelJob(Id!, CancellationToken), "Cancellation requested");

    private async Task Execute(Func<ValueTask<OperationResult<object>>> operation, string message)
    {
        if (_busy || Disabled || Id is null) return;
        _busy = true;
        try
        {
            OperationResult<object> response = await operation();
            if (!await CheckStatus(response.StatusCode)) return;
            response.EnsureSucceeded();
            await Sonner.Success(message, cancellationToken: CancellationToken);
            await Changed.InvokeAsync();
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
        catch (Exception) { await Sonner.Error("Action could not be confirmed. Refresh the job before retrying.", cancellationToken: CancellationToken); }
        finally { _busy = false; }
    }

    private async Task Run()
    {
        if (_busy || Disabled) return;
        _busy = true;
        try
        {
            if (RunRequested.HasDelegate) await RunRequested.InvokeAsync();
            else if (!IsDefinition && Id is not null)
            {
                var response = await Consumer.RunAgain(Id, CancellationToken);
                if (!await CheckStatus(response.StatusCode)) return;
                response.EnsureSucceeded();
                if (response.Value is not { Id.Length: > 0 } execution) throw new InvalidOperationException("Missing execution ID.");
                Navigation.NavigateTo(DashboardNavigation.Path($"jobs/{Uri.EscapeDataString(execution.Id)}"));
            }
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
        catch (Exception) { await Sonner.Error("The run could not be confirmed. Check recent activity before retrying.", cancellationToken: CancellationToken); }
        finally { _busy = false; }
    }

    private async Task<bool> CheckStatus(int status)
    {
        if (status is 401 or 403) { Session.SetAuthenticated(false); return false; }
        string? message = status switch
        {
            404 => "This execution no longer exists.",
            409 => "The job's state changed. Refresh to see its available actions.",
            501 => "This action is not supported for this job or storage provider.",
            _ => null
        };
        if (message is null) return true;
        await Sonner.Error(message, cancellationToken: CancellationToken);
        if (status is 404 or 409) await Changed.InvokeAsync();
        return false;
    }

    private void ViewExecution() => Navigation.NavigateTo(DashboardNavigation.Path($"jobs/{Uri.EscapeDataString(Id!)}"));
}
