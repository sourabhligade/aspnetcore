// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.AspNetCore.Routing.Tree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Components;

/// <summary>
/// Resolves components for an application.
/// </summary>
internal static class RouteTableFactory
{
    private static readonly ConcurrentDictionary<RouteKey, RouteTable> Cache = new();
    public static readonly IComparer<InboundRouteEntry> RouteOrder = Comparer<InboundRouteEntry>.Create((x, y) =>
    {
        var result = RouteComparison(x, y);
        return result != 0 ? result : string.Compare(x.RouteTemplate.TemplateText, y.RouteTemplate.TemplateText, StringComparison.OrdinalIgnoreCase);
    });

    public static RouteTable Create(RouteKey routeKey, IServiceProvider serviceProvider)
    {
        if (Cache.TryGetValue(routeKey, out var resolvedComponents))
        {
            return resolvedComponents;
        }

        var componentTypes = GetRouteableComponents(routeKey);
        var routeTable = Create(componentTypes, serviceProvider);
        Cache.TryAdd(routeKey, routeTable);
        return routeTable;
    }

    public static void ClearCaches() => Cache.Clear();

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Application code does not get trimmed, and the framework does not define routable components.")]
    private static List<Type> GetRouteableComponents(RouteKey routeKey)
    {
        var routeableComponents = new List<Type>();
        if (routeKey.AppAssembly is not null)
        {
            GetRouteableComponents(routeableComponents, routeKey.AppAssembly);
        }

        if (routeKey.AdditionalAssemblies is not null)
        {
            foreach (var assembly in routeKey.AdditionalAssemblies)
            {
                // We don't need process the assembly if it's the app assembly.
                if (assembly != routeKey.AppAssembly)
                {
                    GetRouteableComponents(routeableComponents, assembly);
                }
            }
        }

        return routeableComponents;

        static void GetRouteableComponents(List<Type> routeableComponents, Assembly assembly)
        {
            foreach (var type in assembly.ExportedTypes)
            {
                if (typeof(IComponent).IsAssignableFrom(type) && type.IsDefined(typeof(RouteAttribute)))
                {
                    routeableComponents.Add(type);
                }
            }
        }
    }

    internal static RouteTable Create(List<Type> componentTypes, IServiceProvider serviceProvider)
    {
        var templatesByHandler = new Dictionary<Type, string[]>();
        foreach (var componentType in componentTypes)
        {
            // We're deliberately using inherit = false here.
            //
            // RouteAttribute is defined as non-inherited, because inheriting a route attribute always causes an
            // ambiguity. You end up with two components (base class and derived class) with the same route.
            var routeAttributes = componentType.GetCustomAttributes(typeof(RouteAttribute), inherit: false);
            var templates = new string[routeAttributes.Length];
            for (var i = 0; i < routeAttributes.Length; i++)
            {
                var attribute = (RouteAttribute)routeAttributes[i];
                templates[i] = attribute.Template;
            }

            templatesByHandler.Add(componentType, templates);
        }
        return Create(templatesByHandler, serviceProvider);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Application code does not get trimmed, and the framework does not define routable components.")]
    internal static RouteTable Create(Dictionary<Type, string[]> templatesByHandler, IServiceProvider serviceProvider)
    {
        var builder = new TreeRouteBuilder(
            serviceProvider.GetRequiredService<ILoggerFactory>(),
            new DefaultInlineConstraintResolver(Options.Create(new RouteOptions()), serviceProvider));

        foreach (var (type, templates) in templatesByHandler)
        {
            var allRouteParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parsedTemplates = new (RouteTemplate, HashSet<string>)[templates.Length];
            for (var i = 0; i < templates.Length; i++)
            {
                var parsedTemplate = TemplateParser.Parse(templates[i]);
                var parameterNames = GetParameterNames(parsedTemplate);
                parsedTemplates[i] = (parsedTemplate, parameterNames);

                foreach (var parameterName in parameterNames)
                {
                    allRouteParameterNames.Add(parameterName);
                }
            }

            foreach (var (parsedTemplate, routeParameterNames) in parsedTemplates)
            {
                var unusedRouteParameterNames = GetUnusedParameterNames(allRouteParameterNames!, routeParameterNames!);
                builder.MapInbound(type, parsedTemplate, unusedRouteParameterNames);
            }
        }

        DetectAmbiguousRoutes(builder);

        //builder.InboundEntries.Sort(RouteOrder);
        return new RouteTable(builder.Build());
    }

    private static void DetectAmbiguousRoutes(TreeRouteBuilder builder)
    {
        for (var i = 0; i < builder.InboundEntries.Count; i++)
        {
            var left = builder.InboundEntries[i];
            for (var j = i + 1; j < builder.InboundEntries.Count; j++)
            {
                var right = builder.InboundEntries[j];
                var leftText = left.RouteTemplate.TemplateText!.Trim('/');
                var rightText = right.RouteTemplate.TemplateText!.Trim('/');
                if (left.Precedence != right.Precedence)
                {
                    continue;
                }

                var ambiguous = CompareSegments(left, right);
                if (ambiguous)
                {
                    throw new InvalidOperationException($@"The following routes are ambiguous:
'{leftText}' in '{left.Handler.FullName}'
'{rightText}' in '{right.Handler.FullName}'
");
                }
            }
        }
    }

    private static bool CompareSegments(InboundRouteEntry left, InboundRouteEntry right)
    {
        var ambiguous = true;
        for (var k = 0; k < left.RouteTemplate.Segments.Count; k++)
        {
            var leftSegment = left.RouteTemplate.Segments[k];
            var rightSegment = right.RouteTemplate.Segments[k];
            if (leftSegment.Parts.Count != rightSegment.Parts.Count)
            {
                ambiguous = false;
                break;
            }

            for (var l = 0; l < leftSegment.Parts.Count; l++)
            {
                var leftPart = leftSegment.Parts[l];
                var rightPart = rightSegment.Parts[l];
                if (leftPart.IsLiteral &&
                    rightPart.IsLiteral &&
                    !string.Equals(leftPart.Text, rightPart.Text, StringComparison.OrdinalIgnoreCase))
                {
                    ambiguous = false;
                    break;
                }
            }
            if (!ambiguous)
            {
                break;
            }
        }

        return ambiguous;
    }

    private static HashSet<string> GetParameterNames(RouteTemplate routeTemplate)
    {
        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in routeTemplate.Parameters)
        {
            parameterNames.Add(parameter.Name!);
        }

        return parameterNames;
    }

    private static List<string>? GetUnusedParameterNames(HashSet<string> allRouteParameterNames, HashSet<string> routeParameterNames)
    {
        List<string>? unusedParameters = null;
        foreach (var item in allRouteParameterNames)
        {
            if (!routeParameterNames.Contains(item))
            {
                unusedParameters ??= new();
                unusedParameters.Add(item);
            }
        }

        return unusedParameters;
    }

    /// <summary>
    /// Route precedence algorithm.
    /// We collect all the routes and sort them from most specific to
    /// less specific. The specificity of a route is given by the specificity
    /// of its segments and the position of those segments in the route.
    /// * A literal segment is more specific than a parameter segment.
    /// * A parameter segment with more constraints is more specific than one with fewer constraints
    /// * Segment earlier in the route are evaluated before segments later in the route.
    /// For example:
    /// /Literal is more specific than /Parameter
    /// /Route/With/{parameter} is more specific than /{multiple}/With/{parameters}
    /// /Product/{id:int} is more specific than /Product/{id}
    ///
    /// Routes can be ambiguous if:
    /// They are composed of literals and those literals have the same values (case insensitive)
    /// They are composed of a mix of literals and parameters, in the same relative order and the
    /// literals have the same values.
    /// For example:
    /// * /literal and /Literal
    /// /{parameter}/literal and /{something}/literal
    /// /{parameter:constraint}/literal and /{something:constraint}/literal
    ///
    /// To calculate the precedence we sort the list of routes as follows:
    /// * Shorter routes go first.
    /// * A literal wins over a parameter in precedence.
    /// * For literals with different values (case insensitive) we choose the lexical order
    /// * For parameters with different numbers of constraints, the one with more wins
    /// If we get to the end of the comparison routing we've detected an ambiguous pair of routes.
    /// </summary>
    internal static int RouteComparison(InboundRouteEntry x, InboundRouteEntry y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        var xTemplate = x.RouteTemplate;
        var yTemplate = y.RouteTemplate;
        var xPrecedence = RoutePrecedence.ComputeInbound(xTemplate);
        var yPrecedence = RoutePrecedence.ComputeInbound(yTemplate);

        return (yPrecedence.CompareTo(xPrecedence)) switch
        {
            -1 => 1,
            1 => -1,
            0 => 0,
            _ => throw new InvalidOperationException("Invalid comparison result."),
        };
    }
}
