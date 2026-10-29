using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using OpenFeature.Constant;
using OpenFeature.Model;
using Quonfig.Sdk;
using Xunit;
using Reason = OpenFeature.Constant.Reason;

namespace Quonfig.OpenFeature.ServerProvider.Tests;

/// <summary>
/// qfg-wg1n: a <c>Quonfig.Sdk</c> details result that carries the SDK's <c>ParseError</c> code maps
/// straight to OpenFeature <c>PARSE_ERROR</c>, with no provider re-read. The released
/// <c>Quonfig.Sdk</c> 2.1 has no <c>ParseError</c> member; its next minor appends one after
/// <c>General</c> (ordinal 3). These tests stub the SDK with that code.
/// </summary>
public sealed class ParseErrorPassthroughTests
{
    /// <summary>The SDK's <c>ParseError</c> code, as the next Quonfig.Sdk minor appends it.</summary>
    private static readonly ErrorCode SdkParseError = (ErrorCode)3;

    [Fact]
    public void ErrorTypeFor_SdkParseError_IsParseError() =>
        Mappings.ErrorTypeFor(SdkParseError).Should().Be(ErrorType.ParseError);

    [Fact]
    public void ErrorTypeFor_UnknownSdkCode_StaysGeneral() =>
        Mappings.ErrorTypeFor((ErrorCode)99).Should().Be(ErrorType.General);

    [Fact]
    public void ReasonFor_SdkParseError_IsError() =>
        Mappings.ReasonFor(Quonfig.Sdk.Reason.Error, SdkParseError).Should().Be(Reason.Error);

    [Fact]
    public async Task Integer_SdkParseError_PassesThrough_WithoutReRead()
    {
        var (provider, stub) = await NewProviderAsync("int");
        var detail = await provider.ResolveIntegerValueAsync("k", 7);

        AssertParseError(detail, 7, stub);
    }

    [Fact]
    public async Task Double_SdkParseError_PassesThrough_WithoutReRead()
    {
        var (provider, stub) = await NewProviderAsync("double");
        var detail = await provider.ResolveDoubleValueAsync("k", 1.5);

        AssertParseError(detail, 1.5, stub);
    }

    [Fact]
    public async Task Boolean_SdkParseError_PassesThrough()
    {
        var (provider, stub) = await NewProviderAsync("bool");
        var detail = await provider.ResolveBooleanValueAsync("k", true);

        AssertParseError(detail, true, stub);
    }

    [Fact]
    public async Task String_OnDuration_SdkParseError_PassesThrough_WithoutReRead()
    {
        var (provider, stub) = await NewProviderAsync("duration");
        var detail = await provider.ResolveStringValueAsync("k", "PT0S");

        AssertParseError(detail, "PT0S", stub);
    }

    [Fact]
    public async Task String_OnString_SdkParseError_PassesThrough()
    {
        var (provider, stub) = await NewProviderAsync("string");
        var detail = await provider.ResolveStringValueAsync("k", "fallback");

        AssertParseError(detail, "fallback", stub);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("string_list")]
    public async Task Structure_SdkParseError_PassesThrough(string valueType)
    {
        var (provider, stub) = await NewProviderAsync(valueType);
        var fallback = new Value("fallback");
        var detail = await provider.ResolveStructureValueAsync("k", fallback);

        AssertParseError(detail, fallback, stub);
    }

    private static async Task<(QuonfigProvider Provider, ParseErrorQuonfig Stub)> NewProviderAsync(string valueType)
    {
        var client = ParseErrorQuonfig.Create(valueType);
        var provider = new QuonfigProvider(client);
        await provider.InitializeAsync(EvaluationContext.Empty);
        return (provider, (ParseErrorQuonfig)(object)client);
    }

    private static void AssertParseError<T>(ResolutionDetails<T> detail, T fallback, ParseErrorQuonfig stub)
    {
        detail.Value.Should().Be(fallback);
        detail.Reason.Should().Be(Reason.Error);
        detail.ErrorType.Should().Be(ErrorType.ParseError);
        detail.ErrorMessage.Should().Be(ParseErrorQuonfig.Message);
        // A passed-through PARSE_ERROR needs no plain-getter re-read (the 2.1 workaround).
        stub.PlainCalls.Should().BeEmpty();
    }

    /// <summary>
    /// A stub <see cref="IQuonfig"/> + <see cref="IQuonfigDetailsReader"/> whose every details read
    /// reports the SDK's <c>ParseError</c> code for a config of <c>valueType</c>.
    /// </summary>
    public class ParseErrorQuonfig : DispatchProxy, IQuonfigDetailsReader
    {
        internal const string Message = "config \"k\" cannot be parsed";

        private string _valueType = null!;

        public List<string> PlainCalls { get; } = new();

        public static IQuonfig Create(string valueType)
        {
            var proxy = Create<IQuonfig, ParseErrorQuonfig>();
            ((ParseErrorQuonfig)(object)proxy)._valueType = valueType;
            return proxy;
        }

        public EvaluationDetails<object?> EvaluateDetails(string key, ContextSet? contexts = null) =>
            new(null, Quonfig.Sdk.Reason.Error, "default", null, SdkParseError, Message, null, _valueType);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name == nameof(IQuonfig.InitAsync) || name == nameof(IQuonfig.CloseAsync))
            {
                return Task.CompletedTask;
            }

            if (name.EndsWith("Details", StringComparison.Ordinal))
            {
                var defaultValue = args is { Length: > 2 } ? args[2] : null;
                return Activator.CreateInstance(
                    targetMethod.ReturnType,
                    defaultValue,
                    Quonfig.Sdk.Reason.Error,
                    "default",
                    null,
                    (ErrorCode?)SdkParseError,
                    Message,
                    null);
            }

            if (name.StartsWith("Get", StringComparison.Ordinal))
            {
                PlainCalls.Add(name);
                return null;
            }

            if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal))
            {
                return null;
            }

            throw new NotSupportedException(name);
        }
    }
}
