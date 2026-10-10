namespace Eggnog.Relations
{

    /// <summary>
    /// Updates the value of a <see cref="ResettableOnceLock{T}"/> in place, the Rust's <c>FnOnce(&amp;mut T)</c>. The
    /// explicit state lets callers pass a static lambda and avoid allocating a closure per call.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <param name="value">The value to update.</param>
    /// <param name="state">The caller's state.</param>
    internal delegate void ResettableOnceUpdate<T, in TState>(ref T value, TState state);

}
