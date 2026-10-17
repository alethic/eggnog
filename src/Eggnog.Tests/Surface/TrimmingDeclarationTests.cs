using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Surface
{

    /// <summary>
    /// The library declares itself trimmable, and keeps what its one reflection-based check needs under trimming: the
    /// interfaces of a type passed to <see cref="ValueStore.Register{T}"/>, through which it refuses a sort. CI publishes
    /// <c>src/Eggnog.AotCheck</c> with NativeAOT to check the rest; these tests catch a declaration or an annotation
    /// removed from the source.
    /// </summary>
    public sealed class TrimmingDeclarationTests
    {

        /// <summary>
        /// The library's assembly.
        /// </summary>
        static readonly Assembly Library = typeof(EGraph).Assembly;

        /// <summary>
        /// The project's <c>IsAotCompatible</c> makes the SDK mark the assembly trimmable.
        /// </summary>
        [Fact]
        public void TheLibraryDeclaresItselfTrimmable()
        {
            var metadata = Library.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == "IsTrimmable");

            Assert.NotNull(metadata);
            Assert.Equal("True", metadata.Value);
        }

        /// <summary>
        /// The trimmer keeps the interfaces of every type a program registers, so <see cref="ValueStore.Register{T}"/> still
        /// refuses a sort.
        /// </summary>
        [Fact]
        public void RegisterKeepsTheInterfacesOfItsTypeArgument()
        {
            var register = typeof(ValueStore).GetMethod(nameof(ValueStore.Register))!;
            var annotation = register.GetGenericArguments().Single().GetCustomAttribute<DynamicallyAccessedMembersAttribute>();

            Assert.NotNull(annotation);
            Assert.True(annotation.MemberTypes.HasFlag(DynamicallyAccessedMemberTypes.Interfaces));
        }

        /// <summary>
        /// The sort test asks the trimmer for the interfaces it reads.
        /// </summary>
        [Fact]
        public void IsSortKeepsTheInterfacesOfItsArgument()
        {
            var isSort = typeof(ValueStore).GetMethod(nameof(ValueStore.IsSort), BindingFlags.NonPublic | BindingFlags.Static)!;
            var annotation = isSort.GetParameters().Single().GetCustomAttribute<DynamicallyAccessedMembersAttribute>();

            Assert.NotNull(annotation);
            Assert.True(annotation.MemberTypes.HasFlag(DynamicallyAccessedMemberTypes.Interfaces));
        }

        /// <summary>
        /// A sort implements <see cref="ISort{TSort}"/>; a set is a <see cref="ValueSet{T}"/>; nothing else is either.
        /// </summary>
        [Fact]
        public void IsSortAndIsSetTellTypesApart()
        {
            Assert.True(ValueStore.IsSort(typeof(Node)));
            Assert.False(ValueStore.IsSort(typeof(long)));
            Assert.False(ValueStore.IsSort(typeof(ValueSet<long>)));
            Assert.False(ValueStore.IsSort(typeof(ClassId)));

            Assert.True(ValueStore.IsSet(typeof(ValueSet<long>)));
            Assert.True(ValueStore.IsSet(typeof(ValueSet<string>)));
            Assert.False(ValueStore.IsSet(typeof(long)));
            Assert.False(ValueStore.IsSet(typeof(Node)));
        }

        /// <summary>
        /// The hint of an unknown-type message names the method that registers each kind of type.
        /// </summary>
        [Fact]
        public void RegisterMethodNamesTheMethodForEachKindOfType()
        {
            Assert.Equal("EGraph.DefineSort", ValueStore.RegisterMethod(typeof(Node)));
            Assert.Equal("ValueStore.RegisterSet", ValueStore.RegisterMethod(typeof(ValueSet<long>)));
            Assert.Equal("ValueStore.RegisterEnum", ValueStore.RegisterMethod(typeof(DayOfWeek)));
            Assert.Equal("ValueStore.Register", ValueStore.RegisterMethod(typeof(Guid)));
        }

    }

}
