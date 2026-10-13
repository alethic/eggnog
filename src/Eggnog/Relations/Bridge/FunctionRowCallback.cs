namespace Eggnog.Relations
{

    /// <summary>
    /// Receives each row of a function: the callback of egglog-bridge's <c>for_each</c> (egglog-bridge/src/lib.rs:429-434)
    /// as a delegate, for callers that do not need a specialized visitor (<see cref="IFunctionRowVisitor"/>).
    /// </summary>
    /// <param name="row">The row; valid only during the call.</param>
    internal delegate void FunctionRowCallback(FunctionRow row);

}
