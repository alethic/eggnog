namespace Eggnog
{

    /// <summary>
    /// The fact that a call holds or runs (<see cref="Fact.Exists"/>): in a body or a check, an application has a row or a primitive's call a result; in a head or a host write, the call runs for its effect alone.
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
