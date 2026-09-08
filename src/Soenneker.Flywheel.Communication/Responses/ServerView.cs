namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A live Flywheel worker server and its currently leased jobs.</summary>
public sealed record ServerView(string Id, long ExpiresAt, int Workers, List<JobView> RunningJobs);
