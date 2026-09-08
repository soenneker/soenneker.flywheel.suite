using System.Net;
using System.Net.Http.Json;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class RouterTestHttpHandler(bool authenticated = false) : HttpMessageHandler
{
    public List<string> Paths { get; } = [];
    public bool Authenticated { get; set; } = authenticated;
    public ScheduleView Schedules { get; set; } = new([], []);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Add(request.RequestUri!.AbsolutePath);
        if (Authenticated)
        {
            string path = request.RequestUri.AbsolutePath;
            object? result = path switch
            {
                var p when p.EndsWith("/jobs/search") => new SearchResult([], 0),
                var p when p.EndsWith("/jobs/history/options") => new HistoryOptions(86400),
                var p when p.EndsWith("/jobs/history") => new List<JobHistoryPoint>(),
                var p when p.EndsWith("/jobs/schedules") => Schedules,
                var p when p.EndsWith("/servers") => new List<ServerView>(),
                _ => null
            };
            return Task.FromResult(result is null ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
