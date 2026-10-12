using System;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    public sealed partial class Scope
    {

        /// <summary>
        /// Gets whether <see cref="EGraph.Contradiction"/> holds here.
        /// </summary>
        public bool IsContradictory
        {
            get
            {
                EnterRead();
                try
                {
                    return Engine.IsContradictory;
                }
                finally
                {
                    Guard.Exit();
                }
            }
        }

        /// <summary>
        /// Gets whether every ground fact holds (§6.6.3). Never creates terms and never interns: a constant that was never
        /// interned makes its fact false.
        /// </summary>
        /// <param name="facts">The facts: relation rows, equalities of classes or values, existences.</param>
        /// <returns>Whether every fact holds.</returns>
        /// <exception cref="ArgumentException">A fact is a filter, or a term in it is not ground.</exception>
        public bool Check(params Fact[] facts)
        {
            ArgumentNullException.ThrowIfNull(facts);
            EnterRead();
            try
            {
                foreach (var fact in facts)
                    if (TermReader.Check(this, fact ?? throw new ArgumentNullException(nameof(facts), "A fact is null.")) == false)
                        return false;

                return true;
            }
            finally
            {
                Guard.Exit();
            }
        }

        /// <summary>
        /// Gets the value of a ground term: a function's value, or the class of a constructor term.
        /// </summary>
        /// <typeparam name="T">The term's type.</typeparam>
        /// <param name="term">The term.</param>
        /// <returns>The value; a class is canonical here and usable in this scope.</returns>
        /// <exception cref="InvalidOperationException">The term has no value here.</exception>
        public T Get<T>(Term<T> term)
        {
            if (TryGet(term, out var value))
                return value;

            throw new InvalidOperationException($"{term} has no value in scope '{Path}'.");
        }

        /// <summary>
        /// Gets the value of a ground term, if it has one.
        /// </summary>
        /// <typeparam name="T">The term's type.</typeparam>
        /// <param name="term">The term.</param>
        /// <param name="value">The value, when the result is <c>true</c>.</param>
        /// <returns>Whether the term has a value here.</returns>
        public bool TryGet<T>(Term<T> term, [MaybeNullWhen(false)] out T value)
        {
            ArgumentNullException.ThrowIfNull(term);
            EnterRead();
            try
            {
                if (TermReader.TryRead(this, term, out var cell))
                {
                    value = Graph.Values.TypeOf<T>().Decode(cell, this);
                    return true;
                }

                value = default;
                return false;
            }
            finally
            {
                Guard.Exit();
            }
        }

        /// <summary>
        /// Gets the canonical member of a class here.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="value">The class.</param>
        /// <returns>The canonical member, usable in this scope.</returns>
        /// <exception cref="ArgumentException">The class value is not usable in this scope (D39).</exception>
        public TSort Find<TSort>(TSort value) where TSort : struct, ISort<TSort>
        {
            EnterRead();
            try
            {
                var cell = ClassChecks.Require(this, value.Id, nameof(value));
                return TSort.FromId(new ClassId(Engine.Find(cell), Origin));
            }
            finally
            {
                Guard.Exit();
            }
        }

        /// <summary>
        /// Gets whether two classes are equal here.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="a">One class.</param>
        /// <param name="b">The other class.</param>
        /// <returns>Whether they are one class.</returns>
        /// <exception cref="ArgumentException">A class value is not usable in this scope (D39).</exception>
        public bool AreEqual<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>
        {
            EnterRead();
            try
            {
                var x = ClassChecks.Require(this, a.Id, nameof(a));
                var y = ClassChecks.Require(this, b.Id, nameof(b));
                return Engine.Find(x) == Engine.Find(y);
            }
            finally
            {
                Guard.Exit();
            }
        }

        /// <summary>
        /// Gets whether a class equals the class of a ground term here; <c>false</c> if the term does not exist here.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="a">The class.</param>
        /// <param name="b">The term.</param>
        /// <returns>Whether the term exists and is in the class.</returns>
        /// <exception cref="ArgumentException">A class value is not usable in this scope (D39).</exception>
        public bool AreEqual<TSort>(TSort a, Term<TSort> b) where TSort : struct, ISort<TSort>
        {
            ArgumentNullException.ThrowIfNull(b);
            EnterRead();
            try
            {
                var x = ClassChecks.Require(this, a.Id, nameof(a));
                return TermReader.TryRead(this, b, out var y) && Engine.Find(x) == y;
            }
            finally
            {
                Guard.Exit();
            }
        }

    }

}
