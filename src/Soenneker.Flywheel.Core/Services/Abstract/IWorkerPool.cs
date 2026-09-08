namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Controls the number of job workers running on the current host.</summary>
public interface IWorkerPool
{
    /// <summary>Gets the desired number of workers. Workers retiring from active jobs are not included.</summary>
    int WorkerCount { get; }

    /// <summary>Changes the worker count. Added workers start immediately; removed workers finish their current job before retiring.</summary>
    ValueTask SetWorkerCount(int count, CancellationToken cancellationToken = default);
}
