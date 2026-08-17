using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Orders.UnitTests;

public sealed class NPlusOneDetectionInterceptorTests
{
    private sealed class Widget
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
    }

    private sealed class WidgetContext(DbContextOptions<WidgetContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().Property(w => w.Id).ValueGeneratedNever();
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    private static WidgetContext CreateContext(RecordingLogger logger, int threshold)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseSqlite(connection)
            .AddInterceptors(new NPlusOneDetectionInterceptor(logger, threshold))
            .Options;

        var context = new WidgetContext(options);
        context.Database.EnsureCreated();
        for (var i = 0; i < 10; i++)
        {
            context.Widgets.Add(new Widget { Id = i, ParentId = i % 3 });
        }
        context.SaveChanges();
        return context;
    }

    [Fact]
    public void FlagsRepeatedQueryShapeAsPossibleNPlusOne()
    {
        var logger = new RecordingLogger();
        using var context = CreateContext(logger, threshold: 5);
        logger.Messages.Clear();

        for (var parentId = 0; parentId < 6; parentId++)
        {
            _ = context.Widgets.Where(w => w.ParentId == parentId).ToList();
        }

        Assert.Contains(logger.Messages, m => m.Contains("Possible N+1", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlagASingleJoinStyleQuery()
    {
        var logger = new RecordingLogger();
        using var context = CreateContext(logger, threshold: 5);
        logger.Messages.Clear();

        _ = context.Widgets.Where(w => w.ParentId < 6).ToList();

        Assert.Empty(logger.Messages);
    }
}
