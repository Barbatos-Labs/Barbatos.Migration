// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Barbatos.Migration;

/// <summary>
/// Finds migration steps by scanning an assembly, so each one can live in its own file and be
/// picked up without a registration line somewhere else that has to be kept in sync.
/// </summary>
/// <remarks>
/// <para>
/// The alternative - listing every step in a builder chain - has one advantage, which is that
/// the order is visible in one place. That advantage is smaller than it looks here: the engine
/// orders steps by version regardless of how they were registered, so the list is not what
/// decides anything. What the list does reliably produce is a merge conflict every time two
/// people add a step, and a file that grows without bound.
/// </para>
/// <para>
/// Scanning is <b>deterministic</b>: results are sorted by target version, then by id. Reflection
/// returns types in whatever order the metadata happens to be in, which is not guaranteed stable
/// across builds, and "the order steps run in" is not something to leave to chance.
/// </para>
/// </remarks>
public static class MigrationStepScanner
{
    /// <summary>
    /// Finds every concrete <see cref="IMigrationStep"/> in <paramref name="assembly"/> and
    /// creates one of each.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="factory">
    /// Creates a step from its type. Defaults to the public parameterless constructor; pass a
    /// container-backed factory to let steps take dependencies.
    /// </param>
    /// <param name="filter">An extra predicate on the discovered types, applied before construction.</param>
    /// <returns>The steps, ordered by target version and then by id.</returns>
    [RequiresUnreferencedCode(
        "Scanning an assembly for migration steps requires reflection over its types, which a trimmer cannot follow. " +
        "Register steps explicitly with AddStep when publishing trimmed.")]
    public static IReadOnlyList<IMigrationStep> Scan(
        Assembly assembly,
        Func<Type, IMigrationStep>? factory = null,
        Func<Type, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        factory ??= CreateWithParameterlessConstructor;

        return FindStepTypes(assembly, filter)
            .Select(factory)
            .OrderBy(step => step.TargetVersion)
            .ThenBy(step => step.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Finds the types <see cref="Scan"/> would construct, without constructing them - for
    /// registering them in a dependency-injection container instead.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="filter">An extra predicate on the discovered types.</param>
    [RequiresUnreferencedCode(
        "Scanning an assembly for migration steps requires reflection over its types, which a trimmer cannot follow. " +
        "Register steps explicitly with AddStep when publishing trimmed.")]
    public static IReadOnlyList<Type> FindStepTypes(Assembly assembly, Func<Type, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return GetLoadableTypes(assembly)
            .Where(IsStepType)
            .Where(type => filter == null || filter(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsStepType(Type type) =>
        typeof(IMigrationStep).IsAssignableFrom(type)
        && type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false };

    private static IMigrationStep CreateWithParameterlessConstructor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type)
    {
        try
        {
            return (IMigrationStep)Activator.CreateInstance(type)!;
        }
        catch (MissingMethodException ex)
        {
            throw new MigrationPlanException(
                $"'{type.FullName}' was discovered as a migration step but has no public parameterless constructor. " +
                "Give it one, or register it through Barbatos.Migration.DependencyInjection so the container can " +
                $"supply its dependencies. ({ex.Message})");
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw new MigrationPlanException(
                $"The constructor of migration step '{type.FullName}' threw {ex.InnerException.GetType().Name}: {ex.InnerException.Message}",
                ex.InnerException);
        }
    }

    /// <summary>
    /// Returns the types an assembly can actually load. A missing optional dependency makes
    /// <see cref="Assembly.GetTypes"/> throw for the <em>whole</em> assembly, which would turn
    /// an unrelated unreferenced package into "no migrations found" - and a migration silently
    /// not running is the failure mode worth the most effort to avoid.
    /// </summary>
    [RequiresUnreferencedCode("Enumerates every type in the assembly.")]
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
    }
}
