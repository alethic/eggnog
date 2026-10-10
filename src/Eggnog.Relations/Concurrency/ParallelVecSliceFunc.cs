using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A function of a read-only slice, for <see cref="ParallelVecWriter{T}.WithSlice"/>. A span cannot be a type
    /// argument of <see cref="Func{T, TResult}"/> on every target framework, hence this delegate.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="slice">The slice, valid only during the call.</param>
    /// <returns>The result.</returns>
    internal delegate TResult ParallelVecSliceFunc<T, out TResult>(ReadOnlySpan<T> slice);

}
