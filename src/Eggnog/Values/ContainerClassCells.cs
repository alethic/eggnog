using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// Walks the classes a cell names: a sort's cell is a class, and a container of classes
    /// (<see cref="ColumnType.HoldsClasses"/>) names the classes its elements name.
    /// </summary>
    internal static class ContainerClassCells
    {

        /// <summary>
        /// Appends the class cells a cell of <paramref name="type"/> names, depth first in the containers' order.
        /// </summary>
        /// <param name="type">The cell's column type.</param>
        /// <param name="cell">The cell.</param>
        /// <param name="classes">Receives the class cells.</param>
        public static void AppendClasses(ColumnType type, uint cell, List<uint> classes)
        {
            if (type.Kind == ColumnKind.Sort)
            {
                classes.Add(cell);
                return;
            }

            if (type.HoldsClasses == false)
                return;

            var elements = new List<(ColumnType Type, uint Cell)>();
            ((IContainerColumnType)type).AppendElements(cell, elements);
            foreach (var (elementType, elementCell) in elements)
                AppendClasses(elementType, elementCell, classes);
        }

        /// <summary>
        /// Adds the sorts <paramref name="type"/> is or holds.
        /// </summary>
        /// <param name="type">The column type.</param>
        /// <param name="sorts">Receives the sorts.</param>
        public static void AddSorts(ColumnType type, HashSet<ColumnType> sorts)
        {
            if (type.Kind == ColumnKind.Sort)
            {
                sorts.Add(type);
                return;
            }

            if (type.HoldsClasses)
                foreach (var element in ((IContainerColumnType)type).ElementTypes)
                    AddSorts(element, sorts);
        }

        /// <summary>
        /// Gets the content of a container of classes with each class canonical through <paramref name="find"/>, as a
        /// key: two containers of one type have one key exactly when the rebuild would make them one container.
        /// </summary>
        /// <param name="type">The container's column type, which holds classes.</param>
        /// <param name="cell">The container's cell.</param>
        /// <param name="find">The canonical cell of a class.</param>
        /// <returns>The key.</returns>
        public static string CanonicalContent(ColumnType type, uint cell, Func<uint, uint> find)
        {
            var builder = new StringBuilder(type.Name);
            Append(type, cell, find, builder);
            return builder.ToString();
        }

        /// <summary>
        /// Appends a cell's canonical form: a class's canonical cell, a container of classes' elements in brackets, any
        /// other cell as it is.
        /// </summary>
        static void Append(ColumnType type, uint cell, Func<uint, uint> find, StringBuilder builder)
        {
            if (type.Kind == ColumnKind.Sort)
            {
                builder.Append('#').Append(find(cell).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (type.HoldsClasses == false)
            {
                builder.Append(cell.ToString(CultureInfo.InvariantCulture));
                return;
            }

            var elements = new List<(ColumnType Type, uint Cell)>();
            ((IContainerColumnType)type).AppendElements(cell, elements);
            builder.Append('[');
            for (var i = 0; i < elements.Count; i++)
            {
                if (i > 0)
                    builder.Append(' ');

                Append(elements[i].Type, elements[i].Cell, find, builder);
            }

            builder.Append(']');
        }

    }

}
