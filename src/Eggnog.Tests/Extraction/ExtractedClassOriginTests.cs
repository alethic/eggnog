using System;
using System.Collections.Generic;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Extraction
{

    /// <summary>
    /// The classes of an extracted term carry the origin of the scope that extracted it: they may be used in that scope
    /// and its descendants, and are refused in its parent, in its siblings, and once it is disposed. So do the classes
    /// a per-row cost function reads.
    /// </summary>
    public sealed class ExtractedClassOriginTests
    {

        [Fact]
        public void AnExtractedTermsClassesCarryTheExtractingScopesOrigin()
        {
            var t = new NodeTheory();
            var wrapped = t.Root.Make(t.Wrap.Of(t.Lit.Of(1L)));
            t.Root.Freeze();
            var one = t.Root.CreateChild();
            var two = t.Root.CreateChild();

            var term = one.Extract(wrapped);
            var whole = new Node(term.Class);
            var part = new Node(term.Child(0).Class);

            // Usable where it was extracted and below.
            Assert.Equal(one.Find(wrapped), one.Find(whole));
            Assert.True(one.CreateChild().AreEqual(part, t.Lit.Of(1L)));

            // Refused in the parent and in a sibling, although the class itself is the root's.
            Assert.Contains($"which is not '{t.Root.Path}' or an ancestor of it", Assert.Throws<ArgumentException>(() => t.Root.Find(whole)).Message);
            Assert.Contains($"which is not '{two.Path}' or an ancestor of it", Assert.Throws<ArgumentException>(() => two.Find(part)).Message);

            // Refused as disposed outside its subtree once its scope is disposed.
            one.Dispose();
            Assert.Contains("which is disposed", Assert.Throws<ArgumentException>(() => t.Root.CreateChild().Find(whole)).Message);
        }

        /// <summary>
        /// A class a per-row cost function reads through its <c>NodeView</c> carries the extractor scope's origin, so a
        /// class read while extracting in a child is refused at the root.
        /// </summary>
        [Fact]
        public void ANodeViewClassReadInAChildCarriesTheChildsOrigin()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            var wrapped = child.Make(t.Wrap.Of(t.A.Of()));
            var seen = new List<Node>();
            var costs = CostModel.Default.With(t.Wrap, node =>
            {
                lock (seen)
                    seen.Add(node.Get<Node>(0));

                return 1;
            });

            Assert.Equal("(Wrap (A))", child.Extract(wrapped, costs).ToString());
            Assert.NotEmpty(seen);
            Assert.All(seen, n => Assert.Throws<ArgumentException>(() => t.Root.Find(n)));
            Assert.All(seen, n => Assert.Equal(child.Find(child.Make(t.A.Of())), child.Find(n)));
        }

    }

}
