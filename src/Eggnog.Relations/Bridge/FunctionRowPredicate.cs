namespace Eggnog.Relations
{

    /// <summary>
    /// Receives rows of a function until it returns <c>false</c>: the callback of egglog-bridge's <c>for_each_while</c>
    /// (egglog-bridge/src/lib.rs:436-473) as a delegate, for callers that do not need a specialized visitor
    /// (<see cref="IFunctionRowVisitor"/>).
    /// </summary>
    /// <param name="row">The row; valid only during the call.</param>
    /// <returns><c>false</c> to stop the iteration.</returns>
    public delegate bool FunctionRowPredicate(FunctionRow row);

}
