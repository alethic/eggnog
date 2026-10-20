namespace Eggnog
{

    /// <summary>
    /// A class of a <see cref="SerializedGraph"/>, with egraph-serialize's <c>ClassData</c>.
    /// </summary>
    public sealed class SerializedClass
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id">The class's id.</param>
        /// <param name="type">The name of its sort or value type.</param>
        internal SerializedClass(string id, string type)
        {
            Id = id;
            Type = type;
        }

        /// <summary>
        /// Gets the class's id, as egglog makes it: <c>{type}-{cell}</c>, the name of the sort or value type and the
        /// canonical class's or the value's cell in the scope. A relation's row is a class of its own, of the type
        /// <c>@{relation}Sort</c>, as egglog makes a relation a constructor over a sort of that name. A set's element that
        /// was never interned on its own has no cell, and is a class <c>{type}-~{n}</c>, numbered per type; once another
        /// write interns the element, a later serialization names it by its cell.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the name of the class's sort or value type.
        /// </summary>
        public string Type { get; }

        /// <summary>
        /// Gets the id.
        /// </summary>
        /// <returns>The id.</returns>
        public override string ToString() => Id;

    }

}
