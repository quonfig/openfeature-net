using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using Quonfig.Sdk;
using Quonfig.Sdk.Exceptions;
using Xunit;
using Reason = OpenFeature.Constant.Reason;

namespace Quonfig.OpenFeature.ServerProvider.Tests;

/// <summary>
/// qfg-9p4i.12: the B.5 provider status mapping from
/// project/plans/2026-10-05-init-failure-design.md. Delivery is faked with an
/// <see cref="HttpMessageHandler"/> that answers 503 until <see cref="FakeDelivery.Heal"/>.
/// </summary>
public sealed class InitFailureTests
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(15);

    private static QuonfigProviderOptions Options(FakeDelivery delivery, OnInitFailure mode) => new()
    {
        SdkKey = "openfeature-net-init-failure-tests",
        OnInitFailure = mode,
        InitTimeout = TimeSpan.FromMilliseconds(300),
        ConfigureClient = o =>
        {
            o.ApiUrls = new[] { FakeDelivery.Url };
            o.StreamUrls = new[] { FakeDelivery.Url };
            o.HttpMessageHandler = delivery;
            o.FallbackPollThreshold = TimeSpan.FromMilliseconds(200);
            o.FallbackPollInterval = TimeSpan.FromMilliseconds(200);
            o.CollectEvaluationSummaries = false;
            o.ContextUploadMode = ContextUploadMode.None;
        },
    };

    [Fact]
    public async Task Fail_InitializeThrows_KeepsClientAlive_AndEmitsReadyOnHeal()
    {
        using var delivery = new FakeDelivery();
        var provider = new QuonfigProvider(Options(delivery, OnInitFailure.Fail));

        Func<Task> init = () => provider.InitializeAsync(EvaluationContext.Empty);
        await init.Should().ThrowAsync<QuonfigInitException>();

        (await NextEventAsync(provider)).Type.Should().Be(ProviderEventTypes.ProviderError);
        provider.GetClient().Should().NotBeNull("the provider keeps the client alive so it can heal");

        var before = await provider.ResolveBooleanValueAsync("some.flag", true);
        before.Value.Should().BeTrue();
        before.ErrorType.Should().Be(ErrorType.ProviderNotReady);
        before.Reason.Should().Be(Reason.Error);

        delivery.Heal();

        (await NextEventAsync(provider)).Type.Should().Be(ProviderEventTypes.ProviderReady);
        var after = await provider.ResolveBooleanValueAsync("some.flag", true);
        after.ErrorType.Should().Be(ErrorType.FlagNotFound, "the healed client now evaluates against live config");

        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task Continue_NoCache_ResolvesThenEmitsError_AndReadyOnHeal()
    {
        using var delivery = new FakeDelivery();
        var provider = new QuonfigProvider(Options(delivery, OnInitFailure.Continue));

        await provider.InitializeAsync(EvaluationContext.Empty);

        (await NextEventAsync(provider)).Type.Should().Be(ProviderEventTypes.ProviderError,
            "continue without config serves code defaults, which B.5 reports as ERROR, not READY");

        var before = await provider.ResolveStringValueAsync("some.flag", "fallback");
        before.Value.Should().Be("fallback");
        before.ErrorType.Should().Be(ErrorType.ProviderNotReady);

        delivery.Heal();

        (await NextEventAsync(provider)).Type.Should().Be(ProviderEventTypes.ProviderReady);
        (await provider.ResolveStringValueAsync("some.flag", "fallback")).ErrorType.Should().Be(ErrorType.FlagNotFound);

        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task OpenFeatureApi_Fail_StatusErrorThenReadyOnHeal()
    {
        using var delivery = new FakeDelivery();
        var provider = new QuonfigProvider(Options(delivery, OnInitFailure.Fail));
        var domain = "init-failure-fail-" + Guid.NewGuid();

        await Api.Instance.SetProviderAsync(domain, provider);
        var client = Api.Instance.GetClient(domain);
        await WaitForStatusAsync(client, ProviderStatus.Error);

        var details = await client.GetBooleanDetailsAsync("some.flag", true);
        details.Value.Should().BeTrue();
        details.ErrorType.Should().Be(ErrorType.ProviderNotReady);

        delivery.Heal();
        await WaitForStatusAsync(client, ProviderStatus.Ready);
        (await client.GetBooleanDetailsAsync("some.flag", true)).ErrorType.Should().Be(ErrorType.FlagNotFound);

        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task OpenFeatureApi_Continue_StatusErrorThenReadyOnHeal()
    {
        using var delivery = new FakeDelivery();
        var provider = new QuonfigProvider(Options(delivery, OnInitFailure.Continue));
        var domain = "init-failure-continue-" + Guid.NewGuid();

        await Api.Instance.SetProviderAsync(domain, provider);
        var client = Api.Instance.GetClient(domain);
        await WaitForStatusAsync(client, ProviderStatus.Error);

        // Hold ERROR: the deferred PROVIDER_ERROR must land after the OF SDK's own post-init READY.
        await Task.Delay(500);
        client.ProviderStatus.Should().Be(ProviderStatus.Error);

        delivery.Heal();
        await WaitForStatusAsync(client, ProviderStatus.Ready);

        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task Wait_InitializeBlocksUntilConfig_ThenReady()
    {
        using var delivery = new FakeDelivery();
        var provider = new QuonfigProvider(Options(delivery, OnInitFailure.Wait));

        var init = provider.InitializeAsync(EvaluationContext.Empty);
        await Task.Delay(700);
        init.IsCompleted.Should().BeFalse("wait keeps blocking past InitTimeout");

        delivery.Heal();
        await init.WaitAsync(EventWait);

        (await NextEventAsync(provider)).Type.Should().Be(ProviderEventTypes.ProviderReady);
        await provider.ShutdownAsync();
    }

    private static async Task<ProviderEventPayload> NextEventAsync(QuonfigProvider provider)
    {
        using var cts = new CancellationTokenSource(EventWait);
        var item = await provider.GetEventChannel().Reader.ReadAsync(cts.Token);
        return item.Should().BeOfType<ProviderEventPayload>().Subject;
    }

    private static async Task WaitForStatusAsync(FeatureClient client, ProviderStatus status)
    {
        var deadline = DateTime.UtcNow + EventWait;
        while (client.ProviderStatus != status && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        client.ProviderStatus.Should().Be(status);
    }

    /// <summary>Fake delivery: 503 everywhere until <see cref="Heal"/>, then an empty envelope on
    /// /api/v2/configs (the stream stays down, so the heal comes through the poll/retry path).</summary>
    private sealed class FakeDelivery : HttpMessageHandler
    {
        public const string Url = "http://delivery.quonfig.test";

        private const string Envelope =
            "{\"configs\":[],\"meta\":{\"version\":\"gen-7\",\"environment\":\"production\",\"generation\":7}}";

        private volatile bool _healthy;

        public void Heal() => _healthy = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (_healthy && path.EndsWith("/api/v2/configs", StringComparison.Ordinal))
            {
                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Envelope, Encoding.UTF8, "application/json"),
                };
                ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"gen-7\"");
                return Task.FromResult(ok);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
