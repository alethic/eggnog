using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Xunit;

namespace Eggnog.Tests.Surface
{

    /// <summary>
    /// The package shows one API: no public or protected member of <c>Eggnog</c> mentions an engine type, the engine is
    /// part of <c>Eggnog</c> and exports nothing, and <c>Eggnog</c> references no other Eggnog assembly. And the public
    /// surface is exactly the one <c>Surface/PublicSurface.txt</c> lists.
    /// </summary>
    public sealed class PublicSurfaceTests
    {

        static readonly Assembly Library = typeof(EGraph).Assembly;

        /// <summary>
        /// The engine's namespace. The engine is compiled into the library's assembly (<c>src/Eggnog/Relations</c>), so
        /// its types are told apart by namespace, not by assembly.
        /// </summary>
        const string EngineNamespace = "Eggnog.Relations";

        [Fact]
        public void NoPublicMemberMentionsAnEngineType()
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

        /// <summary>
        /// The public surface is exactly the listing in <c>Surface/PublicSurface.txt</c>: every public type and member,
        /// and nothing else. After a deliberate change of the surface, set <c>EGGNOG_SURFACE</c> to a file path to
        /// write the listing there, and replace <c>Surface/PublicSurface.txt</c> with it.
        /// </summary>
        [Fact]
        public void TheSurfaceIsExactlyTheListedOne()
        {
            var actual = SurfaceListing.Of(Library);
            if (Environment.GetEnvironmentVariable("EGGNOG_SURFACE") is { Length: > 0 } path)
                File.WriteAllText(path, string.Join("\n", actual) + "\n");

            using var stream = typeof(PublicSurfaceTests).Assembly.GetManifestResourceStream("Eggnog.Tests.Surface.PublicSurface.txt")
                ?? throw new InvalidOperationException("The expected surface is not embedded.");
            using var reader = new StreamReader(stream);
            var expected = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

            var missing = expected.Except(actual).ToArray();
            var extra = actual.Except(expected).ToArray();
            Assert.True(missing.Length == 0 && extra.Length == 0,
                "Missing from the library:\n  " + string.Join("\n  ", missing) + "\nNot in the listed surface:\n  " + string.Join("\n  ", extra));
            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// The package holds one assembly: the engine is compiled into the library, exports nothing, and the library
        /// references no other Eggnog assembly, so no other packaged assembly can show types of its own.
        /// </summary>
        [Fact]
        public void TheLibraryContainsTheEngineAndReferencesNoOtherEggnogAssembly()
        {
            Assert.Same(Library, typeof(Relations.Value).Assembly);
            Assert.Empty(Library.GetExportedTypes().Where(IsEngine).Select(t => t.FullName));

            var references = Library.GetReferencedAssemblies().Select(r => r.Name ?? "").ToArray();
            Assert.DoesNotContain(references, n => n.StartsWith("Eggnog", StringComparison.Ordinal));
        }

        /// <summary>
        /// Gets whether a type, or any type it is built from, belongs to the engine.
        /// </summary>
        static bool Mentions(Type type)
        {
            if (type.HasElementType)
                return Mentions(type.GetElementType()!);

            if (type.IsGenericParameter)
                return false;

            if (IsEngine(type))
                return true;

            return type.IsGenericType && type.GetGenericArguments().Any(Mentions);
        }

        /// <summary>
        /// Gets whether a type is declared in the engine's namespace or below it.
        /// </summary>
        static bool IsEngine(Type type) =>
            type.Namespace is { } ns && (ns == EngineNamespace || ns.StartsWith(EngineNamespace + ".", StringComparison.Ordinal));

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

    }

}
