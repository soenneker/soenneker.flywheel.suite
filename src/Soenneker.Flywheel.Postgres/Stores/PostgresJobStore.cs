using Npgsql;
using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Postgres;

namespace Soenneker.Flywheel.Postgres;

public sealed class PostgresJobStore : LibrarianJobStore
{
    private readonly NpgsqlDataSource _source;

    public PostgresJobStore(FlywheelPostgresOptions options) : this(CreateSource(options), options)
    {
    }

    private PostgresJobStore(NpgsqlDataSource source, FlywheelPostgresOptions options)
        : base(new PostgresLibrarianDatabase(source, options.Namespace), options.HistoryRetention,
            options.RetainCompletedJobs, ownsDatabase: true, clock: token => GetServerTime(source, token))
    {
        _source = source;
    }

    private static NpgsqlDataSource CreateSource(FlywheelPostgresOptions options)
    {
        ValidateOptions(options);
        return NpgsqlDataSource.Create(options.ConnectionString);
    }

    internal static void ValidateOptions(FlywheelPostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Namespace);
        _ = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        if (options.HistoryRetention < TimeSpan.FromMinutes(5) || options.HistoryRetention > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(options.HistoryRetention));
    }

    private static async ValueTask<long> GetServerTime(NpgsqlDataSource source, CancellationToken token)
    {
        await using NpgsqlCommand command = source.CreateCommand("SELECT floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint");
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync().ConfigureAwait(false); }
        finally { await _source.DisposeAsync().ConfigureAwait(false); }
    }
}
