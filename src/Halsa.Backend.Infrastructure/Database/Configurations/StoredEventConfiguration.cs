using Halsa.Backend.Infrastructure.EventSourcing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

internal sealed class StoredEventConfiguration : IEntityTypeConfiguration<StoredEvent>
{
    public void Configure(EntityTypeBuilder<StoredEvent> builder)
    {
        builder.ToTable("events", Schemas.EventStore);

        builder.HasKey(storedEvent => storedEvent.Sequence);

        builder.Property(storedEvent => storedEvent.Sequence).ValueGeneratedOnAdd();

        builder.Property(storedEvent => storedEvent.StreamId).HasMaxLength(200).IsRequired();
        builder.Property(storedEvent => storedEvent.StreamType).HasMaxLength(100).IsRequired();
        builder.Property(storedEvent => storedEvent.EventType).HasMaxLength(200).IsRequired();

        builder.Property(storedEvent => storedEvent.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(storedEvent => storedEvent.Metadata).HasColumnType("jsonb");

        // Optimistic concurrency for the whole write model: two writers cannot claim one version.
        builder.HasIndex(storedEvent => new { storedEvent.StreamId, storedEvent.Version })
            .IsUnique()
            .HasDatabaseName("ix_events_stream_id_version");

        builder.HasIndex(storedEvent => storedEvent.StreamType)
            .HasDatabaseName("ix_events_stream_type");
    }
}
