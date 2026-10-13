using System;

using Xunit;

using static Eggnog.Relations.Tests.ActionTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests from the adversarial review of the actions milestone. Each one exposes a defect and fails until it is fixed.
    /// </summary>
    public class ActionsReviewTests
    {

        /// <summary>
        /// egglog checks on every later push that each used variable is mapped (a <c>debug_assert</c>,
        /// core-relations/src/action/mod.rs:214-224). The port checks only the first push, so a later push whose used
        /// list names a variable the first push did not map reads its offset as -1 and writes the value into lane
        /// <c>matches - 1</c> of the segment at offset 0: another variable's earlier match is overwritten silently.
        /// </summary>
        [Fact]
        public void ALaterPushWithAnUnmappedUsedVariableDoesNotCorruptAnotherColumn()
        {
            var b = new Bindings();
            var map = new DenseIdMap<Variable, Value>();
            map.Insert(V(0), new Value(1));
            map.Insert(V(2), new Value(5));
            b.Push(map, [V(0)]);

            map.Insert(V(0), new Value(2));
            map.Insert(V(1), new Value(9));
            var threw = false;
            try
            {
                b.Push(map, [V(0), V(1)]);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            // Either the push is rejected, or variable 0 keeps the values it was given.
            if (threw == false)
                Assert.Equal(new uint[] { 1, 2 }, b[V(0)].ToArray());
        }

    }

}
