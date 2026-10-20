using Microsoft.Extensions.Hosting;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Core.Stores.Librarian;

namespace Soenneker.Flywheel.Core.Logging;

internal sealed class LogArchiveService(IJobLogStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        store is LibrarianJobStore librarian
            ? librarian.ConfigureLogArchiveSchedule(cancellationToken)
            : throw new InvalidOperationException("The selected Flywheel store does not support recurring log archival.");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
