using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using OpenFeature.Constant;
using OpenFeature.Model;
using Quonfig.Sdk;
using Xunit;
using QuonfigClient = Quonfig.Sdk.Quonfig;
using Reason = OpenFeature.Constant.Reason;

namespace Quonfig.OpenFeature.ServerProvider.Tests;

/// <summary>
/// Tests for <see cref="QuonfigProvider"/>. Unit tests cover context mapping and the
/// not-initialized path; integration tests run in datadir mode against the shared
/// integration-test-data fixtures (same keys/values the openfeature-go provider asserts).
/// </summary>
public sealed class QuonfigProviderTests
{
    // ---- Context mapping unit tests (assert the mapped ContextSet directly) ----

    [Fact]
    public void MapContext_DotNotation_SplitsIntoNamespace()
    {
        var ctx = EvaluationContext.Builder().Set("user.email", new Value("alice@co.com")).Build();
        var mapped = ContextMapper.MapContext(ctx, "user.id");

        mapped.Should().NotBeNull();
        var lookup = mapped!.GetContextValue("user.email");
        lookup.Exists.Should().BeTrue();
        lookup.Value!.ToObject().Should().Be("alice@co.com");
    }

    [Fact]
    public void MapContext_NoDot_GoesToEmptyNamespace()
    {
        var ctx = EvaluationContext.Builder().Set("country", new Value("US")).Build();
        var mapped = ContextMapper.MapContext(ctx, "user.id");

        mapped!.GetContextValue("country").Exists.Should().BeTrue();
    }

    [Fact]
    public void MapContext_TargetingKey_UsesMapping()
    {
        var ctx = EvaluationContext.Builder().Set("targetingKey", new Value("user-123")).Build();
        var mapped = ContextMapper.MapContext(ctx, "user.id");

        var lookup = mapped!.GetContextValue("user.id");
        lookup.Exists.Should().BeTrue();
        lookup.Value!.ToObject().Should().Be("user-123");
    }

    [Fact]
    public void MapContext_MultiDot_SplitsOnFirstDotOnly()
    {
        var ctx = EvaluationContext.Builder().Set("user.ip.address", new Value("1.2.3.4")).Build();
        var mapped = ContextMapper.MapContext(ctx, "user.id");

        // namespace "user", property "ip.address"
        mapped!.GetContextValue("user.ip.address").Exists.Should().BeTrue();
    }

    [Fact]
    public void MapContext_Empty_ReturnsNull()
    {
        var ctx = EvaluationContext.Builder().Build();
        ContextMapper.MapContext(ctx, "user.id").Should().BeNull();
    }

    [Fact]
    public void MapContext_Null_ReturnsNull()
    {
        ContextMapper.MapContext(null, "user.id").Should().BeNull();
    }

    // ---- Provider unit tests ----

    [Fact]
    public void Metadata_Name_IsQuonfig()
    {
        var provider = new QuonfigProvider(new QuonfigProviderOptions { Datadir = "/nonexistent", Environment = "Production" });
        provider.GetMetadata().Name.Should().Be("quonfig");
    }

    [Fact]
    public async Task NotInitialized_ReturnsDefault_WithProviderNotReady()
    {
        // Provider built but InitializeAsync NOT called — client is null.
        var provider = new QuonfigProvider(new QuonfigProviderOptions { Datadir = "/nonexistent", Environment = "Production" });

        var b = await provider.ResolveBooleanValueAsync("some-flag", true);
        b.Value.Should().BeTrue();
        b.ErrorType.Should().Be(ErrorType.ProviderNotReady);
        b.Reason.Should().Be(Reason.Error);

        var s = await provider.ResolveStringValueAsync("some-flag", "default");
        s.Value.Should().Be("default");

        var i = await provider.ResolveIntegerValueAsync("some-flag", 42);
        i.Value.Should().Be(42);

        var d = await provider.ResolveDoubleValueAsync("some-flag", 3.14);
        d.Value.Should().Be(3.14);
    }

    // ---- Integration tests (datadir mode) ----

    private static QuonfigProvider NewDatadirProvider()
    {
        var provider = new QuonfigProvider(new QuonfigProviderOptions
        {
            Datadir = IntegrationTestDataDir(),
            Environment = "Production",
            ConfigureClient = o => o.CollectEvaluationSummaries = false,
        });
        provider.InitializeAsync(EvaluationContext.Empty).GetAwaiter().GetResult();
        return provider;
    }

    [Fact]
    public async Task Integration_BooleanFlag_AlwaysTrue()
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveBooleanValueAsync("always.true", false);

        detail.Value.Should().BeTrue();
        detail.ErrorType.Should().Be(ErrorType.None);
        // sdk-net (like sdk-java, unlike sdk-go) reports an ALWAYS_TRUE-only rule match as
        // TARGETING_MATCH. The provider passes the SDK reason through verbatim.
        detail.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Fact]
    public async Task Integration_StringFlag()
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveStringValueAsync("brand.new.string", "default");

        detail.Value.Should().Be("hello.world");
        detail.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Fact]
    public async Task Integration_IntFlag()
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveIntegerValueAsync("brand.new.int", 0);

        detail.Value.Should().Be(123);
    }

    [Fact]
    public async Task Integration_UnknownFlag_ReturnsDefault_WithFlagNotFound()
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveStringValueAsync("this-flag-does-not-exist", "fallback");

        detail.Value.Should().Be("fallback");
        detail.Reason.Should().Be(Reason.Default);
        detail.ErrorType.Should().Be(ErrorType.FlagNotFound);
    }

    [Fact]
    public async Task Integration_DotNotationContext()
    {
        var provider = NewDatadirProvider();

        var noCtx = await provider.ResolveStringValueAsync("my-test-key", "default");
        noCtx.Value.Should().Be("my-test-value");

        var withCtx = await provider.ResolveStringValueAsync(
            "my-test-key",
            "default",
            EvaluationContext.Builder().Set("namespace.key", new Value("present")).Build());
        withCtx.Value.Should().Be("namespace-value");
    }

    [Fact]
    public async Task Integration_TargetingMatchReason()
    {
        var provider = NewDatadirProvider();

        var match = await provider.ResolveBooleanValueAsync(
            "of.targeting",
            false,
            EvaluationContext.Builder().Set("user.plan", new Value("pro")).Build());
        match.Value.Should().BeTrue();
        match.Reason.Should().Be(Reason.TargetingMatch);

        var fallthrough = await provider.ResolveBooleanValueAsync("of.targeting", true);
        fallthrough.Value.Should().BeFalse();
        fallthrough.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Fact]
    public async Task Integration_WeightedValue_ResolvesNonDefault()
    {
        var provider = NewDatadirProvider();

        var detail = await provider.ResolveStringValueAsync(
            "of.weighted",
            "default",
            EvaluationContext.Builder().Set("targetingKey", new Value("92a202f2")).Build());

        // The weighted config resolves to a real value (not the caller default) and carries no error.
        detail.Value.Should().NotBe("default");
        detail.ErrorType.Should().Be(ErrorType.None);
    }

    // ---- Mapping unit tests (deterministic; cover every arm including SPLIT/ERROR) ----

    [Fact]
    public void ReasonMapping_CoversAllArms()
    {
        Mappings.ReasonFor(Quonfig.Sdk.Reason.Static, null).Should().Be(Reason.Static);
        Mappings.ReasonFor(Quonfig.Sdk.Reason.TargetingMatch, null).Should().Be(Reason.TargetingMatch);
        Mappings.ReasonFor(Quonfig.Sdk.Reason.Split, null).Should().Be(Reason.Split);
        Mappings.ReasonFor(Quonfig.Sdk.Reason.Default, null).Should().Be(Reason.Default);
        Mappings.ReasonFor(Quonfig.Sdk.Reason.Unknown, null).Should().Be(Reason.Unknown);
        // FLAG_NOT_FOUND surfaces as DEFAULT; other error codes as ERROR.
        Mappings.ReasonFor(Quonfig.Sdk.Reason.Error, Quonfig.Sdk.ErrorCode.FlagNotFound).Should().Be(Reason.Default);
        Mappings.ReasonFor(Quonfig.Sdk.Reason.Error, Quonfig.Sdk.ErrorCode.General).Should().Be(Reason.Error);
    }

    [Fact]
    public void ErrorTypeMapping_CoversAllArms()
    {
        Mappings.ErrorTypeFor(null).Should().Be(ErrorType.None);
        Mappings.ErrorTypeFor(Quonfig.Sdk.ErrorCode.FlagNotFound).Should().Be(ErrorType.FlagNotFound);
        Mappings.ErrorTypeFor(Quonfig.Sdk.ErrorCode.TypeMismatch).Should().Be(ErrorType.TypeMismatch);
        Mappings.ErrorTypeFor(Quonfig.Sdk.ErrorCode.General).Should().Be(ErrorType.General);
    }

    [Fact]
    public async Task Integration_StringList_ViaStructure()
    {
        var provider = NewDatadirProvider();

        var detail = await provider.ResolveStructureValueAsync("brand.new.string-list", new Value("fallback"));

        detail.Value.IsList.Should().BeTrue();
        detail.Value.AsList!.Select(v => v.AsString).Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public async Task Integration_EmitsProviderReady()
    {
        var provider = new QuonfigProvider(new QuonfigProviderOptions
        {
            Datadir = IntegrationTestDataDir(),
            Environment = "Production",
            ConfigureClient = o => o.CollectEvaluationSummaries = false,
        });

        await provider.InitializeAsync(EvaluationContext.Empty);

        provider.GetEventChannel().Reader.TryRead(out var payload).Should().BeTrue();
        payload.Should().BeOfType<ProviderEventPayload>()
            .Which.Type.Should().Be(ProviderEventTypes.ProviderReady);
    }

    [Fact]
    public async Task Integration_InternalClientCtor_ResolvesFlags()
    {
        // Exercises the internal (IQuonfig) test-seam constructor with a pre-built client.
        await using var client = new QuonfigClient(new QuonfigOptions
        {
            Datadir = IntegrationTestDataDir(),
            Environment = "Production",
            CollectEvaluationSummaries = false,
        });
        await client.InitAsync();

        var provider = new QuonfigProvider(client);
        await provider.InitializeAsync(EvaluationContext.Empty);

        provider.GetClient().Should().NotBeNull();
        var detail = await provider.ResolveBooleanValueAsync("always.true", false);
        detail.Value.Should().BeTrue();
    }

    /// <summary>Walks up from the test runtime directory to find the shared integration-test-data fixtures.
    /// Uses <see cref="System.AppContext.BaseDirectory"/> (the bin output dir) rather than [CallerFilePath]:
    /// under deterministic CI builds (CI=true) the compiler rewrites source paths to "/_/...", which makes a
    /// CallerFilePath walk-up fail. Mirrors sdk-net's LocateCorpus.</summary>
    private static string IntegrationTestDataDir()
    {
        var dir = System.AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, "integration-test-data", "data", "integration-tests"));
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
            {
                break;
            }

            dir = parent;
        }

        throw new DirectoryNotFoundException("could not locate integration-test-data/data/integration-tests by walking up from " + System.AppContext.BaseDirectory);
    }
}
