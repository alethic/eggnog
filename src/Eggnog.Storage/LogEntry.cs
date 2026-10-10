namespace Eggnog.Storage
{

    /// <summary>
    /// An entry in the log a context keeps while it has children: a row it added or changed, or a class it
    /// merged away. A child reads it to learn what changed underneath it.
    /// </summary>
    internal readonly record struct LogEntry(bool IsUnion, int Table, int Slot, ulong Loser);

}
