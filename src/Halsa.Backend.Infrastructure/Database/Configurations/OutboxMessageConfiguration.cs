using Halsa.Backend.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", Schemas.Outbox);

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Type).HasMaxLength(300).IsRequired();
        builder.Property(message => message.Content).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.Error).HasColumnType("text");

        // Partial index: the dispatcher only ever scans unprocessed rows.
        builder.HasIndex(message => message.OccurredOnUtc)
            .HasDatabaseName("ix_outbox_messages_unprocessed")
            .HasFilter("processed_on_utc IS NULL");
    }
}
