using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review checks for request E11 of the rule-layer spec: the engine exports no type, and no exported type of this
    /// test assembly exposes an engine type through its base type, its interfaces or a public or protected member. The
    /// engine is compiled into the <c>Eggnog</c> assembly, so its types are those of its namespace, not of an assembly.
    /// </summary>
    public class EngineSurfaceReviewTests
    {

        static readonly Assembly Engine = typeof(Value).Assembly;

        static readonly string EngineNamespace = typeof(Value).Namespace!;

        [Fact]
        public void TheEngineExportsNoType()
        {
            Assert.Equal("Eggnog.Relations", EngineNamespace);
            Assert.Empty(Engine.GetExportedTypes().Where(IsEngine).Select(t => t.FullName));
        }

        [Fact]
        public void NoExportedTestTypeExposesAnEngineType()
        {
            var leaks = new List<string>();
            foreach (var type in typeof(EngineSurfaceReviewTests).Assembly.GetExportedTypes())
            {
                if (type.BaseType is { } baseType && Mentions(baseType))
                    leaks.Add($"{type.FullName}: base {baseType}");

                foreach (var face in type.GetInterfaces().Where(Mentions))
                    leaks.Add($"{type.FullName}: interface {face}");

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var member in type.GetMembers(flags))
                {
                    if (IsVisible(member) == false)
                        continue;

                    foreach (var mentioned in Signature(member).Where(Mentions))
                        leaks.Add($"{type.FullName}.{member.Name}: {mentioned}");
                }
            }

            Assert.Empty(leaks);
        }

        static bool IsVisible(MemberInfo member) => member switch
        {
            MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
            FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
            PropertyInfo p => p.GetAccessors(true).Any(a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
            EventInfo e => e.AddMethod is { } add && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly),
            Type t => t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem,
            _ => false,
        };

        static IEnumerable<Type> Signature(MemberInfo member)
        {
            switch (member)
            {
                case MethodInfo m:
                    yield return m.ReturnType;
                    foreach (var p in m.GetParameters())
                        yield return p.ParameterType;
                    if (m.IsGenericMethodDefinition)
                        foreach (var a in m.GetGenericArguments())
                            foreach (var c in a.GetGenericParameterConstraints())
                                yield return c;
                    break;
                case ConstructorInfo c:
                    foreach (var p in c.GetParameters())
                        yield return p.ParameterType;
                    break;
                case FieldInfo f:
                    yield return f.FieldType;
                    break;
                case PropertyInfo p:
                    yield return p.PropertyType;
                    break;
                case EventInfo e:
                    if (e.EventHandlerType is { } handler)
                        yield return handler;
                    break;
            }
        }

        static bool Mentions(Type type)
        {
            if (type.IsGenericParameter)
                return false;

            if (type.HasElementType)
                return Mentions(type.GetElementType()!);

            if (IsEngine(type))
                return true;

            return type.IsGenericType && type.GetGenericArguments().Any(Mentions);
        }

        static bool IsEngine(Type type) =>
            type.Assembly == Engine && type.Namespace is { } ns &&
            (ns == EngineNamespace || ns.StartsWith(EngineNamespace + ".", StringComparison.Ordinal));

    }

}
