using Microsoft.EntityFrameworkCore;
using PathologyOrders.Models;

namespace PathologyOrders.Data;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options)
    : DbContext(options)
{
    public DbSet<PathologyOrder> PathologyOrders =>
        Set<PathologyOrder>();

    public DbSet<Hl7OutboundMessage> Hl7OutboundMessages =>
        Set<Hl7OutboundMessage>();

    public DbSet<Hl7OutboundMessageOrder> Hl7OutboundMessageOrders =>
        Set<Hl7OutboundMessageOrder>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigurePathologyOrder(modelBuilder);
        ConfigureHl7OutboundMessage(modelBuilder);
        ConfigureHl7OutboundMessageOrder(modelBuilder);
    }

    private static void ConfigurePathologyOrder(
        ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<PathologyOrder>();

        order.ToTable("PathologyOrders");

        order.HasKey(x => x.Id);

        order.HasIndex(x => x.FhirId)
            .IsUnique();

        order.HasIndex(x => x.IdentifierValue);

        order.HasIndex(x => x.RequisitionGroupNumber);

        order.HasIndex(x => x.RequisitionNumber);

        order.HasIndex(x => new
        {
            x.LastQueuedAtUtc,
            x.FhirLastUpdated
        });

        order.Property(x => x.FhirId)
            .HasMaxLength(200)
            .IsRequired();

        order.Property(x => x.IdentifierSystem)
            .HasMaxLength(500);

        order.Property(x => x.IdentifierValue)
            .HasMaxLength(500);

        order.Property(x => x.Status)
            .HasMaxLength(100);

        order.Property(x => x.Intent)
            .HasMaxLength(100);

        order.Property(x => x.OrderCode)
            .HasMaxLength(500);

        order.Property(x => x.OrderCodeSystem)
            .HasMaxLength(500);

        order.Property(x => x.PatientReference)
            .HasMaxLength(500);

        order.Property(x => x.EncounterReference)
            .HasMaxLength(500);

        order.Property(x => x.RequesterReference)
            .HasMaxLength(500);

        order.Property(x => x.RequisitionGroupNumber)
            .HasMaxLength(500);

        order.Property(x => x.RequisitionNumber)
            .HasMaxLength(500);

        order.Property(x => x.RawJson)
            .IsRequired();
    }

    private static void ConfigureHl7OutboundMessage(
        ModelBuilder modelBuilder)
    {
        var outbound =
            modelBuilder.Entity<Hl7OutboundMessage>();

        outbound.ToTable("Hl7OutboundMessages");

        outbound.HasKey(x => x.Id);

        outbound.HasIndex(x => x.MessageControlId)
            .IsUnique();

        // Used by the outbound worker to locate due messages.
        outbound.HasIndex(x => new
        {
            x.Status,
            x.NextAttemptAtUtc
        });

        outbound.HasIndex(x => x.CreatedAtUtc);

        outbound.Property(x => x.MessageControlId)
            .HasMaxLength(100)
            .IsRequired();

        outbound.Property(x => x.MessageType)
            .HasMaxLength(20)
            .IsRequired();

        outbound.Property(x => x.TriggerEvent)
            .HasMaxLength(20)
            .IsRequired();

        outbound.Property(x => x.Hl7Version)
            .HasMaxLength(20)
            .IsRequired();

        outbound.Property(x => x.OrderControl)
            .HasMaxLength(20)
            .IsRequired();

        outbound.Property(x => x.Payload)
            .IsRequired();

        outbound.Property(x => x.Status)
            .HasConversion<int>()
            .IsRequired();

        outbound.Property(x => x.AcknowledgementCode)
            .HasMaxLength(10);

        outbound.Property(x => x.AcknowledgementText)
            .HasMaxLength(2000);

        outbound.Property(x => x.AcknowledgementPayload);

        outbound.Property(x => x.LastError);
    }

    private static void ConfigureHl7OutboundMessageOrder(
        ModelBuilder modelBuilder)
    {
        var messageOrder =
            modelBuilder.Entity<Hl7OutboundMessageOrder>();

        messageOrder.ToTable("Hl7OutboundMessageOrders");

        messageOrder.HasKey(x => new
        {
            x.Hl7OutboundMessageId,
            x.PathologyOrderId
        });

        messageOrder.HasIndex(x => x.PathologyOrderId);

        messageOrder
            .HasOne(x => x.Hl7OutboundMessage)
            .WithMany(x => x.Orders)
            .HasForeignKey(x => x.Hl7OutboundMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        messageOrder
            .HasOne(x => x.PathologyOrder)
            .WithMany(x => x.OutboundMessageOrders)
            .HasForeignKey(x => x.PathologyOrderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}