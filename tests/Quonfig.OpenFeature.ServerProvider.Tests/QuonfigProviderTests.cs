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
        // An unconditional (ALWAYS_TRUE / no-criteria) rule match is STATIC under the
        // canonical reason semantics sdk-net adopted in 0.0.2 (qfg-q7yz). The provider
        // passes the SDK reason through verbatim.
        detail.Reason.Should().Be(Reason.Static);
    }

    [Fact]
    public async Task Integration_StringFlag()
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveStringValueAsync("brand.new.string", "default");

        detail.Value.Should().Be("hello.world");
        // Unconditional rule -> STATIC under canonical 0.0.2 reason semantics (qfg-q7yz).
        detail.Reason.Should().Be(Reason.Static);
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
    public async Task Integration_UnknownStructureFlag_ReturnsDefault_WithFlagNotFound()
    {
        // Structure resolution calls GetStringListDetails / GetJsonDetails with no
        // default; under the SDK's default OnNoDefault.Throw those must not throw.
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveStructureValueAsync("this-flag-does-not-exist", new Value("fallback"));

        detail.Value.AsString.Should().Be("fallback");
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
    public async Task Integration_WeightedValue_Bucket0_ReportsSplit()
    {
        // Mirrors integration-test-data tests/openfeature/openfeature.yaml Case 5:
        // of.weighted + targetingKey "user-123" (targetingKeyMapping user.id, the default)
        // lands in weighted bucket 0 -> variant-a. A 0-based bucket index must still be
        // reported as SPLIT (qfg-stbb), with the bucket index carried in variant + metadata.
        var provider = NewDatadirProvider();

        var detail = await provider.ResolveStringValueAsync(
            "of.weighted",
            "variant-b",
            EvaluationContext.Builder().SetTargetingKey("user-123").Build());

        detail.Value.Should().Be("variant-a");
        detail.ErrorType.Should().Be(ErrorType.None);
        detail.Reason.Should().Be(Reason.Split);
        detail.Variant.Should().Be("split:0");
        detail.FlagMetadata.Should().NotBeNull();
        detail.FlagMetadata!.GetInt("weightedValueIndex").Should().Be(0);
    }

    // ---- integration-test-data tests/openfeature/openfeature.yaml, Case 6 (qfg-9xsl) ----
    // A value that has the requested type but cannot be parsed is PARSE_ERROR (a data problem);
    // a read with the method for another type is TYPE_MISMATCH (a caller problem).

    [Fact]
    public async Task Case6_MalformedStoredInt_ViaInteger_ReturnsDefault_ParseError()
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveIntegerValueAsync("getter.contract.int.malformed", 7);

        AssertError(detail, 7, ErrorType.ParseError);
    }

    [Fact]
    public async Task Case6_MalformedStoredDouble_ViaDouble_ReturnsDefault_ParseError()
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveDoubleValueAsync("getter.contract.double.malformed", 1.5);

        AssertError(detail, 1.5, ErrorType.ParseError);
    }

    [Fact]
    public async Task Case6_EnvVarProvidedMalformedInt_ViaInteger_ReturnsDefault_ParseError()
    {
        System.Environment.SetEnvironmentVariable("QUONFIG_ITD_GETTER_INT", "twelve");
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveIntegerValueAsync("getter.contract.provided.int.malformed", 7);

        AssertError(detail, 7, ErrorType.ParseError);
    }

    [Fact]
    public async Task Case6_IntConfig_ViaBoolean_ReturnsDefault_TypeMismatch()
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveBooleanValueAsync("getter.contract.int", false);

        AssertError(detail, false, ErrorType.TypeMismatch);
    }

    // Case 2: a duration read as a string returns its stored ISO 8601 text unchanged (qfg-177f).
    [Theory]
    [InlineData("test.duration.PT90S", "PT90S")]
    [InlineData("test.duration.P1DT6H2M1.5S", "P1DT6H2M1.5S")]
    public async Task Case2_Duration_ViaString_ReturnsStoredIsoText(string key, string expected)
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveStringValueAsync(key, "PT0S");

        detail.Value.Should().Be(expected);
        detail.Reason.Should().Be(Reason.Static);
        detail.ErrorType.Should().Be(ErrorType.None);
    }

    // Case 2, malformed stored duration: the provider returns the default with PARSE_ERROR.
    [Fact]
    public async Task Case2_MalformedStoredDuration_ViaString_ReturnsDefault_ParseError()
    {
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveStringValueAsync("test.duration.malformed.30s", "PT0S");

        AssertError(detail, "PT0S", ErrorType.ParseError);
    }

    // A weighted duration read as a string reports SPLIT with the variant of the evaluation. The
    // config is written to a temp datadir: CI pins an integration-test-data tag that predates
    // weighted.variant.duration.
    [Fact]
    public async Task Duration_PickedBySplit_ViaString_ReturnsIsoText_Split()
    {
        var dir = Path.Combine(Path.GetTempPath(), "of-net-duration-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "configs"));
        File.WriteAllText(Path.Combine(dir, "quonfig.json"), "{\"environments\": [\"Production\"]}");
        File.WriteAllText(Path.Combine(dir, "configs", "of.duration.weighted.json"), """
            {
              "id": "19000000000000002",
              "projectId": "124",
              "key": "of.duration.weighted",
              "type": "config",
              "valueType": "duration",
              "sendToClientSdk": false,
              "default": {
                "rules": [
                  {
                    "criteria": [{ "operator": "ALWAYS_TRUE" }],
                    "value": {
                      "type": "weighted_values",
                      "value": {
                        "weightedValues": [
                          { "weight": 100000, "value": { "type": "duration", "value": "PT90S" } }
                        ],
                        "hashByPropertyName": "user.tracking_id"
                      }
                    }
                  }
                ]
              }
            }
            """);
        try
        {
            var provider = new QuonfigProvider(new QuonfigProviderOptions
            {
                Datadir = dir,
                Environment = "Production",
                ConfigureClient = o => o.CollectEvaluationSummaries = false,
            });
            await provider.InitializeAsync(EvaluationContext.Empty);

            var ctx = EvaluationContext.Builder().Set("user.tracking_id", new Value("abc")).Build();
            var detail = await provider.ResolveStringValueAsync("of.duration.weighted", "PT0S", ctx);

            detail.Value.Should().Be("PT90S");
            detail.Reason.Should().Be(Reason.Split);
            detail.ErrorType.Should().Be(ErrorType.None);
            detail.Variant.Should().NotBeNullOrEmpty();

            await provider.ShutdownAsync();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A duration picked by a targeting rule reports TARGETING_MATCH; the catch-all rule reports what the SDK reports.
    [Fact]
    public async Task Duration_PickedByTargetingRule_ViaString_ReturnsIsoText_TargetingMatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "of-net-duration-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "configs"));
        File.WriteAllText(Path.Combine(dir, "quonfig.json"), "{\"environments\": [\"Production\"]}");
        File.WriteAllText(Path.Combine(dir, "configs", "of.duration.targeting.json"), """
            {
              "id": "19000000000000001",
              "projectId": "124",
              "key": "of.duration.targeting",
              "type": "config",
              "valueType": "duration",
              "sendToClientSdk": false,
              "default": {
                "rules": [
                  {
                    "criteria": [
                      {
                        "propertyName": "user.plan",
                        "operator": "PROP_IS_ONE_OF",
                        "valueToMatch": { "type": "string_list", "value": ["pro"] }
                      }
                    ],
                    "value": { "type": "duration", "value": "PT5M" }
                  },
                  {
                    "criteria": [{ "operator": "ALWAYS_TRUE" }],
                    "value": { "type": "duration", "value": "PT30S" }
                  }
                ]
              }
            }
            """);
        try
        {
            var provider = new QuonfigProvider(new QuonfigProviderOptions
            {
                Datadir = dir,
                Environment = "Production",
                ConfigureClient = o => o.CollectEvaluationSummaries = false,
            });
            await provider.InitializeAsync(EvaluationContext.Empty);

            var pro = EvaluationContext.Builder().Set("user.plan", new Value("pro")).Build();
            var matched = await provider.ResolveStringValueAsync("of.duration.targeting", "PT0S", pro);
            matched.Value.Should().Be("PT5M");
            matched.Reason.Should().Be(Reason.TargetingMatch);
            matched.ErrorType.Should().Be(ErrorType.None);

            var free = EvaluationContext.Builder().Set("user.plan", new Value("free")).Build();
            var fallback = await provider.ResolveStringValueAsync("of.duration.targeting", "PT0S", free);
            fallback.Value.Should().Be("PT30S");
            fallback.ErrorType.Should().Be(ErrorType.None);
            // The reason and variant are the SDK's, the same as its typed duration read reports.
            var typed = provider.GetClient()!.GetDurationDetails("of.duration.targeting", ContextMapper.MapContext(free, "user.id"));
            fallback.Reason.Should().Be(Mappings.ReasonFor(typed.Reason, typed.ErrorCode));
            fallback.Variant.Should().Be(typed.Variant);

            await provider.ShutdownAsync();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // An ENV_VAR-provided duration that does not parse is PARSE_ERROR through the string read too.
    [Fact]
    public async Task Case2_EnvVarMalformedDuration_ViaString_ReturnsDefault_ParseError()
    {
        System.Environment.SetEnvironmentVariable("QUONFIG_ITD_DURATION_30S", "30s");
        var provider = NewDatadirProvider();
        var detail = await provider.ResolveStringValueAsync("provided.duration.malformed.30s", "PT0S");

        AssertError(detail, "PT0S", ErrorType.ParseError);
    }

    // A client without IQuonfigDetailsReader (a custom IQuonfig) keeps the 2.x behavior: TYPE_MISMATCH.
    [Fact]
    public async Task Duration_ViaString_ClientWithoutDetailsReader_ReturnsDefault_TypeMismatch()
    {
        await using var real = new QuonfigClient(new QuonfigOptions
        {
            Datadir = IntegrationTestDataDir(),
            Environment = "Production",
            CollectEvaluationSummaries = false,
        });
        await real.InitAsync();
        var plain = PlainQuonfig.Wrap(real);
        plain.Should().NotBeAssignableTo<IQuonfigDetailsReader>();

        var provider = new QuonfigProvider(plain);
        await provider.InitializeAsync(EvaluationContext.Empty);

        var duration = await provider.ResolveStringValueAsync("test.duration.PT90S", "PT0S");
        AssertError(duration, "PT0S", ErrorType.TypeMismatch);

        var str = await provider.ResolveStringValueAsync("getter.contract.string", "");
        str.Value.Should().Be("fast");
        str.ErrorType.Should().Be(ErrorType.None);
    }

    // A string or log_level config read as a string is one untyped evaluation, and the result is the
    // same value, reason, variant and metadata the SDK's typed GetStringDetails reports.
    [Theory]
    [InlineData("getter.contract.string")]
    [InlineData("log-level.prefab.views.index")]
    public async Task StringRead_OfStringOrLogLevel_OneEvaluation_SameAsGetStringDetails(string key)
    {
        await using var real = new QuonfigClient(new QuonfigOptions
        {
            Datadir = IntegrationTestDataDir(),
            Environment = "Production",
            CollectEvaluationSummaries = false,
        });
        await real.InitAsync();
        var counting = CountingQuonfig.Wrap(real);
        var provider = new QuonfigProvider(counting);
        await provider.InitializeAsync(EvaluationContext.Empty);

        var detail = await provider.ResolveStringValueAsync(key, "fallback");

        var spy = (CountingQuonfig)(object)counting;
        spy.EvaluateDetailsCalls.Should().Be(1);
        spy.TypedCalls.Should().BeEmpty();

        var typed = real.GetStringDetails(key, null, "fallback");
        typed.ErrorCode.Should().BeNull();
        detail.Value.Should().Be(typed.Value);
        detail.Reason.Should().Be(Mappings.ReasonFor(typed.Reason, typed.ErrorCode));
        detail.Variant.Should().Be(typed.Variant);
        detail.ErrorType.Should().Be(ErrorType.None);
        AssertSameMetadata(detail.FlagMetadata!, typed.Metadata);
    }

    // A config of another type still reports TYPE_MISMATCH through the typed read.
    [Fact]
    public async Task StringRead_OfIntConfig_FallsThroughToTypedRead_TypeMismatch()
    {
        await using var real = new QuonfigClient(new QuonfigOptions
        {
            Datadir = IntegrationTestDataDir(),
            Environment = "Production",
            CollectEvaluationSummaries = false,
        });
        await real.InitAsync();
        var counting = CountingQuonfig.Wrap(real);
        var provider = new QuonfigProvider(counting);
        await provider.InitializeAsync(EvaluationContext.Empty);

        var detail = await provider.ResolveStringValueAsync("getter.contract.int", "fallback");

        AssertError(detail, "fallback", ErrorType.TypeMismatch);
        var spy = (CountingQuonfig)(object)counting;
        spy.EvaluateDetailsCalls.Should().Be(1);
        spy.TypedCalls.Should().Contain(nameof(IQuonfig.GetStringDetails));
    }

    private static void AssertSameMetadata(ImmutableMetadata actual, System.Collections.Generic.IReadOnlyDictionary<string, object?> expected)
    {
        var nonNull = expected.Where(kv => kv.Value is not null).ToList();
        nonNull.Should().NotBeEmpty();
        actual.Count.Should().Be(nonNull.Count);
        foreach (var kv in nonNull)
        {
            actual.GetString(kv.Key).Should().Be((string)kv.Value!, "metadata key {0}", kv.Key);
        }
    }

    /// <summary>An <see cref="IQuonfig"/> + <see cref="IQuonfigDetailsReader"/> that forwards to a real
    /// client and records which reads the provider made.</summary>
    public class CountingQuonfig : System.Reflection.DispatchProxy, IQuonfigDetailsReader
    {
        private QuonfigClient _inner = null!;

        public int EvaluateDetailsCalls { get; private set; }

        public System.Collections.Generic.List<string> TypedCalls { get; } = new();

        public static IQuonfig Wrap(QuonfigClient inner)
        {
            var proxy = Create<IQuonfig, CountingQuonfig>();
            ((CountingQuonfig)(object)proxy)._inner = inner;
            return proxy;
        }

        public EvaluationDetails<object?> EvaluateDetails(string key, ContextSet? contexts = null)
        {
            EvaluateDetailsCalls++;
            return _inner.EvaluateDetails(key, contexts);
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name.StartsWith("Get", System.StringComparison.Ordinal))
            {
                TypedCalls.Add(targetMethod.Name);
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>An <see cref="IQuonfig"/> that forwards to a real client but does not implement
    /// <see cref="IQuonfigDetailsReader"/>, standing in for a custom implementation.</summary>
    public class PlainQuonfig : System.Reflection.DispatchProxy
    {
        private IQuonfig _inner = null!;

        public static IQuonfig Wrap(IQuonfig inner)
        {
            var proxy = Create<IQuonfig, PlainQuonfig>();
            ((PlainQuonfig)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                return targetMethod!.Invoke(_inner, args);
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    private static void AssertError<T>(ResolutionDetails<T> detail, T fallback, ErrorType errorType)
    {
        detail.Value.Should().Be(fallback);
        detail.Reason.Should().Be(Reason.Error);
        detail.ErrorType.Should().Be(errorType);
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
