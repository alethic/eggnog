namespace Eggnog.Relations
{

    /// <summary>
    /// Receives the rows of a function from <see cref="EGraph.ForEach{TVisitor}"/>: egglog-bridge's
    /// <c>FnMut(FunctionRow) -&gt; bool</c> callback of <c>for_each_while</c> (egglog-bridge/src/lib.rs:436-473).
    /// </summary>
    /// <remarks>
    /// Implemented by structs, so the scan is specialized for the visitor and the per-row call is direct, as egglog's
    /// closure is monomorphized.
    /// </remarks>
    internal interface IFunctionRowVisitor
    {

        /// <summary>
        /// Visits one row.
        /// </summary>
        /// <param name="row">The row; valid only during the call.</param>
        /// <returns><c>false</c> to stop the iteration.</returns>
        bool Visit(FunctionRow row);

    }

}
