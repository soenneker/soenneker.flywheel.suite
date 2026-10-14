namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>Special schedule expressions understood by Flywheel.</summary>
public static class JobSchedule
{
    /// <summary>Persists a manual-only recurring job with no automatic execution or next run.</summary>
    public const string None = "none";
}
