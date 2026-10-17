using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Benchmarks.Probes
{

    /// <summary>
    /// Drives one benchmark case of this app outside BenchmarkDotNet, with the life cycle BenchmarkDotNet gives it: the
    /// parameters set on a fresh instance, the global setup, then per invocation the iteration setup, the benchmark
    /// method and the iteration cleanup, and the global cleanup at the end. Setup and cleanup methods apply when they
    /// name no target or name the benchmark method.
    /// </summary>
    internal sealed class BenchmarkInvoker
    {

        readonly object instance;
        readonly MethodInfo method;
        readonly Func<object?> call;
        readonly MethodInfo[] globalSetups;
        readonly MethodInfo[] iterationSetups;
        readonly MethodInfo[] iterationCleanups;
        readonly MethodInfo[] globalCleanups;
        bool setUp;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="instance">The benchmark class's instance, its parameters set.</param>
        /// <param name="method">The benchmark method.</param>
        /// <param name="parameters">The parameters set, by name.</param>
        BenchmarkInvoker(object instance, MethodInfo method, IReadOnlyList<KeyValuePair<string, object?>> parameters)
        {
            this.instance = instance;
            this.method = method;
            call = Bind(instance, method);
            Parameters = parameters;
            var methods = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
            globalSetups = Targeting<GlobalSetupAttribute>(methods, method.Name);
            iterationSetups = Targeting<IterationSetupAttribute>(methods, method.Name);
            iterationCleanups = Targeting<IterationCleanupAttribute>(methods, method.Name);
            globalCleanups = Targeting<GlobalCleanupAttribute>(methods, method.Name);
            OperationsPerInvoke = method.GetCustomAttribute<BenchmarkAttribute>()!.OperationsPerInvoke;
        }

        /// <summary>
        /// Gets the case's name: <c>Class.Method</c>.
        /// </summary>
        public string Name => instance.GetType().Name + "." + method.Name;

        /// <summary>
        /// Gets the parameters set, by name, in the class's declaration order.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, object?>> Parameters { get; }

        /// <summary>
        /// Gets the operations one invocation counts as (<see cref="BenchmarkAttribute.OperationsPerInvoke"/>).
        /// </summary>
        public int OperationsPerInvoke { get; }

        /// <summary>
        /// Finds a benchmark method in <paramref name="assembly"/> and prepares an instance of its class with the given
        /// parameter values. A parameter not given takes its first value: the first of <see cref="ParamsAttribute"/>,
        /// the first of <see cref="ParamsSourceAttribute"/>'s source, or <c>false</c> or the first enum value under
        /// <see cref="ParamsAllValuesAttribute"/>.
        /// </summary>
        /// <param name="assembly">The assembly of benchmark classes.</param>
        /// <param name="name"><c>Class.Method</c>, or <c>Class</c> when the class has one benchmark method; the class
        /// by its name without the namespace.</param>
        /// <param name="values">The parameter values given, by name, as text.</param>
        /// <returns>The invoker.</returns>
        /// <exception cref="ArgumentException">No class or method has that name, the class has several benchmark methods
        /// and none was named, a value names no parameter, or a value does not convert to its parameter's type.</exception>
        public static BenchmarkInvoker Create(Assembly assembly, string name, IReadOnlyDictionary<string, string> values)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(values);

            var classes = assembly.GetTypes()
                .Where(t => t.IsClass && t.IsAbstract == false && t.GetMethods().Any(m => m.IsDefined(typeof(BenchmarkAttribute))))
                .ToList();
            var dot = name.LastIndexOf('.');
            var type = classes.FirstOrDefault(t => t.Name == name)
                ?? (dot > 0 ? classes.FirstOrDefault(t => t.Name == name.Substring(0, dot)) : null)
                ?? throw new ArgumentException($"No benchmark class is named '{name}'. The classes: {string.Join(", ", classes.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal))}.", nameof(name));

            var benchmarks = type.GetMethods().Where(m => m.IsDefined(typeof(BenchmarkAttribute))).ToList();
            var methodName = type.Name == name ? null : name.Substring(dot + 1);
            var method = methodName == null
                ? benchmarks.Count == 1 ? benchmarks[0] : throw new ArgumentException($"{type.Name} has several benchmark methods; name one: {string.Join(", ", benchmarks.Select(m => m.Name))}.", nameof(name))
                : benchmarks.FirstOrDefault(m => m.Name == methodName) ?? throw new ArgumentException($"{type.Name} has no benchmark method '{methodName}'; it has: {string.Join(", ", benchmarks.Select(m => m.Name))}.", nameof(name));

            var instance = Activator.CreateInstance(type)!;
            var members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m is PropertyInfo or FieldInfo)
                .Where(m => m.IsDefined(typeof(ParamsAttribute)) || m.IsDefined(typeof(ParamsSourceAttribute)) || m.IsDefined(typeof(ParamsAllValuesAttribute)))
                .OrderBy(m => m.MetadataToken)
                .ToList();

            foreach (var given in values.Keys)
                if (members.Any(m => m.Name == given) == false)
                    throw new ArgumentException($"{type.Name} has no parameter '{given}'; it has: {string.Join(", ", members.Select(m => m.Name))}.", nameof(values));

            var set = new List<KeyValuePair<string, object?>>();
            foreach (var member in members)
            {
                var memberType = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
                var value = values.TryGetValue(member.Name, out var text) ? Convert(text, memberType, member.Name) : FirstValue(instance, member, memberType);
                if (member is PropertyInfo p)
                    p.SetValue(instance, value);
                else
                    ((FieldInfo)member).SetValue(instance, value);

                set.Add(new KeyValuePair<string, object?>(member.Name, value));
            }

            return new BenchmarkInvoker(instance, method, set);
        }

        /// <summary>
        /// Runs the global setup, once.
        /// </summary>
        public void GlobalSetup()
        {
            if (setUp)
                return;

            Invoke(globalSetups);
            setUp = true;
        }

        /// <summary>
        /// Runs the iteration setup.
        /// </summary>
        public void IterationSetup() => Invoke(iterationSetups);

        /// <summary>
        /// Runs the benchmark method once, through a delegate bound to it when the invoker was created, so the call
        /// allocates nothing of its own beyond the boxing of a result of a value type (24 bytes for an
        /// <see cref="int"/>). Reflection's invoker, which allocates on its first calls while it prepares itself, is not
        /// involved; the first call still compiles the method, which a warm-up invocation absorbs.
        /// </summary>
        /// <returns>The method's result, kept alive by the caller until it is no longer measured.</returns>
        public object? Invoke() => Unwrap(call);

        /// <summary>
        /// Runs the iteration cleanup.
        /// </summary>
        public void IterationCleanup() => Invoke(iterationCleanups);

        /// <summary>
        /// Runs the global cleanup.
        /// </summary>
        public void GlobalCleanup() => Invoke(globalCleanups);

        /// <summary>
        /// Binds the benchmark method to <paramref name="instance"/> as a delegate that returns its result as an
        /// <see cref="object"/>, <c>null</c> for a method that returns nothing.
        /// </summary>
        /// <param name="instance">The benchmark class's instance.</param>
        /// <param name="method">The benchmark method, which takes no arguments.</param>
        /// <returns>The call.</returns>
        static Func<object?> Bind(object instance, MethodInfo method)
        {
            if (method.ReturnType == typeof(void))
            {
                var action = method.CreateDelegate<Action>(instance);
                return () =>
                {
                    action();
                    return null;
                };
            }

            var bind = typeof(BenchmarkInvoker).GetMethod(nameof(BindResult), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(method.ReturnType);
            return (Func<object?>)bind.Invoke(null, [instance, method])!;
        }

        /// <summary>
        /// Binds a benchmark method that returns a <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The method's return type.</typeparam>
        /// <param name="instance">The benchmark class's instance.</param>
        /// <param name="method">The benchmark method.</param>
        /// <returns>The call, which boxes a result of a value type.</returns>
        static Func<object?> BindResult<T>(object instance, MethodInfo method)
        {
            var function = method.CreateDelegate<Func<T>>(instance);
            return () => function();
        }

        /// <summary>
        /// Gets the methods with attribute <typeparamref name="T"/> that apply to <paramref name="benchmark"/>.
        /// </summary>
        /// <typeparam name="T">The life-cycle attribute.</typeparam>
        /// <param name="methods">The class's public instance methods.</param>
        /// <param name="benchmark">The benchmark method's name.</param>
        /// <returns>The methods, in declaration order.</returns>
        static MethodInfo[] Targeting<T>(MethodInfo[] methods, string benchmark)
            where T : TargetedAttribute
        {
            return methods
                .Where(m => m.GetCustomAttribute<T>() is { } a && (a.Targets == null || a.Targets.Length == 0 || a.Targets.Contains(benchmark)))
                .OrderBy(m => m.MetadataToken)
                .ToArray();
        }

        /// <summary>
        /// Gets the first value BenchmarkDotNet would give a parameter.
        /// </summary>
        /// <param name="instance">The instance, for a source member that is not static.</param>
        /// <param name="member">The parameter.</param>
        /// <param name="type">The parameter's type.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The parameter's source has no value.</exception>
        static object? FirstValue(object instance, MemberInfo member, Type type)
        {
            if (member.GetCustomAttribute<ParamsAttribute>() is { } values)
                return values.Values.Length > 0 ? values.Values[0] : throw new ArgumentException($"{member.Name} has no values.");

            if (member.GetCustomAttribute<ParamsSourceAttribute>() is { } source)
            {
                var owner = source.Type ?? instance.GetType();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                var provider = (MemberInfo?)owner.GetProperty(source.Name, flags) ?? owner.GetMethod(source.Name, flags, Type.EmptyTypes)
                    ?? (MemberInfo?)owner.GetField(source.Name, flags) ?? throw new ArgumentException($"{member.Name}'s source '{source.Name}' does not exist.");
                var target = owner == instance.GetType() ? instance : null;
                var result = provider switch
                {
                    PropertyInfo p => p.GetValue(target),
                    MethodInfo m => m.Invoke(target, null),
                    _ => ((FieldInfo)provider).GetValue(target),
                };

                foreach (var first in (IEnumerable)result!)
                    return first;

                throw new ArgumentException($"{member.Name}'s source '{source.Name}' has no values.");
            }

            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(bool))
                return false;

            if (underlying.IsEnum)
                return Enum.GetValues(underlying).GetValue(0);

            throw new ArgumentException($"{member.Name} is neither a bool nor an enum, so it has no values to take all of.");
        }

        /// <summary>
        /// Converts a parameter value given as text.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="type">The parameter's type.</param>
        /// <param name="name">The parameter's name, for the error message.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The text does not convert.</exception>
        static object? Convert(string text, Type type, string name)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            try
            {
                if (underlying.IsEnum)
                    return Enum.Parse(underlying, text, ignoreCase: true);

                return System.Convert.ChangeType(text, underlying, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException)
            {
                throw new ArgumentException($"'{text}' is not a value of {name} ({underlying.Name}).", nameof(text), e);
            }
        }

        /// <summary>
        /// Runs each method on the instance.
        /// </summary>
        /// <param name="methods">The methods.</param>
        void Invoke(MethodInfo[] methods)
        {
            foreach (var m in methods)
                Unwrap(() => m.Invoke(instance, null));
        }

        /// <summary>
        /// Runs a call, rethrowing the exception a reflected method threw instead of its wrapper.
        /// </summary>
        /// <param name="call">The call.</param>
        /// <returns>The call's result.</returns>
        static object? Unwrap(Func<object?> call)
        {
            try
            {
                return call();
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

    }

}
