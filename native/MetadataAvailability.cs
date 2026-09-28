using System;
using System.Net.Http;

namespace GameLibrary.Native;

public sealed class MetadataUnavailableException : HttpRequestException
{
    public DateTimeOffset RetryAt { get; }
    public MetadataUnavailableException(DateTimeOffset retryAt) : base("Metadata service is temporarily unavailable; retry after " + retryAt.ToLocalTime().ToString("T") + ". Existing metadata is retained.") => RetryAt = retryAt;
}

public sealed class MetadataAvailability
{
    private readonly Func<DateTimeOffset> utcNow;
    private int failures;
    public DateTimeOffset RetryAt { get; private set; }
    public MetadataAvailability(Func<DateTimeOffset>? utcNow = null) => this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    public bool Waiting => utcNow() < RetryAt;
    public void RequireAvailable() { if (Waiting) throw new MetadataUnavailableException(RetryAt); }
    public void Succeeded() { failures = 0; RetryAt = default; }
    public MetadataUnavailableException Failed()
    {
        failures = Math.Min(failures + 1, 5);
        RetryAt = utcNow().AddSeconds(Math.Min(300, 30 * (1 << (failures - 1))));
        return new MetadataUnavailableException(RetryAt);
    }
}
