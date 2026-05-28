using System;
using System.Collections.Generic;
using OpenFeature.Model;
using Quonfig.Sdk;

namespace Quonfig.OpenFeature.ServerProvider;

/// <summary>
/// Converts an OpenFeature flat <see cref="EvaluationContext"/> into a Quonfig
/// <see cref="ContextSet"/>. Mirrors the openfeature-go provider's <c>MapContext</c> exactly.
///
/// <para>Mapping rules (split on the first dot):</para>
/// <list type="bullet">
///   <item><c>"user.email"</c> → namespace <c>"user"</c>, property <c>"email"</c></item>
///   <item><c>"country"</c> (no dot) → namespace <c>""</c>, property <c>"country"</c></item>
///   <item><c>"user.ip.address"</c> → namespace <c>"user"</c>, property <c>"ip.address"</c> (first dot only)</item>
///   <item><c>targetingKey</c> → resolved via the configured targeting-key mapping (default <c>"user.id"</c>)</item>
/// </list>
/// </summary>
public static class ContextMapper
{
    /// <summary>The OpenFeature reserved key that carries the targeting key.</summary>
    private const string TargetingKey = "targetingKey";

    /// <summary>
    /// Maps <paramref name="context"/> to a <see cref="ContextSet"/>, or returns <c>null</c> when the
    /// context is empty or contributes no usable values (matching the Go provider's nil semantics).
    /// </summary>
    public static ContextSet? MapContext(EvaluationContext? context, string targetingKeyMapping)
    {
        if (context is null || context.Count == 0)
        {
            return null;
        }

        var namespaces = new Dictionary<string, ContextProperties>(StringComparer.Ordinal);

        foreach (var kv in context.AsDictionary())
        {
            var value = kv.Value;
            if (value is null || value.IsNull)
            {
                continue;
            }

            var contextValue = ToContextValue(value);
            if (contextValue is null)
            {
                continue;
            }

            var (ns, prop) = kv.Key == TargetingKey
                ? SplitFirst(targetingKeyMapping)
                : SplitFirst(kv.Key);

            if (!namespaces.TryGetValue(ns, out var props))
            {
                props = new ContextProperties();
                namespaces[ns] = props;
            }

            props[prop] = contextValue;
        }

        if (namespaces.Count == 0)
        {
            return null;
        }

        var ctxSet = new ContextSet();
        foreach (var entry in namespaces)
        {
            ctxSet[entry.Key] = entry.Value;
        }

        return ctxSet;
    }

    /// <summary>Splits <paramref name="s"/> on the first dot; with no dot the whole string is the property in the empty namespace.</summary>
    private static (string Namespace, string Property) SplitFirst(string s)
    {
        var idx = s.IndexOf('.');
        return idx < 0 ? (string.Empty, s) : (s.Substring(0, idx), s.Substring(idx + 1));
    }

    /// <summary>
    /// Converts an OpenFeature <see cref="Value"/> to a Quonfig <see cref="ContextValue"/>. Returns
    /// <c>null</c> for structures and other non-scalar shapes that have no Quonfig context analogue.
    /// </summary>
    private static ContextValue? ToContextValue(Value value)
    {
        if (value.IsBoolean)
        {
            return new ContextValueBool(value.AsBoolean!.Value);
        }

        if (value.IsString)
        {
            return new ContextValueString(value.AsString!);
        }

        if (value.IsNumber)
        {
            var d = value.AsDouble!.Value;
            // OpenFeature stores all numbers as double; preserve whole numbers as long so integer
            // targeting (segment membership, bucketing keys) behaves like the native SDK.
            if (!double.IsInfinity(d) && !double.IsNaN(d) && d == Math.Floor(d)
                && d >= long.MinValue && d <= long.MaxValue)
            {
                return new ContextValueLong((long)d);
            }

            return new ContextValueDouble(d);
        }

        if (value.IsList)
        {
            var items = new List<string>();
            foreach (var item in value.AsList!)
            {
                if (item.IsString)
                {
                    items.Add(item.AsString!);
                }
            }

            return new ContextValueStringList(items);
        }

        if (value.IsDateTime)
        {
            return new ContextValueString(value.AsDateTime!.Value.ToString("o"));
        }

        return null;
    }
}
