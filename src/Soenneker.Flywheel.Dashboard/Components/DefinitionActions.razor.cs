using Microsoft.AspNetCore.Components;
using Soenneker.Flywheel.Communication;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Dashboard.Consumers.Abstract;
using Soenneker.Flywheel.Dashboard.Communication;
using Soenneker.Dtos.Results.Operation;
using System.Text.Json;

namespace Soenneker.Flywheel.Dashboard;

public partial class DefinitionActions
{
    /// <summary>Definition identifier.</summary>
    [Parameter] public string Id { get; set; } = "";
    /// <summary>Whether this is a manual definition.</summary>
    [Parameter] public bool Manual { get; set; }
    /// <summary>Whether future recurring occurrences are enabled.</summary>
    [Parameter] public bool Enabled { get; set; } = true;
    /// <summary>Displayed recurring definition revision.</summary>
    [Parameter] public long Revision { get; set; }
    /// <summary>Refreshes the containing list after an operation.</summary>
    [Parameter] public EventCallback Changed { get; set; }
    [Inject] private IFlywheelDashboardConsumer Consumer { get; set; } = null!;
    [Inject] private DashboardSessionState Session { get; set; } = null!;
    private string? _error, _message, _loadedId, _description;
    private string _name = "", _payload = "{}", _policy = "{}", _kind = "interval", _cron = "", _timeZone = "UTC";
    private bool _busy, _editing, _confirmDelete, _includeSeconds, _enabled;
    private double _intervalSeconds;
    private long? _revision;

    protected override void OnParametersSet()
    {
        if (_loadedId == Id) return;
        _loadedId = Id;
        Close();
        _error = _message = null;
    }
    private void Close() { _editing = false; _confirmDelete = false; }
    private void AskDelete() { Close(); _error = _message = null; _confirmDelete = true; }
    private async Task Edit()
    {
        if (_busy) return;
        _busy = true;
        _error = _message = null;
        try
        {
            EnqueueRequest request;
            if (Manual)
            {
                var response = await Consumer.GetManualDefinition(Id, CancellationToken);
                if (!Check(response.StatusCode)) return;
                response.EnsureSucceeded();
                request = response.Value ?? throw new InvalidOperationException();
            }
            else
            {
                var response = await Consumer.GetScheduleDefinition(Id, CancellationToken);
                if (!Check(response.StatusCode)) return;
                response.EnsureSucceeded();
                var definition = response.Value!.Definition;
                request = new(definition.Name, definition.Payload, definition.Policy, TimeSpan.Zero, Description: definition.Description);
                _kind = definition.Cron is null ? "interval" : "cron";
                _cron = definition.Cron ?? "";
                _timeZone = definition.TimeZoneId;
                _includeSeconds = definition.IncludeSeconds;
                _intervalSeconds = definition.IntervalMilliseconds / 1000d;
                _enabled = definition.Enabled;
                _revision = definition.ExpectedRevision;
            }
            _name = request.Name; _payload = request.Payload; _description = request.Description;
            _policy = JsonSerializer.Serialize(request.Policy, FlywheelJsonContext.Get<JobPolicy>());
            _editing = true; _confirmDelete = false;
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
        catch (Exception) { _error = "Could not load this definition. Refresh and try again."; }
        finally { _busy = false; }
    }
    private Task Toggle() => Execute(() => Consumer.SetScheduleEnabled(Id, !Enabled, Revision, CancellationToken),
        Enabled ? "Schedule paused. Active executions continue." : "Schedule resumed.");
    private Task Delete() => Execute(() => Manual ? Consumer.DeleteManual(Id, CancellationToken) : Consumer.DeleteSchedule(Id, Revision, CancellationToken), "Definition deleted.");
    private async Task Save()
    {
        try
        {
            using var payload = JsonDocument.Parse(_payload);
            var policy = JsonSerializer.Deserialize(_policy, FlywheelJsonContext.Get<JobPolicy>()) ?? throw new JsonException();
            policy.Validate();
            if (!Manual && _kind == "interval" && (!double.IsFinite(_intervalSeconds) || _intervalSeconds < 1)) throw new ArgumentException();
            if (Manual) await Execute(() => Consumer.UpdateManual(Id, new(_name, _payload, policy, TimeSpan.Zero, Description: _description), CancellationToken), "Definition updated.");
            else await Execute(() => Consumer.UpdateSchedule(Id, new(_name, _payload, policy,
                _kind == "interval" ? checked((long)(_intervalSeconds * 1000)) : 0,
                _kind == "cron" ? _cron : null, _timeZone, _includeSeconds, _enabled, _revision, _description), CancellationToken), "Schedule updated.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or OverflowException)
        { _error = "Check the JSON, execution policy, and interval. Nothing was saved."; }
    }
    private async Task Execute(Func<ValueTask<OperationResult<object>>> action, string message)
    {
        if (_busy) return;
        _busy = true; _error = _message = null;
        try
        {
            var response = await action();
            if (!Check(response.StatusCode)) { await Changed.InvokeAsync(); return; }
            response.EnsureSucceeded();
            Close(); _message = message;
            await Changed.InvokeAsync();
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
        catch (Exception) { _error = "Action could not be confirmed. Refresh the definition before retrying."; }
        finally { _busy = false; }
    }
    private bool Check(int status)
    {
        _error = status switch
        {
            400 => "The definition is invalid. Check the payload, policy, cron expression, and time zone.",
            401 or 403 => "Sign in again to manage definitions.",
            404 => "This definition no longer exists.",
            409 => "The definition changed or cannot be edited. Reopen the editor to load the latest version.",
            501 => "This storage provider does not support definition management.",
            _ => null
        };
        if (status is 401 or 403) Session.SetAuthenticated(false);
        return status is >= 200 and < 300;
    }
}
