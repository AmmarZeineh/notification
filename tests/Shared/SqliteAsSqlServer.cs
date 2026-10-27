using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Notifications.Infrastructure.Persistence.Configurations;

namespace Notifications.Tests.Shared;

/// <summary>
/// Makes in-memory SQLite behave like SQL Server where the feature depends on it. Shared by the test
/// projects (linked, not referenced) so both run the same database emulation.
/// </summary>
internal static class SqliteAsSqlServer
{
    /// <summary>
    /// SQLite has no SQL Server collations. Registers the case-sensitive collation as an exact comparison,
    /// which is what the binary collation does on SQL Server.
    /// </summary>
    public static void UseSqlServerCaseSensitiveCollation(this SqliteConnection connection) =>
        connection.CreateCollation(DeviceTokenConfiguration.CaseSensitiveCollation, string.CompareOrdinal);

    /// <summary>
    /// SQLite cannot sort or compare <see cref="DateTimeOffset"/> columns (SQL Server can), so they are stored
    /// as sortable numbers. Only the test model changes; the production model and migrations are untouched.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseSqliteAsSqlServer<TContext>(
        this DbContextOptionsBuilder<TContext> options, SqliteConnection connection)
        where TContext : DbContext =>
        options.UseSqlite(connection).ReplaceService<IModelCustomizer, SortableDateTimeOffsetCustomizer>();

    private sealed class SortableDateTimeOffsetCustomizer(ModelCustomizerDependencies dependencies)
        : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);

            var dateTimeOffsets = modelBuilder.Model.GetEntityTypes()
                .SelectMany(entity => entity.GetProperties())
                .Where(property => property.ClrType == typeof(DateTimeOffset));

            foreach (var property in dateTimeOffsets)
            {
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
            }
        }
    }
}
