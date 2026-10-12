using System;

namespace Eggnog
{

    /// <summary>
    /// A variable of one rule builder; using it in another builder is a <see cref="RuleDefinitionException"/>.
    /// </summary>
    /// <typeparam name="T">The CLR type of its values.</typeparam>
    public sealed class Var<T> : Term<T>
    {

        readonly object owner;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="owner">The builder that declared the variable.</param>
        /// <param name="name">The variable's name.</param>
        internal Var(object owner, string name)
        {
            this.owner = owner;
            Name = name;
        }

        /// <summary>
        /// Gets the variable's name.
        /// </summary>
        public string Name { get; }

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Variable, typeof(T), Name, owner, null, null, null, null, Array.Empty<Term>());

    }

}
