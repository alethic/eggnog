using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the base value registry of a <see cref="Database"/> (egglog's <c>Database::base_values</c>,
    /// core-relations/src/free_join/mod.rs:306, 341-347) and its access from execution states.
    /// </summary>
    public class DatabaseBaseValuesTests
    {

        [Fact]
        public void TheRegistryIsCreatedOnceAndSharedWithExecutionStates()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var registry = db.BaseValues;
            Assert.Same(registry, db.BaseValues);
            Assert.Same(registry, db.CreateExecutionState().BaseValues);
            Assert.Same(registry, db.CreateExecutionState().Clone().BaseValues);
            Assert.Throws<System.InvalidOperationException>(() => new ExecutionState(new TableCatalog()).BaseValues);
        }

        [Fact]
        public void ACloneKeepsTheTypesAndSharesTheInternTables()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var ty = db.BaseValues.RegisterType<StringCodec, string>();
            var hello = db.BaseValues.Get<StringCodec, string>("hello");

            var copy = db.Clone();
            Assert.NotSame(db.BaseValues, copy.BaseValues);
            Assert.Equal(ty, copy.BaseValues.GetTypeId<StringCodec, string>());
            Assert.Equal("hello", copy.BaseValues.Unwrap<StringCodec, string>(hello));

            // Interned through the copy, visible through the original (egglog's Arc-shared intern tables).
            var world = copy.BaseValues.Get<StringCodec, string>("world");
            Assert.Equal("world", db.BaseValues.Unwrap<StringCodec, string>(world));
        }

    }

}
