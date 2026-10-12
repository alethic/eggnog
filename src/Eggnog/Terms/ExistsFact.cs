namespace Eggnog
{

    /// <summary>
    /// The fact that a function or constructor application has a row.
    /// </summary>
    internal sealed class ExistsFact : Fact
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="application">The application.</param>
        public ExistsFact(Term application)
        {
            Application = application;
        }

        /// <summary>
        /// Gets the application.
        /// </summary>
        public Term Application { get; }

        /// <inheritdoc />
        internal override FactKind Kind => FactKind.Exists;

    }

}
