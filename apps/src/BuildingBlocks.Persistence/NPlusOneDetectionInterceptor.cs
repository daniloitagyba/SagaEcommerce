using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks;

/// <summary>
/// Guardrail: warns when the same query shape executes an unusual number
/// of times within a DbContext's lifetime - the runtime signature of an
/// N+1. EF Core already parameterizes literal values, so counting exact
/// `CommandText` repeats needs no normalization. A fresh interceptor
/// instance per DbContext means counts naturally reset per request.
/// </summary>
public sealed class NPlusOneDetectionInterceptor(ILogger logger, int threshold = 5) : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<string, int> _commandCounts = new();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Track(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Track(command.CommandText);
        return ValueTask.FromResult(result);
    }

    private void Track(string commandText)
    {
        var count = _commandCounts.AddOrUpdate(commandText, 1, (_, existing) => existing + 1);
        if (count == threshold)
        {
            NPlusOneLog.PossibleNPlusOne(logger, count, commandText);
        }
    }
}

internal static partial class NPlusOneLog
{
    [LoggerMessage(
        EventId = 9900,
        Level = LogLevel.Warning,
        Message = "Possible N+1 query pattern: the same query shape has executed {Count} times within one scope - consider Include()/a join instead of a per-item query. SQL: {Sql}")]
    public static partial void PossibleNPlusOne(ILogger logger, int count, string sql);
}

public static class NPlusOneDetectionExtensions
{
    /// <summary>
    /// Wires the N+1 detector into a DbContext's options. Call from the
    /// (serviceProvider, options) overload of AddDbContext so a logger can
    /// be resolved from DI.
    /// </summary>
    public static DbContextOptionsBuilder AddNPlusOneDetection(
        this DbContextOptionsBuilder builder,
        ILoggerFactory loggerFactory,
        int threshold = 5)
    {
        var logger = loggerFactory.CreateLogger("BuildingBlocks.NPlusOneDetection");
        return builder.AddInterceptors(new NPlusOneDetectionInterceptor(logger, threshold));
    }
}
