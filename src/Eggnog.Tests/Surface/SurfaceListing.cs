using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Eggnog.Tests.Surface
{

    /// <summary>
    /// Lists an assembly's public surface, one line per type and member, in a stable order and the same on every target
    /// framework: types by full name with their kind, base type and the interfaces a consumer relies on; then their
    /// public and protected members by kind and name.
    /// </summary>
    public static class SurfaceListing
    {

        /// <summary>
        /// The BCL interfaces a public type's listing shows; the rest (formatting, conversion, serialization, cloning)
        /// come with enums, exceptions and delegates and differ between target frameworks.
        /// </summary>
        static readonly HashSet<string> ListedInterfaces = new(StringComparer.Ordinal)
        {
            "IDisposable", "IEquatable`1", "IReadOnlyList`1", "IReadOnlyCollection`1", "IEnumerable`1", "IEnumerable",
        };

        /// <summary>
        /// Lists the public surface of <paramref name="assembly"/>.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <returns>The lines.</returns>
        public static IReadOnlyList<string> Of(Assembly assembly)
        {
            var lines = new List<string>();
            foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                lines.Add(Header(type));
                if (type.IsEnum)
                {
                    lines.AddRange(Enum.GetNames(type).Select(n => "    " + n));
                    continue;
                }

                if (typeof(Delegate).IsAssignableFrom(type))
                {
                    var invoke = type.GetMethod("Invoke")!;
                    lines.Add("    " + Name(invoke.ReturnType) + " Invoke(" + Parameters(invoke.GetParameters()) + ")");
                    continue;
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                lines.AddRange(type.GetMembers(flags).Select(Member).Where(l => l is not null).Select(l => "    " + l!).OrderBy(l => l, StringComparer.Ordinal));
            }

            return lines;
        }

        /// <summary>
        /// A type's line: modifiers, kind, name, base type and listed interfaces.
        /// </summary>
        static string Header(Type type)
        {
            var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : typeof(Delegate).IsAssignableFrom(type) ? "delegate" : "class";
            var modifiers = type.IsInterface || type.IsValueType || typeof(Delegate).IsAssignableFrom(type) ? ""
                : type.IsAbstract && type.IsSealed ? "static " : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : "";
            var builder = new StringBuilder(modifiers).Append(kind).Append(' ').Append(type.Namespace).Append('.').Append(Name(type));
            if (type.IsByRefLike)
                builder.Insert(0, "ref ");

            var bases = new List<string>();
            if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum)
                && baseType != typeof(MulticastDelegate))
                bases.Add(Name(baseType));

            bases.AddRange(type.GetInterfaces().Where(i => (i.IsPublic && i.Namespace?.StartsWith("Eggnog", StringComparison.Ordinal) == true) || ListedInterfaces.Contains(i.Name))
                .Select(Name).OrderBy(n => n, StringComparer.Ordinal));
            if (bases.Count > 0)
                builder.Append(" : ").Append(string.Join(", ", bases));

            return builder.ToString();
        }

        /// <summary>
        /// A member's line, or <c>null</c> when it is not public or protected (or is an accessor or a
        /// compiler-generated member).
        /// </summary>
        static string? Member(MemberInfo member)
        {
            switch (member)
            {
                case ConstructorInfo constructor when Visible(constructor):
                    return Access(constructor) + "ctor(" + Parameters(constructor.GetParameters()) + ")";
                case MethodInfo method when Visible(method) && (method.IsSpecialName == false || method.Name.StartsWith("op_", StringComparison.Ordinal)):
                {
                    var modifiers = (method.IsStatic ? "static " : "") + (method.IsAbstract ? "abstract " : method.GetBaseDefinition() != method ? "override " : method.IsVirtual && method.IsFinal == false ? "virtual " : "");
                    var generics = method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(Constraint)) + ">" : "";
                    return Access(method) + modifiers + Name(method.ReturnType) + " " + method.Name + generics + "(" + Parameters(method.GetParameters()) + ")";
                }
                case PropertyInfo property:
                {
                    var accessors = property.GetAccessors(true).Where(Visible).ToArray();
                    if (accessors.Length == 0)
                        return null;

                    var parts = accessors.OrderBy(a => a == property.GetMethod ? 0 : 1)
                        .Select(a => Access(a) + (a == property.GetMethod ? "get" : a.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") ? "init" : "set"));
                    var indexers = property.GetIndexParameters();
                    var name = indexers.Length > 0 ? "this[" + Parameters(indexers) + "]" : property.Name;
                    return (accessors[0].IsStatic ? "static " : "") + Name(property.PropertyType) + " " + name + " { " + string.Join("; ", parts) + " }";
                }
                case FieldInfo field when field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly:
                    return (field.IsStatic ? "static " : "") + "field " + Name(field.FieldType) + " " + field.Name;
                case EventInfo e when e.AddMethod is { } add && Visible(add):
                    return "event " + Name(e.EventHandlerType!) + " " + e.Name;
                case Type nested when nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem:
                    return "nested " + nested.Name;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Gets whether a method is visible outside the assembly.
        /// </summary>
        static bool Visible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

        /// <summary>
        /// The access of a protected member.
        /// </summary>
        static string Access(MethodBase method) => method.IsFamily ? "protected " : method.IsFamilyOrAssembly ? "protected internal " : "";

        /// <summary>
        /// A generic parameter with its constraints.
        /// </summary>
        static string Constraint(Type parameter)
        {
            var constraints = parameter.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(Name).ToList();
            var attributes = parameter.GenericParameterAttributes;
            if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                constraints.Insert(0, "struct");
            else if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                constraints.Insert(0, "class");

            return parameter.Name + (constraints.Count > 0 ? " : " + string.Join(", ", constraints) : "");
        }

        /// <summary>
        /// Gets whether a parameter is <c>params</c>: an array's <see cref="ParamArrayAttribute"/>, or the
        /// <c>ParamCollectionAttribute</c> of a span or other collection, which the compiler embeds where the runtime lacks
        /// it, so it is matched by name.
        /// </summary>
        static bool IsParams(ParameterInfo parameter) => parameter.CustomAttributes.Any(a =>
            a.AttributeType.FullName is "System.ParamArrayAttribute" or "System.Runtime.CompilerServices.ParamCollectionAttribute");

        /// <summary>
        /// A parameter list.
        /// </summary>
        static string Parameters(ParameterInfo[] parameters) => string.Join(", ", parameters.Select(p =>
            (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + (IsParams(p) ? "params " : "")
            + Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType) + " " + p.Name
            + (p.HasDefaultValue ? " = " + (p.DefaultValue switch { null => "null", string s => "\"" + s + "\"", var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) }) : "")));

        /// <summary>
        /// A type's name, with generic arguments: <c>Relation&lt;T1, T2&gt;</c>, <c>Nullable</c> as <c>T?</c>.
        /// </summary>
        static string Name(Type type)
        {
            if (type.IsGenericParameter)
                return type.Name;

            if (type.IsArray)
                return Name(type.GetElementType()!) + "[]";

            if (type.IsByRef)
                return Name(type.GetElementType()!) + "&";

            if (Nullable.GetUnderlyingType(type) is { } underlying)
                return Name(underlying) + "?";

            var name = type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
                name = name.Substring(0, tick);

            return type.IsGenericType ? name + "<" + string.Join(", ", type.GetGenericArguments().Select(Name)) + ">" : name;
        }

    }

}
