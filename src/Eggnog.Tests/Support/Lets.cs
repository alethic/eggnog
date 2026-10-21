using Xunit;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// egglog's top-level <c>let</c> and <c>fail</c> for ported tests: egglog desugars <c>(let $x e)</c> into a nullary
    /// function <c>$x</c> set to <c>e</c>, which interns <c>e</c>'s value, as a host write does here and a read does not;
    /// and <c>(fail e)</c> of a call with no result is that write refused.
    /// </summary>
    public sealed class Lets
    {

        readonly EGraph graph;
        int fails;

        /// <summary>
        /// Initializes the lets of a graph, written at its root.
        /// </summary>
        /// <param name="graph">The graph.</param>
        public Lets(EGraph graph)
        {
            this.graph = graph;
        }

        /// <summary>
        /// Gets the root.
        /// </summary>
        public Scope Root => graph.Root;

        /// <summary>
        /// <c>(let $name value)</c>: sets the nullary function <c>name</c> to the value and returns it.
        /// </summary>
        /// <typeparam name="T">The value's type.</typeparam>
        /// <param name="name">The name, without egglog's <c>$</c>, which a table name here may not start with.</param>
        /// <param name="value">The value.</param>
        /// <returns>The value.</returns>
        public T Let<T>(string name, Term<T> value)
        {
            var let = graph.DefineFunction<T>(name);
            graph.Root.Assume(let.Of().Is(value));
            return graph.Root.Get(let.Of());
        }

        /// <summary>
        /// <c>(fail value)</c> of a call with no result: setting a nullary function to it is refused for that reason.
        /// </summary>
        /// <typeparam name="T">The value's type.</typeparam>
        /// <param name="value">The value.</param>
        public void Fail<T>(Term<T> value)
        {
            var let = graph.DefineFunction<T>("fail-" + fails++);
            var e = Assert.Throws<RuleEvaluationException>(() => graph.Root.Assume(let.Of().Is(value)));
            Assert.Contains("has no result", e.Message, System.StringComparison.Ordinal);
        }

    }

}
