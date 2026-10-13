namespace Eggnog.Relations
{

    /// <summary>
    /// Adapts a <see cref="FunctionRowCallback"/> to <see cref="IFunctionRowVisitor"/>, visiting every row: one delegate
    /// call per row.
    /// </summary>
    internal readonly struct FunctionRowCallbackVisitor : IFunctionRowVisitor
    {

        readonly FunctionRowCallback callback;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="callback">The callback.</param>
        public FunctionRowCallbackVisitor(FunctionRowCallback callback)
        {
            this.callback = callback;
        }

        /// <inheritdoc />
        public bool Visit(FunctionRow row)
        {
            callback(row);
            return true;
        }

    }

}
