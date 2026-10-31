using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A store's count of the changes a child's catch-up test reads in its ancestors: it moves after every change of a
    /// level that has live descendants to its change stamp (<see cref="Database.ChangeStamp"/>) or its container log
    /// (<see cref="ContainerLog"/>: an Added entry buffered, Dirty ids appended). A child that found itself current
    /// with every ancestor at one reading needs no ancestor read while the reading has not moved
    /// (<see cref="ContextTables.NeedsCatchUp"/>), so testing a chain costs O(1) per level, not one read per level and
    /// ancestor.
    /// </summary>
    /// <remarks>
    /// A level without live descendants does not move the clock, so a root or a leaf pays one volatile read of its log's
    /// <see cref="ContainerLog.Active"/> where its change stamp moves. Each change moves the clock after the change, and a
    /// reader reads the clock before the ancestors, so a reading that saw every ancestor current stays exact until the
    /// clock moves. Thread-safe.
    /// </remarks>
    internal sealed class ChangeClock
    {

        long value;

        /// <summary>
        /// Gets the clock's reading. One volatile read.
        /// </summary>
        public long Value => Interlocked.Read(ref value);

        /// <summary>
        /// Moves the clock, after a change of a level with live descendants.
        /// </summary>
        public void Advance() => Interlocked.Increment(ref value);

    }

}
