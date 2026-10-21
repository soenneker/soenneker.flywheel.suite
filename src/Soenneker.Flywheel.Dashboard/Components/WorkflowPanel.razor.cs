using Microsoft.AspNetCore.Components;
using Soenneker.Flywheel.Dashboard.Communication;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Consumers.Abstract;

namespace Soenneker.Flywheel.Dashboard;

public partial class WorkflowPanel
{
    /// <summary>An execution whose workflow should be displayed.</summary>
    [Parameter] public string JobId { get; set; } = "";
    [Inject] private IFlywheelDashboardConsumer Consumer { get; set; } = null!;
    [Inject] private DashboardNavigationOptions Navigation { get; set; } = null!;
    private WorkflowView? _workflow;
    private string? _error, _loadedId;
    private bool _loading;
    private Task? _loop;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedId == JobId) return;
        _loadedId = JobId; _workflow = null;
        await Refresh();
        _loop ??= Poll();
    }
    private async Task Refresh()
    {
        if (_loading || IsCancellationRequested) return;
        string id = JobId;
        _loading = true;
        try
        {
            var response = await Consumer.GetWorkflow(id, CancellationToken);
            if (id != JobId) return;
            if (response.StatusCode == 404) { _workflow = null; _error = "Workflow is no longer retained."; return; }
            response.EnsureSucceeded();
            _workflow = response.Value; _error = null;
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
        catch (Exception) { _error = "Could not refresh the workflow. Displayed status may be out of date."; }
        finally { _loading = false; }
    }
    private async Task Poll()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(CancellationToken))
                await InvokeAsync(async () => { await Refresh(); StateHasChanged(); });
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
    }
    private string JobLink(string id) => Navigation.Path($"jobs/{Uri.EscapeDataString(id)}");
    private int StepNumber(string id) => _workflow?.Steps.ToList().FindIndex(s => s.Id == id) + 1 ?? 0;
    private static string ConditionLabel(ContinuationCondition condition) => condition switch
    {
        ContinuationCondition.OnSuccess => "All succeeded",
        ContinuationCondition.OnFailure => "Failure",
        ContinuationCondition.OnCancelled => "Cancellation",
        _ => "All finished"
    };
}
