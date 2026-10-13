using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Xunit;

namespace Eggnog.Tests.Surface
{

    /// <summary>
    /// Writes the public surface of the library to the file <c>EGGNOG_API_DUMP</c> names, every exported type with its
    /// public and protected members, to read the API by hand; does nothing when the variable is not set.
    /// </summary>
    public sealed class PublicSurfaceDumpTests
    {

        /// <summary>
        /// Writes the dump when <c>EGGNOG_API_DUMP</c> is set.
        /// </summary>
        [Fact]
        public void DumpPublicSurface()
        {
            var path = Environment.GetEnvironmentVariable("EGGNOG_API_DUMP");
            if (string.IsNullOrEmpty(path))
                return;

            var sb = new StringBuilder();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in typeof(EGraph).Assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : typeof(Delegate).IsAssignableFrom(type) ? "delegate" : "class";
                var mods = (type.IsAbstract && type.IsSealed) ? "static " : type.IsAbstract && type.IsInterface == false ? "abstract " : type.IsSealed && type.IsValueType == false ? "sealed " : "";
                sb.Append(mods).Append(kind).Append(' ').Append(Name(type));
                if (type.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum) && b != typeof(MulticastDelegate))
                    sb.Append(" : ").Append(Name(b));
                foreach (var i in type.GetInterfaces())
                    sb.Append(", ").Append(Name(i));
                sb.AppendLine();
                if (type.IsEnum)
                {
                    foreach (var n in Enum.GetNames(type))
                        sb.Append("    ").AppendLine(n);
                    continue;
                }

                foreach (var m in type.GetMembers(flags).OrderBy(m => m.MemberType.ToString()).ThenBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.ToString(), StringComparer.Ordinal))
                {
                    var line = Describe(m);
                    if (line is not null)
                        sb.Append("    ").AppendLine(line);
                }
            }

            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>
        /// Renders a member on one line, or <c>null</c> for a member outside the public surface.
        /// </summary>
        /// <param name="m">The member.</param>
        /// <returns>The line, or <c>null</c>.</returns>
        static string? Describe(MemberInfo m)
        {
            switch (m)
            {
                case ConstructorInfo c when c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly:
                    return Access(c) + "ctor(" + Params(c.GetParameters()) + ")";
                case MethodInfo mi when (mi.IsPublic || mi.IsFamily || mi.IsFamilyOrAssembly) && mi.IsSpecialName == false:
                    return Access(mi) + (mi.IsStatic ? "static " : "") + (mi.IsAbstract ? "abstract " : mi.IsVirtual && mi.GetBaseDefinition() != mi ? "override " : mi.IsVirtual && mi.IsFinal == false ? "virtual " : "")
                        + Name(mi.ReturnType) + " " + mi.Name + (mi.IsGenericMethodDefinition ? "<" + string.Join(", ", mi.GetGenericArguments().Select(Constraint)) + ">" : "") + "(" + Params(mi.GetParameters()) + ")";
                case MethodInfo op when (op.IsPublic) && op.IsSpecialName && op.Name.StartsWith("op_", StringComparison.Ordinal):
                    return "static " + Name(op.ReturnType) + " " + op.Name + "(" + Params(op.GetParameters()) + ")";
                case PropertyInfo p:
                    var acc = p.GetAccessors(true).Where(a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly).ToArray();
                    if (acc.Length == 0)
                        return null;
                    var parts = acc.Select(a => (a.IsFamily ? "protected " : "") + (a == p.GetMethod ? "get" : p.SetMethod is { } s && s.ReturnParameter.GetRequiredCustomModifiers().Any(t => t.Name == "IsExternalInit") ? "init" : "set"));
                    var stat = acc[0].IsStatic ? "static " : "";
                    var idx = p.GetIndexParameters();
                    return stat + Name(p.PropertyType) + " " + (idx.Length > 0 ? "this[" + Params(idx) + "]" : p.Name) + " { " + string.Join("; ", parts) + " }";
                case FieldInfo f when f.IsPublic || f.IsFamily:
                    return "field " + Name(f.FieldType) + " " + f.Name;
                case EventInfo e:
                    return "event " + e.Name;
                case Type t when t.IsNestedPublic || t.IsNestedFamily:
                    return "NESTED " + t.Name;
            }

            return null;
        }

        /// <summary>
        /// The accessibility a method or constructor shows beyond public: protected or protected internal.
        /// </summary>
        /// <param name="m">The method or constructor.</param>
        /// <returns>The modifier and a space, or empty.</returns>
        static string Access(MethodBase m) => m.IsFamily ? "protected " : m.IsFamilyOrAssembly ? "protected internal " : "";

        /// <summary>
        /// Renders a generic parameter with its constraints.
        /// </summary>
        /// <param name="g">The generic parameter.</param>
        /// <returns>The rendering.</returns>
        static string Constraint(Type g)
        {
            var cs = g.GetGenericParameterConstraints().Select(Name).ToList();
            var a = g.GenericParameterAttributes;
            if ((a & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                cs.Insert(0, "struct");
            else if ((a & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                cs.Insert(0, "class");
            cs.Remove("ValueType");
            return g.Name + (cs.Count > 0 ? " : " + string.Join(", ", cs) : "");
        }

        /// <summary>
        /// Renders a parameter list.
        /// </summary>
        /// <param name="ps">The parameters.</param>
        /// <returns>The rendering.</returns>
        static string Params(ParameterInfo[] ps) => string.Join(", ", ps.Select(p =>
            (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + (p.GetCustomAttribute<ParamArrayAttribute>() is not null ? "params " : "")
            + Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : "")));

        /// <summary>
        /// Renders a type as C# names it: generic arguments, arrays, by-reference and nullable types.
        /// </summary>
        /// <param name="t">The type.</param>
        /// <returns>The rendering.</returns>
        static string Name(Type t)
        {
            if (t.IsGenericParameter)
                return t.Name;
            if (t.IsArray)
                return Name(t.GetElementType()!) + "[]";
            if (t.IsByRef)
                return Name(t.GetElementType()!) + "&";
            if (Nullable.GetUnderlyingType(t) is { } u)
                return Name(u) + "?";
            var n = t.Name;
            var tick = n.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
                n = n.Substring(0, tick);
            if (t.IsGenericType)
                n += "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
            return n;
        }

    }

}
