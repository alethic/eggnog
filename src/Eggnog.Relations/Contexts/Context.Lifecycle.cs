using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Creates a child: a context that starts as a copy of this one and stores only what is asserted in it
        /// (colored-contexts spec §8.2).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A context that is not frozen is entered (its domain), and the child shares that domain; a frozen context is
        /// not entered, and the child gets a domain of its own, so children of a frozen context may run on different
        /// threads (§8.3). The child is added to this context's children under their lock, and the gate of every
        /// ancestor, this one included, counts it until it is disposed. The first child seals the store's counters
        /// (X16).
        /// </para>
        /// <para>
        /// Interim (M9.1): §8.2 first rebuilds a parent that is not frozen and settles the root, so the child's cursors
        /// start on a clean, canonical parent. Neither operation exists before M9.2/M9.4, and a child of this milestone
        /// reads nothing of its parent, so the child is created as the parent stands.
        /// </para>
        /// </remarks>
        /// <param name="options">The child's options; <see cref="ContextOptions.Default"/> if <c>null</c>.</param>
        /// <returns>The child.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted or contradicted; it is at depth
        /// <see cref="MaxDepth"/>; the call comes from inside another operation of the same thread domain; or the
        /// domain is held by another thread.</exception>
        public Context CreateChild(ContextOptions? options = null)
        {
            ThrowIfDisposed();
            ThrowIfFaulted();
            ThrowIfContradicted();
            var current = Domain;
            if (current.Depth > 0)
                ThrowReentrant(nameof(CreateChild));

            if (Depth + 1 > MaxDepth)
                throw new InvalidOperationException($"A context can be at most {MaxDepth} levels below the root.");

            options ??= ContextOptions.Default;
            if (IsFrozen)
                return AddChild(options);

            using (current.Enter())
                return AddChild(options);
        }

        /// <summary>
        /// Disposes the child (§8.2): it leaves its parent's children, every ancestor's gate stops counting it, and its
        /// structures are released. A second call does nothing; every later call to another member throws
        /// <see cref="ObjectDisposedException"/>. The ids it minted leak, which is harmless.
        /// </summary>
        /// <exception cref="InvalidOperationException">The context is the root; it has live children; the call comes
        /// from inside another operation of the same thread domain; or the domain is held by another thread.</exception>
        public void Dispose()
        {
            if (disposed)
                return;

            if (IsRoot)
                throw new InvalidOperationException("The root context cannot be disposed.");

            var current = Domain;
            if (current.Depth > 0)
                ThrowReentrant(nameof(Dispose));

            using (current.Enter())
            {
                lock (children)
                {
                    if (disposed)
                        return;

                    if (children.Count > 0)
                        throw new InvalidOperationException($"The context has {children.Count} live children; dispose them first.");

                    disposed = true;
                }

                lock (parent!.children)
                    parent.children.Remove(this);

                for (var i = 0; i < Depth; i++)
                    ancestors[i].gate.Exit();

                // M9.2-M9.4 release the overlay, the journal and the layers here, and clear the store's container dirty
                // log when the root's gate reaches zero (§8.2).
                Volatile.Write(ref host, null);
            }
        }

        /// <summary>
        /// Creates the child and links it in: under the child list's lock (which a concurrent dispose of this context
        /// also takes), then the ancestors' gates.
        /// </summary>
        Context AddChild(ContextOptions options)
        {
            // The first child seals the counters; under the definition lock, so a counter being added completes first.
            if (store.HasHadChildren == false)
                lock (store.DefinitionLock)
                    store.NoteChildCreated();

            Context child;
            lock (children)
            {
                ThrowIfDisposed();
                child = new Context(this, options);
                children.Add(child);
            }

            for (var i = 0; i <= Depth; i++)
                ancestors[i].gate.Enter();

            return child;
        }

        /// <summary>
        /// Gets the number of live children. A snapshot.
        /// </summary>
        internal int ChildCount
        {
            get
            {
                lock (children)
                    return children.Count;
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowReentrant(string operation)
        {
            throw new InvalidOperationException($"{operation} cannot be called from inside another operation of the same context's thread domain (a write, a rule action or external, a visitor or a sink).");
        }

    }

}
