namespace Collections.Core.Entities;

/// <summary>A record that belongs to exactly one business workspace (tenant).</summary>
public interface ITenantOwned
{
    int BusinessId { get; set; }
}

/// <summary>A record with UTC audit timestamps maintained by the persistence layer.</summary>
public interface IHasTimestamps
{
    DateTimeOffset CreatedAtUtc { get; set; }
    DateTimeOffset UpdatedAtUtc { get; set; }
}
