using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Domain.DeviceTokens;

namespace Notifications.Infrastructure.Persistence.Configurations;

public sealed class DeviceTokenConfiguration : IEntityTypeConfiguration<DeviceToken>
{
    /// <summary>
    /// FCM tokens and user ids are compared case-sensitively in code, but SQL Server's default collation
    /// compares text case-insensitively. A binary collation keeps lookups, deletes and the unique index exact,
    /// so the database and the ownership checks in code never disagree (e.g. "alice" vs "Alice").
    /// </summary>
    public const string CaseSensitiveCollation = "Latin1_General_100_BIN2";

    public void Configure(EntityTypeBuilder<DeviceToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DeviceTokens");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.UserId)
            .HasMaxLength(DeviceToken.UserIdMaxLength)
            .UseCollation(CaseSensitiveCollation)
            .IsRequired();

        builder.Property(d => d.Token)
            .HasMaxLength(DeviceToken.TokenMaxLength)
            .UseCollation(CaseSensitiveCollation)
            .IsRequired();

        builder.Property(d => d.Platform)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.LastSeenAt).IsRequired();

        // A token identifies one app installation, so it can only belong to one user at a time.
        builder.HasIndex(d => d.Token).IsUnique();

        // Covers the send-to-user query (a user's most recently seen tokens) with one ordered seek, no key
        // lookups or sort; the leading UserId also serves every other per-user lookup.
        builder.HasIndex(d => new { d.UserId, d.LastSeenAt })
            .IsDescending(false, true)
            .IncludeProperties(d => d.Token);
    }
}
