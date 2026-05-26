using System.Diagnostics;
using System.Text;
using PlayerWallet.Api.Kafka;

namespace PlayerWallet.Tests.Component.Kafka;

/// <summary>Trace-context propagation contract tests against <see cref="KafkaWalletEventPublisher.BuildHeaders"/> without a real broker; asserts consumers parse <c>traceparent</c>/<c>tracestate</c> back to the producer's ids.</summary>
public sealed class KafkaWalletEventPublisherTests
{
    [Fact]
    public void BuildHeaders_Injects_Traceparent_From_Active_Span()
    {
        using var listener = StartListening();
        using var source = new ActivitySource("test-source");
        using var activity = source.StartActivity("publish wallet.events", ActivityKind.Producer);
        Assert.NotNull(activity);

        var headers = KafkaWalletEventPublisher.BuildHeaders(activity);

        var traceparentHeader = headers.Single(h => h.Key == "traceparent");
        var traceparent = Encoding.UTF8.GetString(traceparentHeader.GetValueBytes());
        Assert.StartsWith("00-", traceparent);
        Assert.Contains(activity.TraceId.ToHexString(), traceparent);
        Assert.Contains(activity.SpanId.ToHexString(), traceparent);
    }

    [Fact]
    public void BuildHeaders_Includes_TraceState_When_Present()
    {
        using var listener = StartListening();
        using var source = new ActivitySource("test-source");
        using var activity = source.StartActivity("publish wallet.events", ActivityKind.Producer);
        Assert.NotNull(activity);
        activity.TraceStateString = "tenant=demo";

        var headers = KafkaWalletEventPublisher.BuildHeaders(activity);

        var tracestateHeader = headers.Single(h => h.Key == "tracestate");
        Assert.Equal("tenant=demo", Encoding.UTF8.GetString(tracestateHeader.GetValueBytes()));
    }

    [Fact]
    public void BuildHeaders_Omits_TraceState_When_Absent()
    {
        using var listener = StartListening();
        using var source = new ActivitySource("test-source");
        using var activity = source.StartActivity("publish wallet.events", ActivityKind.Producer);
        Assert.NotNull(activity);

        var headers = KafkaWalletEventPublisher.BuildHeaders(activity);

        Assert.DoesNotContain(headers, h => h.Key == "tracestate");
    }

    [Fact]
    public void BuildHeaders_Empty_When_No_ActiveSpan()
    {
        var headers = KafkaWalletEventPublisher.BuildHeaders(activity: null);
        Assert.Empty(headers);
    }

    [Fact]
    public void Traceparent_Roundtrips_Into_ConsumerSide_Parent_Context()
    {
        using var listener = StartListening();
        using var source = new ActivitySource("test-source");
        using var producerActivity = source.StartActivity("publish wallet.events", ActivityKind.Producer);
        Assert.NotNull(producerActivity);

        var headers = KafkaWalletEventPublisher.BuildHeaders(producerActivity);
        var traceparent = Encoding.UTF8.GetString(headers.Single(h => h.Key == "traceparent").GetValueBytes());

        Assert.True(
            ActivityContext.TryParse(traceparent, traceState: null, out var parsed),
            "Consumer side must be able to parse the traceparent header.");

        Assert.Equal(producerActivity.TraceId, parsed.TraceId);
        Assert.Equal(producerActivity.SpanId, parsed.SpanId);
    }

    private static ActivityListener StartListening()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
