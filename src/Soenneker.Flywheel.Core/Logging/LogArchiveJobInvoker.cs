using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Logging;

internal sealed class LogArchiveJobInvoker : IJobInvoker
{
    internal const string JobName = "Flywheel.LogArchive.Export";
    public string Name => JobName;
    public MethodPolicy Policy => new() { MaxConcurrency = 1 };

    public async Task Invoke(IServiceProvider services, string payload, CancellationToken cancellationToken)
    {
        var options = services.GetRequiredService<LogArchiveOptions>();
        if (!options.Enabled) return;
        var queue = services.GetRequiredService<IJobLogStore>() as IJobLogArchiveQueue ??
            throw new InvalidOperationException("The selected Flywheel store does not support log archival.");
        LogArchiveClaim? claim = await queue.ClaimLogArchive(cancellationToken);
        if (claim is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.ExportTimeout);
        await services.GetRequiredService<IJobLogArchive>().Save(claim.File, deadline.Token);
        await queue.CompleteLogArchive(claim, cancellationToken);
    }
}
