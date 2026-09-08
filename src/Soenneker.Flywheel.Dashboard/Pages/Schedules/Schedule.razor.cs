using Microsoft.AspNetCore.Components;
using Soenneker.Dtos.Results.Operation;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication;

namespace Soenneker.Flywheel.Dashboard.Pages.Schedules;

public partial class Schedule
{
    /// <summary>Identifier of the recurring schedule selected by the route.</summary>
    [Parameter] public string ScheduleId { get; set; } = "";

    /// <summary>Schedule supplied by the list when displaying its detail drawer.</summary>
    [Parameter] public RecurringScheduleView? Value { get; set; }

    /// <summary>Displays details inside the list's drawer without loading another copy of its data.</summary>
    [Parameter] public bool Embedded { get; set; }

    /// <summary>Whether the containing list is already starting this schedule.</summary>
    [Parameter] public bool Running { get; set; }

    /// <summary>Delegates run requests to the containing list when supplied.</summary>
    [Parameter] public EventCallback<string> RunRequested { get; set; }

    private bool _running;
    private string? _runError;
    private RecurringScheduleView? SelectedSchedule => Embedded ? Value :
        _schedules?.Recurring.FirstOrDefault(schedule => schedule.Id == ScheduleId);

    protected override Task OnInitializedAsync() => Embedded ? Task.CompletedTask : base.OnInitializedAsync();

    protected override void OnParametersSet() => _runError = null;

    private async Task Run()
    {
        if (_running || Running || SelectedSchedule is not { } schedule) return;
        if (RunRequested.HasDelegate)
        {
            await RunRequested.InvokeAsync(schedule.Id);
            return;
        }
        _running = true;
        _runError = null;
        try
        {
            OperationResult<StartedJob> response = await Consumer.RunRecurring(schedule.Id, CancellationToken);
            if (response.StatusCode == 401) { Session.SetAuthenticated(false); return; }
            if (response.StatusCode == 404) { _runError = "This recurring schedule no longer exists."; return; }
            response.EnsureSucceeded();
            if (response.Value is not { Id.Length: > 0 } job) throw new InvalidOperationException("Missing execution ID.");
            await Sonner.Success("Job queued", cancellationToken: CancellationToken);
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
        catch (Exception) { _runError = "The run could not be confirmed. Check recent job activity before trying again."; }
        finally { _running = false; }
    }
}
