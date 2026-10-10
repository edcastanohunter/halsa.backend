using Halsa.Backend.Application.WorkItems.ReadModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

internal sealed class WorkItemViewConfiguration : IEntityTypeConfiguration<WorkItemView>
{
    public void Configure(EntityTypeBuilder<WorkItemView> builder)
    {
        builder.ToTable("work_items", Schemas.Default);

        builder.HasKey(view => view.Id);

        builder.Property(view => view.Title).HasMaxLength(200).IsRequired();
        builder.Property(view => view.Description).HasMaxLength(2_000);
        builder.Property(view => view.Status).HasMaxLength(50).IsRequired();

        builder.HasIndex(view => view.Status).HasDatabaseName("ix_work_items_status");
    }
}
