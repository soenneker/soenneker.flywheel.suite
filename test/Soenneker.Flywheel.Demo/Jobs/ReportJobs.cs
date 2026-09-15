using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Demo.Requests;

namespace Soenneker.Flywheel.Demo.Jobs;

public sealed class ReportJobs(ILogger<ReportJobs> logger, IJobProgress progress)
{
    [FlywheelJob("demo.reports.generate.v1")]
    public async ValueTask Generate(ReportRequest report, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting report: {Title}.", report.Title);
        await progress.Report(0, "Starting report", cancellationToken);
        for (var section = 1; section <= report.Sections; section++)
        {
            await Task.Delay(1000, cancellationToken);
            await progress.Report(section * 100d / report.Sections, $"Rendered section {section}/{report.Sections}", cancellationToken);
            logger.LogInformation("Rendered section {Section}/{Total} ({Percent}%).", section, report.Sections, section * 100 / report.Sections);
        }
        logger.LogInformation("Report complete. Simulated output is ready.");
    }

    [FlywheelJob("demo.reports.delayed.v1")]
    public ValueTask Delayed(ReportRequest report, CancellationToken cancellationToken) => Generate(report, cancellationToken);
}
