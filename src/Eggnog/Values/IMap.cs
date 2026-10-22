using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A <see cref="Map{TKey, TValue}"/> seen without its type arguments, for rendering a map constant that no column
    /// type describes.
    /// </summary>
    internal interface IMap
    {

        /// <summary>
        /// Gets the entries, in key order.
        /// </summary>
        IEnumerable<(object Key, object Value)> Entries { get; }

    }

}
