namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a <see cref="ColorRunner"/> runs besides the engine.
    /// </summary>
    /// <param name="Reference">Whether <see cref="ReferenceRuleContext"/> runs in lockstep and every checkpoint
    /// compares with it.</param>
    /// <param name="Unseeded">Whether a second engine runs in lockstep with every child created with
    /// <see cref="ContextOptions.InheritWatermarks"/> off, compared at every checkpoint, and match totals are
    /// kept.</param>
    /// <param name="Copy">Whether, after the program, each live context saturates and is compared with a fresh root
    /// that replays its chain's host writes and saturates the same rules, conjunctive queries included (monotone
    /// programs only).</param>
    sealed record ColorRunOptions(bool Reference = true, bool Unseeded = false, bool Copy = false);

}
