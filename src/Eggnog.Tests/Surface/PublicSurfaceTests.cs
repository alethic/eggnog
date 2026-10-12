using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

using Xunit;

namespace Eggnog.Tests.Surface
{

    /// <summary>
    /// The package shows one API (D29, D44): no public or protected member of <c>Eggnog</c> mentions an engine or spike
    /// type, no public type of another packaged assembly shares a simple name with a public <c>Eggnog</c> type, and
    /// <c>Eggnog</c> references no spike assembly.
    /// </summary>
    public sealed class PublicSurfaceTests
    {

        static readonly Assembly Library = typeof(EGraph).Assembly;

        /// <summary>
        /// The spike's assembly name, assembled from its parts so that a text search of this project for the spike's name
        /// finds nothing (R1a's done-when).
        /// </summary>
        static readonly string Spike = string.Join(".", "Eggnog", "Storage");

        static readonly string[] Hidden = { "Eggnog.Relations", Spike };

        [Fact]
        public void NoPublicMemberMentionsAnEngineOrSpikeType()
        {
            var leaks = new List<string>();
            foreach (var type in Library.GetExportedTypes())
            {
                if (type.BaseType is { } baseType && Mentions(baseType))
                    leaks.Add($"{type.FullName}: base {baseType}");

                foreach (var face in type.GetInterfaces().Where(Mentions))
                    leaks.Add($"{type.FullName}: interface {face}");

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var member in type.GetMembers(flags))
                {
                    if (IsVisible(member) == false)
                        continue;

                    foreach (var mentioned in Signature(member).Where(Mentions))
                        leaks.Add($"{type.FullName}.{member.Name}: {mentioned}");
                }
            }

            Assert.Empty(leaks);
            Assert.NotEmpty(Library.GetExportedTypes());
        }

        [Fact]
        public void NoPackagedAssemblyExportsATypeNamedLikeALibraryType()
        {
            var names = new HashSet<string>(Library.GetExportedTypes().Select(SimpleName), StringComparer.Ordinal);
            var clashes = new List<string>();
            foreach (var reference in Library.GetReferencedAssemblies().Where(r => r.Name is { } n && n.StartsWith("Eggnog", StringComparison.Ordinal)))
            {
                foreach (var type in Assembly.Load(reference).GetExportedTypes())
                {
                    var browsable = type.GetCustomAttribute<EditorBrowsableAttribute>();
                    if (browsable?.State == EditorBrowsableState.Never)
                        continue;

                    if (names.Contains(SimpleName(type)))
                        clashes.Add($"{reference.Name}: {type.FullName}");
                }
            }

            Assert.Empty(clashes);
        }

        [Fact]
        public void TheLibraryReferencesTheEngineAndNotTheSpike()
        {
            var references = Library.GetReferencedAssemblies().Select(r => r.Name).ToArray();
            Assert.Contains("Eggnog.Relations", references);
            Assert.DoesNotContain(Spike, references);
            Assert.Empty(typeof(Relations.Value).Assembly.GetExportedTypes());
        }

        /// <summary>
        /// Gets whether a type, or any type it is built from, belongs to the engine or the spike.
        /// </summary>
        static bool Mentions(Type type)
        {
            if (type.HasElementType)
                return Mentions(type.GetElementType()!);

            if (type.IsGenericParameter)
                return false;

            if (Hidden.Contains(type.Assembly.GetName().Name))
                return true;

            return type.IsGenericType && type.GetGenericArguments().Any(Mentions);
        }

        /// <summary>
        /// Gets whether a member is visible outside the library: public or protected.
        /// </summary>
        static bool IsVisible(MemberInfo member) => member switch
        {
            MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
            FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
            PropertyInfo p => p.GetAccessors(true).Any(a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
            EventInfo e => e.AddMethod is { } add && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly),
            Type t => t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem,
            _ => false,
        };

        /// <summary>
        /// Gets the types a visible member's signature mentions.
        /// </summary>
        static IEnumerable<Type> Signature(MemberInfo member)
        {
            switch (member)
            {
                case MethodInfo method:
                    yield return method.ReturnType;
                    foreach (var parameter in method.GetParameters())
                        yield return parameter.ParameterType;

                    foreach (var argument in method.IsGenericMethodDefinition ? method.GetGenericArguments().SelectMany(a => a.GetGenericParameterConstraints()) : Type.EmptyTypes)
                        yield return argument;

                    break;
                case ConstructorInfo constructor:
                    foreach (var parameter in constructor.GetParameters())
                        yield return parameter.ParameterType;

                    break;
                case PropertyInfo property:
                    yield return property.PropertyType;
                    foreach (var parameter in property.GetIndexParameters())
                        yield return parameter.ParameterType;

                    break;
                case FieldInfo field:
                    yield return field.FieldType;
                    break;
                case EventInfo evt when evt.EventHandlerType is { } handler:
                    yield return handler;
                    break;
                case Type nested:
                    yield return nested;
                    break;
            }
        }

        /// <summary>
        /// Gets a type's simple name without its generic arity: <c>Relation`2</c> is <c>Relation</c>.
        /// </summary>
        static string SimpleName(Type type)
        {
            var name = type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            return tick < 0 ? name : name.Substring(0, tick);
        }

    }

}
