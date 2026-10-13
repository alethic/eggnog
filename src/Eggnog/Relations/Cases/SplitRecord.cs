using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A split opened in a scope: its key and request, the source that asked for it, the driver that opened it, the
    /// depth it was opened at, and its cases. The scope keeps it until <see cref="SplitDriver.DisposeRecords"/>; while
    /// its cases are not disposed it is live and covers its source's split over its key's values in the scope and every
    /// descendant, for every driver (coverage compares the source by identity, not by its index in a driver's list).
    /// Only the driver that opened it runs and lifts its cases again.
    /// </summary>
    internal sealed class SplitRecord
    {

        /// <summary>
        /// Initializes a record of <paramref name="request"/> from <paramref name="source"/>, whose cases are
        /// <paramref name="cases"/>.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="source">The source that asked for it.</param>
        /// <param name="cases">The cases, a set under the scope.</param>
        /// <param name="depth">The split depth of the step that opened it.</param>
        /// <param name="driver">The driver that opened it.</param>
        public SplitRecord(SplitRequest request, ISplitSource source, CaseSet cases, int depth, SplitDriver driver)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(cases);
            ArgumentNullException.ThrowIfNull(driver);
            Request = request;
            Source = source;
            Cases = cases;
            Depth = depth;
            Driver = driver;
        }

        /// <summary>
        /// Gets the split's key.
        /// </summary>
        public SplitKey Key => Request.Key;

        /// <summary>
        /// Gets the request.
        /// </summary>
        public SplitRequest Request { get; }

        /// <summary>
        /// Gets the source that asked for the split.
        /// </summary>
        public ISplitSource Source { get; }

        /// <summary>
        /// Gets the cases.
        /// </summary>
        public CaseSet Cases { get; }

        /// <summary>
        /// Gets the split depth of the step that opened it.
        /// </summary>
        public int Depth { get; }

        /// <summary>
        /// Gets the driver that opened the split: the only one whose steps run the in-case schedule in its cases and
        /// lift them, since another driver serving the same scope has its own in-case schedule and lift spec.
        /// </summary>
        public SplitDriver Driver { get; }

        /// <summary>
        /// Gets whether the record is a split of <paramref name="source"/> over <paramref name="values"/>: the same source
        /// object (by reference, whatever its index in a driver's list) and the same identifying cells. O(cells).
        /// </summary>
        /// <param name="source">The source.</param>
        /// <param name="values">The identifying cells.</param>
        /// <returns><c>true</c> if the record splits the same thing.</returns>
        public bool Splits(ISplitSource source, ReadOnlySpan<uint> values) => ReferenceEquals(Source, source) && Key.Values.SequenceEqual(values);

        /// <summary>
        /// Gets whether the record still covers its key: its cases are not disposed.
        /// </summary>
        public bool IsLive => Cases.IsDisposed == false;

    }

}
