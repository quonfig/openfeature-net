using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using Xunit;
using Reason = OpenFeature.Constant.Reason;

namespace Quonfig.OpenFeature.ServerProvider.Tests;

/// <summary>
/// Typed-getter contract (qfg-zvyy), driven through the real provider and the OpenFeature client
/// against the integration-test-data <c>getter.contract.*</c> fixtures. No mocks: this pins what
/// provider users inherit from <c>Quonfig.Sdk</c> 2.0.0.
/// </summary>
/// <remarks>
/// The rule: a flag read with the method for another type returns the caller's default with reason
/// ERROR and error type TYPE_MISMATCH. Strings are never coerced (a stored "false" is not a bool,
/// "3" is not a number). Numbers convert only when exact: an int config reads as a double, and a
/// double config reads as an int only when it is whole and within +/-(2^53-1). A value of the requested
/// type that does not parse (a malformed stored or ENV_VAR int or double) is PARSE_ERROR (qfg-9xsl); an
/// unset ENV_VAR is GENERAL.
/// </remarks>
public sealed class TypedGetterContractTests : IClassFixture<TypedGetterContractTests.Fixture>
{
    private readonly FeatureClient _client;

    public TypedGetterContractTests(Fixture fixture)
    {
        _client = fixture.Client;
    }

    // ---- Wrong type -> default + TYPE_MISMATCH ----

    [Fact]
    public async Task Boolean_OnStoredStringFalse_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetBooleanDetailsAsync("getter.contract.string.false", true), true);

    [Fact]
    public async Task Boolean_OnStoredStringTrue_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetBooleanDetailsAsync("getter.contract.string.true", false), false);

    [Fact]
    public async Task Integer_OnStoredStringNumber_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetIntegerDetailsAsync("getter.contract.string.number", 7), 7);

    [Fact]
    public async Task Double_OnStoredStringNumber_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetDoubleDetailsAsync("getter.contract.string.number", 7.5), 7.5);

    [Fact]
    public async Task String_OnStoredInt_ReturnsDefault_NotItsText() =>
        AssertTypeMismatch(await _client.GetStringDetailsAsync("getter.contract.int", "fallback"), "fallback");

    [Fact]
    public async Task String_OnStoredBool_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetStringDetailsAsync("getter.contract.bool", "fallback"), "fallback");

    [Fact]
    public async Task Boolean_OnStoredInt_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetBooleanDetailsAsync("getter.contract.int", true), true);

    [Fact]
    public async Task Integer_OnFractionalDouble_ReturnsDefault_NotTruncated() =>
        AssertTypeMismatch(await _client.GetIntegerDetailsAsync("getter.contract.double", 7), 7);

    [Fact]
    public async Task Integer_OnUnsafeDouble_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetIntegerDetailsAsync("getter.contract.double.unsafe", 7), 7);

    [Fact]
    public async Task Integer_OnHugeDouble_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetIntegerDetailsAsync("getter.contract.double.huge", 7), 7);

    [Fact]
    public async Task String_OnDuration_ReturnsStoredIsoText()
    {
        // A duration reads as a string as its stored ISO 8601 text (openfeature.yaml Case 2, qfg-177f).
        var details = await _client.GetStringDetailsAsync("getter.contract.duration", "fallback");
        details.Value.Should().Be("PT30S");
        details.Reason.Should().Be(Reason.Static);
        details.ErrorType.Should().Be(ErrorType.None);
    }

    [Fact]
    public async Task Double_OnDuration_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetDoubleDetailsAsync("getter.contract.duration", 5.0), 5.0);

    [Fact]
    public async Task Integer_OnDuration_ReturnsDefault() =>
        AssertTypeMismatch(await _client.GetIntegerDetailsAsync("getter.contract.duration", 5), 5);

    [Fact]
    public async Task Structure_OnDuration_ReturnsDefault()
    {
        var fallback = new Value("d");
        var details = await _client.GetObjectDetailsAsync("getter.contract.duration", fallback);
        details.Value.Should().BeSameAs(fallback);
        details.Reason.Should().Be(Reason.Error);
        details.ErrorType.Should().Be(ErrorType.TypeMismatch);
    }

    [Fact]
    public async Task Structure_OnStoredString_ReturnsDefault()
    {
        var fallback = new Value("d");
        var details = await _client.GetObjectDetailsAsync("getter.contract.string", fallback);
        details.Value.Should().BeSameAs(fallback);
        details.Reason.Should().Be(Reason.Error);
        details.ErrorType.Should().Be(ErrorType.TypeMismatch);
    }

    [Fact]
    public async Task Structure_OnStoredBool_ReturnsDefault()
    {
        var fallback = new Value("d");
        var details = await _client.GetObjectDetailsAsync("getter.contract.bool", fallback);
        details.Value.Should().BeSameAs(fallback);
        details.Reason.Should().Be(Reason.Error);
        details.ErrorType.Should().Be(ErrorType.TypeMismatch);
    }

    // ---- Matching and safely convertible types resolve ----

    [Fact]
    public async Task Boolean_OnBoolConfig()
    {
        var details = await _client.GetBooleanDetailsAsync("getter.contract.bool", false);
        details.Value.Should().BeTrue();
        details.Reason.Should().Be(Reason.Static);
        details.ErrorType.Should().Be(ErrorType.None);
    }

    [Fact]
    public async Task String_OnStringConfig() =>
        (await _client.GetStringValueAsync("getter.contract.string", "")).Should().Be("fast");

    [Fact]
    public async Task Integer_OnIntConfig() =>
        (await _client.GetIntegerValueAsync("getter.contract.int", 0)).Should().Be(30);

    [Fact]
    public async Task Double_OnIntConfig_Converts()
    {
        var details = await _client.GetDoubleDetailsAsync("getter.contract.int", 0.0);
        details.Value.Should().Be(30.0);
        details.ErrorType.Should().Be(ErrorType.None);
    }

    [Fact]
    public async Task Double_OnDoubleConfig() =>
        (await _client.GetDoubleValueAsync("getter.contract.double", 0.0)).Should().Be(3.9);

    [Fact]
    public async Task Integer_OnWholeDouble_Converts()
    {
        var details = await _client.GetIntegerDetailsAsync("getter.contract.double.whole", 0);
        details.Value.Should().Be(3);
        details.ErrorType.Should().Be(ErrorType.None);
    }

    [Fact]
    public async Task Structure_OnJsonConfig()
    {
        var details = await _client.GetObjectDetailsAsync("getter.contract.json", new Value("d"));
        details.ErrorType.Should().Be(ErrorType.None);
        details.Value.IsStructure.Should().BeTrue();
        details.Value.AsStructure!.GetValue("a").AsInteger.Should().Be(1);
    }

    [Fact]
    public async Task Structure_OnStringListConfig()
    {
        var details = await _client.GetObjectDetailsAsync("getter.contract.string_list", new Value("d"));
        details.ErrorType.Should().Be(ErrorType.None);
        details.Value.AsList!.Select(v => v.AsString).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Boolean_OnEnvVarBool() =>
        (await _client.GetBooleanValueAsync("getter.contract.provided.bool", false)).Should().BeTrue();

    // ---- Malformed values and resolution errors return the default ----

    [Fact]
    public async Task Integer_OnMalformedStoredInt_ReturnsDefault_ParseError() =>
        AssertParseError(await _client.GetIntegerDetailsAsync("getter.contract.int.malformed", 12), 12);

    [Fact]
    public async Task Double_OnMalformedStoredDouble_ReturnsDefault_ParseError() =>
        AssertParseError(await _client.GetDoubleDetailsAsync("getter.contract.double.malformed", 1.5), 1.5);

    [Fact]
    public async Task Integer_OnMalformedEnvVar_ReturnsDefault_ParseError() =>
        AssertParseError(await _client.GetIntegerDetailsAsync("getter.contract.provided.int.malformed", 4), 4);

    [Fact]
    public async Task Double_OnMalformedEnvVar_ReturnsDefault_ParseError() =>
        AssertParseError(await _client.GetDoubleDetailsAsync("getter.contract.provided.double.malformed", 2.5), 2.5);

    [Fact]
    public async Task Boolean_OnUnsetEnvVar_ReturnsDefault_General()
    {
        var details = await _client.GetBooleanDetailsAsync("getter.contract.provided.bool.missing", true);
        details.Value.Should().BeTrue();
        details.Reason.Should().Be(Reason.Error);
        details.ErrorType.Should().Be(ErrorType.General);
    }

    // ---- Logging: one warning per key, none for a valid read ----

    [Fact]
    public async Task WrongTypeRead_LogsOneWarningPerKey()
    {
        var (client, logger, provider) = await NewLoggingClientAsync();
        for (var i = 0; i < 3; i++)
        {
            await client.GetBooleanValueAsync("getter.contract.string.false", true);
        }

        logger.Warnings.Should().ContainSingle(w => w.Contains("getter.contract.string.false", StringComparison.Ordinal));
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task StructureRead_OfJsonConfig_LogsNoWarning()
    {
        // A valid object read must not log a type-mismatch warning for a probe the provider made.
        var (client, logger, provider) = await NewLoggingClientAsync();
        var details = await client.GetObjectDetailsAsync("getter.contract.json", new Value("d"));

        details.ErrorType.Should().Be(ErrorType.None);
        logger.Warnings.Should().BeEmpty();
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task StructureRead_OfStringListConfig_LogsNoWarning()
    {
        // qfg-tzby: a string_list read as an object is a valid read; the provider must not probe it
        // with a json read that logs the SDK's type-mismatch warning.
        var (client, logger, provider) = await NewLoggingClientAsync();
        var details = await client.GetObjectDetailsAsync("getter.contract.string_list", new Value("d"));

        details.ErrorType.Should().Be(ErrorType.None);
        details.Value.AsList!.Select(v => v.AsString).Should().Equal("a", "b");
        logger.Warnings.Should().BeEmpty();
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task StringRead_OfDurationConfig_LogsNoWarning()
    {
        // Reading a duration as a string is a valid read (qfg-177f): no type-mismatch warning.
        var (client, logger, provider) = await NewLoggingClientAsync();
        var details = await client.GetStringDetailsAsync("getter.contract.duration", "fallback");

        details.Value.Should().Be("PT30S");
        logger.Warnings.Should().BeEmpty();
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task MalformedRead_LogsOneWarningPerKey()
    {
        // The PARSE_ERROR re-read must not add a warning: the SDK warns once per key.
        var (client, logger, provider) = await NewLoggingClientAsync();
        for (var i = 0; i < 3; i++)
        {
            await client.GetIntegerValueAsync("getter.contract.int.malformed", 1);
        }

        logger.Warnings.Should().ContainSingle(w => w.Contains("getter.contract.int.malformed", StringComparison.Ordinal));
        await provider.ShutdownAsync();
    }

    // ---- Helpers ----

    private static void AssertParseError<T>(FlagEvaluationDetails<T> details, T fallback)
    {
        details.Value.Should().Be(fallback);
        details.Reason.Should().Be(Reason.Error);
        details.ErrorType.Should().Be(ErrorType.ParseError);
    }

    private static void AssertTypeMismatch<T>(FlagEvaluationDetails<T> details, T fallback)
    {
        details.Value.Should().Be(fallback);
        details.Reason.Should().Be(Reason.Error);
        details.ErrorType.Should().Be(ErrorType.TypeMismatch);
    }

    private static async Task<(FeatureClient Client, CapturingLogger Logger, QuonfigProvider Provider)> NewLoggingClientAsync()
    {
        var logger = new CapturingLogger();
        var provider = new QuonfigProvider(new QuonfigProviderOptions
        {
            Datadir = Fixture.IntegrationTestDataDir(),
            Environment = "Production",
            ConfigureClient = o =>
            {
                o.CollectEvaluationSummaries = false;
                o.Logger = logger;
            },
        });
        var domain = "typed-getter-contract-log-" + Guid.NewGuid();
        await Api.Instance.SetProviderAsync(domain, provider);
        return (Api.Instance.GetClient(domain), logger, provider);
    }

    /// <summary>One provider shared by the read-only contract cases.</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private QuonfigProvider? _provider;

        public FeatureClient Client { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Environment.SetEnvironmentVariable("QUONFIG_ITD_GETTER_BOOL", "true");
            Environment.SetEnvironmentVariable("QUONFIG_ITD_GETTER_UNSET_BOOL", null);
            Environment.SetEnvironmentVariable("QUONFIG_ITD_GETTER_INT", "twelve");
            Environment.SetEnvironmentVariable("QUONFIG_ITD_GETTER_DOUBLE", "abc");

            _provider = new QuonfigProvider(new QuonfigProviderOptions
            {
                Datadir = IntegrationTestDataDir(),
                Environment = "Production",
                ConfigureClient = o => o.CollectEvaluationSummaries = false,
            });
            var domain = "typed-getter-contract-" + Guid.NewGuid();
            await Api.Instance.SetProviderAsync(domain, _provider);
            Client = Api.Instance.GetClient(domain);
        }

        public async Task DisposeAsync()
        {
            if (_provider is not null)
            {
                await _provider.ShutdownAsync();
            }
        }

        /// <summary>Walks up from the test bin dir to the sibling integration-test-data checkout.</summary>
        internal static string IntegrationTestDataDir()
        {
            var dir = AppContext.BaseDirectory;
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

            throw new DirectoryNotFoundException("could not locate integration-test-data/data/integration-tests by walking up from " + AppContext.BaseDirectory);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyList<string> Warnings => _warnings.ToList();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                _warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
