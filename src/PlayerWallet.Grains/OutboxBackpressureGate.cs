namespace PlayerWallet.Grains;

/// <summary>
/// Shared back-pressure state for the wallet outbox. The drainer publishes a periodic pending-row count here; the wallet grain calls <see cref="ShouldRejectNewWrites"/> before each mutation and returns OutboxFull when the cap is exceeded.
/// Default cap is generous (100,000 rows). Override via the <c>Wallet:OutboxCap</c> configuration key.
/// </summary>
public sealed class OutboxBackpressureGate
{
    public const int DefaultCap = 100_000;

    private long _pending;
    private readonly long _cap;

    public OutboxBackpressureGate(int cap = DefaultCap)
    {
        _cap = cap;
    }

    public long PendingCount => Interlocked.Read(ref _pending);

    public long Cap => _cap;

    public bool ShouldRejectNewWrites => PendingCount >= _cap;

    public void Update(long pending)
    {
        Interlocked.Exchange(ref _pending, pending);
    }
}
