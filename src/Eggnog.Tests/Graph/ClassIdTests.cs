using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Graph
{

    /// <summary>
    /// Class values carry the scope that returned them (D39, §6.6.7). In R1 every origin is the root's, so the checks
    /// that can fail are <c>default(ClassId)</c>, another graph's class, and a value of a batch that did not commit.
    /// </summary>
    public sealed class ClassIdTests
    {

        [Fact]
        public void DefaultNamesNoClassAndEveryEntryPointRefusesIt()
        {
            var t = new NodeTheory();
            var root = t.Graph.Root;
            var a = root.Make(t.A.Of());
            var none = default(Node);
            Assert.True(none.Id.IsDefault);
            Assert.False(a.Id.IsDefault);
            Assert.Equal("default(ClassId)", none.Id.ToString());

            Assert.Contains("names no class", Assert.Throws<ArgumentException>(() => root.Find(none)).Message);
            Assert.Throws<ArgumentException>(() => root.AreEqual(none, a));
            Assert.Throws<ArgumentException>(() => root.AreEqual(a, none));
            Assert.Throws<ArgumentException>(() => root.Union(none, a));
            Assert.Throws<ArgumentException>(() => root.Make(t.Wrap.Of(none)));
            Assert.Throws<ArgumentException>(() => root.Assume(t.Tag.Of(none)));
            Assert.Throws<ArgumentException>(() => root.Extract(none));
            Assert.Throws<ArgumentException>(() => root.Check(t.Tag.Of(none)));
            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("holds default", r => r.When(t.Tag.Of(none))));
            Assert.Equal("a class constant is default(ClassId), which names no class", e.Message);
            Assert.Empty(t.Rules.Rules);
            Assert.False(root.IsFaulted);
        }

        [Fact]
        public void AnotherGraphsClassIsRefused()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            var foreign = other.Graph.Root.Make(other.A.Of());
            var root = t.Graph.Root;

            var e = Assert.Throws<ArgumentException>(() => root.Find(foreign));
            Assert.Contains($"class {foreign.Id} belongs to another graph", e.Message);
            Assert.Throws<ArgumentException>(() => root.Assume(t.Tag.Of(foreign)));
            Assert.Throws<ArgumentException>(() => root.Make(t.Wrap.Of(foreign)));
            var definition = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("holds foreign", r => r.When(t.Tag.Of(foreign))));
            Assert.Equal($"class {foreign.Id} belongs to another graph", definition.Message);
        }

        [Fact]
        public void AWriteThatThrowsLeavesTheValuesItsMakeReturnedUnusable()
        {
            var t = new NodeTheory();
            var root = t.Graph.Root;
            var made = default(Node);
            Assert.Throws<InvalidOperationException>(() => root.Write(w =>
            {
                made = w.Make(t.A.Of());
                throw new InvalidOperationException("abandon the batch");
            }));

            Assert.False(made.Id.IsDefault);
            var e = Assert.Throws<ArgumentException>(() => root.Find(made));
            Assert.Contains("was made by a Write batch that did not commit", e.Message);
            Assert.False(root.Check(Fact.Exists(t.A.Of())));
            var definition = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("holds uncommitted", r => r.When(t.Tag.Of(made))));
            Assert.Equal($"class {made.Id} was made by a Write batch that did not commit", definition.Message);
        }

        [Fact]
        public void AWriteThatCommitsLeavesTheValuesItsMakeReturnedUsable()
        {
            var t = new NodeTheory();
            var root = t.Graph.Root;
            var a = default(Node);
            var wrapped = default(Node);
            root.Write(w =>
            {
                a = w.Make(t.A.Of());
                wrapped = w.Make(t.Wrap.Of(a));
                w.Assume(t.Tag.Of(wrapped));
            });

            Assert.Equal(a, root.Find(a));
            Assert.True(root.AreEqual(a, t.A.Of()));
            Assert.True(root.AreEqual(wrapped, t.Wrap.Of(t.A.Of())));
            Assert.True(root.Check(t.Tag.Of(wrapped)));
            t.Rules.DefineRule("holds committed", r => r.When(t.Tag.Of(wrapped)));
        }

        [Fact]
        public void AWriteThatChangesNothingStillCommitsItsValues()
        {
            var t = new NodeTheory();
            var root = t.Graph.Root;
            root.Make(t.A.Of());
            var a = default(Node);
            root.Write(w => a = w.Make(t.A.Of()));
            Assert.True(root.AreEqual(a, t.A.Of()));
        }

        [Fact]
        public void ValuesAreEqualByGraphAndIdentifier()
        {
            var t = new NodeTheory();
            var root = t.Graph.Root;
            var a = root.Make(t.A.Of());
            var again = root.Make(t.A.Of());
            var b = root.Make(t.B.Of());
            Assert.Equal(a.Id, again.Id);
            Assert.True(a.Id == again.Id);
            Assert.True(a.Id != b.Id);
            Assert.Equal(a.Id.GetHashCode(), again.Id.GetHashCode());
            Assert.StartsWith("#", a.Id.ToString());

            var other = new NodeTheory();
            var foreign = other.Graph.Root.Make(other.A.Of());
            Assert.NotEqual(a.Id, foreign.Id);
        }

    }

}
