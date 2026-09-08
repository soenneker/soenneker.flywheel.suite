using Microsoft.AspNetCore.Components;
using Soenneker.Dtos.Results.Operation;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication;
using Soenneker.Flywheel.Dashboard.Consumers.Abstract;
using Soenneker.Lepton.Suite;

namespace Soenneker.Flywheel.Dashboard.Pages.Shared;

/// <summary>Shares recurring and scheduled job data from the layout's live subscription.</summary>
public abstract class JobsPageBase : LeptonCancellable
{
    [Inject] protected IFlywheelDashboardConsumer Consumer { get; set; } = null!;
    [Inject] private DashboardBoardConnection BoardConnection { get; set; } = null!;
    [Inject] private DashboardSessionState Session { get; set; } = null!;
    protected ScheduleView? _schedules;
    protected string? _scheduleError;

    protected override async Task OnInitializedAsync()
    {
        BoardConnection.Snapshot += OnSnapshot;
        if (BoardConnection.Latest is { } snapshot)
        {
            ApplySchedules(snapshot.Schedules);
            return;
        }
        try
        {
            OperationResult<ScheduleView> response = await Consumer.GetSchedules(CancellationToken);
            if (response.StatusCode == 401) { Session.SetAuthenticated(false); return; }
            if (response.StatusCode == 501) { ApplySchedules(null); return; }
            response.EnsureSucceeded();
            if (BoardConnection.Latest is null) ApplySchedules(response.Value);
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
        catch (Exception) { _scheduleError = "Unable to load schedules. Reload the page to retry."; }
    }

    private Task OnSnapshot(LiveBoard snapshot) => InvokeAsync(() =>
    {
        if (IsDisposed || IsCancellationRequested) return;
        ApplySchedules(snapshot.Schedules);
        StateHasChanged();
    });

    private void ApplySchedules(ScheduleView? schedules)
    {
        _schedules = schedules;
        _scheduleError = schedules is null ? "Schedule information is unavailable from this job store." : null;
        SchedulesChanged();
    }

    protected virtual void SchedulesChanged() { }

    public override async ValueTask DisposeAsync()
    {
        BoardConnection.Snapshot -= OnSnapshot;
        await base.DisposeAsync();
    }
}
