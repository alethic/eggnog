using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One step of a <see cref="ColorProgram"/>. Contexts and ids are named by slots, assigned when the command that
    /// creates them runs, so a shrunk program keeps its meaning: a command whose slot was never filled (its creator was
    /// dropped) or is not usable when it runs (a frozen or disposed context) is skipped.
    /// </summary>
    /// <param name="Kind">What the command does.</param>
    /// <param name="Context">The context slot it acts on (the parent's, for
    /// <see cref="ColorCommandKind.Child"/>).</param>
    /// <param name="Table">The table, for terms, rows, deletes and clears; otherwise -1.</param>
    /// <param name="Cells">The id slots and values of a term, row or union; empty otherwise.</param>
    /// <param name="Arg">The rule mask of a run or saturation, or the row selector of a delete.</param>
    /// <param name="Result">The slot a new id, term or child fills, or a saturation's mode; otherwise -1.</param>
    readonly record struct ColorCommand(ColorCommandKind Kind, int Context, int Table, int[] Cells, int Arg, int Result)
    {

        /// <summary>
        /// Prints the command, with ids as <c>#slot</c> and contexts as <c>cN</c>.
        /// </summary>
        public string Describe()
        {
            var kind = Kind;
            var index = Table;
            var table = index >= 0 ? ColorSchema.Names[index] : "";
            string Cell(int value, int column) => index >= 0 && ColorSchema.IdColumns[index][column] ? "#" + value : value.ToString();
            var cells = string.Join(", ", Cells.Select((value, column) => kind == ColorCommandKind.Union ? "#" + value : kind == ColorCommandKind.Term && index == ColorSchema.Num ? value.ToString() : Cell(value, column)));
            var body = Kind switch
            {
                ColorCommandKind.NewId => $"#{Result} = new id",
                ColorCommandKind.Term => $"#{Result} = term {table}({cells})",
                ColorCommandKind.Row => $"row {table}({cells})",
                ColorCommandKind.Union => $"union({cells})",
                ColorCommandKind.Delete => $"delete {table} row {Arg}",
                ColorCommandKind.Clear => $"clear {table}",
                ColorCommandKind.Run => $"run mask {Arg}",
                ColorCommandKind.Saturate => $"saturate mask {Arg}{(Result == 0 ? "" : " (loop)")}",
                ColorCommandKind.Child => $"c{Result} = child",
                _ => Kind.ToString().ToLowerInvariant(),
            };
            return $"c{Context}: {body}";
        }

    }

}
