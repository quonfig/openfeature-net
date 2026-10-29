using System;
using System.Collections.Generic;
using OpenFeature.Model;
using Quonfig.Sdk;
using OFConst = OpenFeature.Constant;

namespace Quonfig.OpenFeature.ServerProvider;

/// <summary>
/// Maps Quonfig's typed evaluation outcome (Reason / ErrorCode / metadata) onto OpenFeature's
/// equivalents. The mapping is 1:1 and identical to the openfeature-go / -node / -ruby / -python
/// providers — it reads the SDK's typed <see cref="ErrorCode"/> set at the actual error site, never
/// inferring from error-message text.
/// </summary>
internal static class Mappings
{
    /// <summary>
    /// Maps the Quonfig <see cref="Reason"/> / <see cref="ErrorCode"/> to an OpenFeature reason string.
    /// A missing flag surfaces as <c>DEFAULT</c> (the spec lets providers pick DEFAULT or ERROR; we pick
    /// DEFAULT to match the existing providers); other error codes surface as <c>ERROR</c>.
    /// </summary>
    public static string ReasonFor(Reason reason, ErrorCode? errorCode)
    {
        if (errorCode == ErrorCode.FlagNotFound)
        {
            return OFConst.Reason.Default;
        }

        if (errorCode is not null)
        {
            return OFConst.Reason.Error;
        }

        return reason switch
        {
            Reason.Static => OFConst.Reason.Static,
            Reason.TargetingMatch => OFConst.Reason.TargetingMatch,
            Reason.Split => OFConst.Reason.Split,
            Reason.Default => OFConst.Reason.Default,
            _ => OFConst.Reason.Unknown,
        };
    }

    /// <summary>The name of the SDK's parse-failure <see cref="ErrorCode"/> member (qfg-wg1n).</summary>
    private const string ParseErrorName = "ParseError";

    /// <summary>
    /// The ordinal Quonfig.Sdk gives <c>ParseError</c>: it is appended after <see cref="ErrorCode.General"/>
    /// so the existing members keep their values.
    /// </summary>
    private const int ParseErrorOrdinal = 3;

    /// <summary>Maps a Quonfig <see cref="ErrorCode"/> to an OpenFeature <see cref="OFConst.ErrorType"/>.</summary>
    /// <remarks>
    /// The SDK's <c>ParseError</c> code (a value of the requested type that does not parse, qfg-wg1n) maps
    /// to <c>PARSE_ERROR</c>. This provider builds against <c>Quonfig.Sdk</c> 2.1, which has no such member,
    /// so the code is matched by name at run time (see <see cref="IsParseError"/>) and the provider works
    /// with both 2.1 and later SDKs. Any other code it does not know stays <c>GENERAL</c>.
    /// </remarks>
    public static OFConst.ErrorType ErrorTypeFor(ErrorCode? errorCode) => errorCode switch
    {
        null => OFConst.ErrorType.None,
        ErrorCode.FlagNotFound => OFConst.ErrorType.FlagNotFound,
        ErrorCode.TypeMismatch => OFConst.ErrorType.TypeMismatch,
        ErrorCode.General => OFConst.ErrorType.General,
        { } code when IsParseError(code) => OFConst.ErrorType.ParseError,
        _ => OFConst.ErrorType.General,
    };

    /// <summary>
    /// True when <paramref name="code"/> is the SDK's <c>ParseError</c>: the member of that name in the
    /// loaded <c>Quonfig.Sdk</c>, or, when the loaded SDK names no member at that value, the ordinal
    /// the SDK reserves for it.
    /// </summary>
    internal static bool IsParseError(ErrorCode code) =>
        Enum.IsDefined(typeof(ErrorCode), code)
            ? string.Equals(Enum.GetName(typeof(ErrorCode), code), ParseErrorName, StringComparison.Ordinal)
            : (int)code == ParseErrorOrdinal;

    /// <summary>Copies the SDK's flag metadata into an OpenFeature <see cref="ImmutableMetadata"/>, dropping null values.</summary>
    public static ImmutableMetadata ToFlagMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        var dict = new Dictionary<string, object>();
        foreach (var kv in metadata)
        {
            if (kv.Value is not null)
            {
                dict[kv.Key] = kv.Value;
            }
        }

        return new ImmutableMetadata(dict);
    }

    /// <summary>Converts a parsed JSON value returned by <c>GetJson</c> into an OpenFeature <see cref="Value"/>.</summary>
    public static Value ToValue(object? json)
    {
        switch (json)
        {
            case null:
                return new Value();
            case bool b:
                return new Value(b);
            case string s:
                return new Value(s);
            case int i:
                return new Value(i);
            case long l:
                return new Value((double)l);
            case double d:
                return new Value(d);
            case IReadOnlyDictionary<string, object?> map:
                {
                    var builder = Structure.Builder();
                    foreach (var kv in map)
                    {
                        builder.Set(kv.Key, ToValue(kv.Value));
                    }

                    return new Value(builder.Build());
                }
            case IEnumerable<object?> list:
                {
                    var values = new List<Value>();
                    foreach (var item in list)
                    {
                        values.Add(ToValue(item));
                    }

                    return new Value(values);
                }
            default:
                return new Value(json.ToString() ?? string.Empty);
        }
    }
}
