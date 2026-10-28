using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Eggnog.Tests.Support;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Applies the operations of a random container program (<see cref="ContainerProgramOp"/>) to a scope, through the
    /// library's public entry points.
    /// </summary>
    static class ContainerProgramOps
    {

        /// <summary>
        /// Applies an operation step in <paramref name="scope"/> and describes its outcome without ids: <c>ok</c> for a
        /// write, the answer of a read, or <c>E:</c> with the exception's type and message, ids masked, for a refusal.
        /// </summary>
        /// <param name="theory">The graph.</param>
        /// <param name="scope">The scope, the program's or the replay's root.</param>
        /// <param name="step">The step.</param>
        /// <returns>The outcome.</returns>
        public static string Apply(ContainerProgramTheory theory, Scope scope, ContainerProgramStep step)
        {
            var num = theory.Num;
            var of = Primitives.VecOf<Node>();
            try
            {
                switch (step.Op)
                {
                    case ContainerProgramOp.Union:
                        scope.Union(scope.Make(num.Of(step.A)), scope.Make(num.Of(step.B)));
                        return "ok";
                    case ContainerProgramOp.UnionAdd:
                        scope.Union(scope.Make(theory.Add.Of(num.Of(step.A), num.Of(step.B))), scope.Make(num.Of(step.C)));
                        return "ok";
                    case ContainerProgramOp.VectorOfAdd:
                        scope.Assume(theory.FV.Of(step.K).Is(of.Of(num.Of(step.A), theory.Add.Of(num.Of(step.A), num.Of(step.B)))));
                        return "ok";
                    case ContainerProgramOp.Vector:
                        scope.Assume(theory.FV.Of(step.K).Is(of.Of(num.Of(step.A), num.Of(step.B))));
                        return "ok";
                    case ContainerProgramOp.NestedVector:
                        scope.Assume(theory.FVV.Of(step.K).Is(Primitives.VecOf<Vec<Node>>().Of(of.Of(num.Of(step.A), num.Of(step.B)), of.Of(num.Of(step.C)))));
                        return "ok";
                    case ContainerProgramOp.Set:
                        scope.Assume(theory.FS.Of(step.K).Is(Primitives.SetOf<Node>().Of(num.Of(step.A), num.Of(step.B))));
                        return "ok";
                    case ContainerProgramOp.Map:
                        scope.Assume(theory.FM.Of(step.K).Is(Primitives.MapInsert<Node, Node>().Of(Primitives.MapEmpty<Node, Node>().Of(), num.Of(step.A), num.Of(step.B))));
                        return "ok";
                    case ContainerProgramOp.MultiSet:
                        scope.Assume(theory.FMS.Of(step.K).Is(Primitives.MultiSetOf<Node>().Of(num.Of(step.A), num.Of(step.B), num.Of(step.B))));
                        return "ok";
                    case ContainerProgramOp.Pair:
                        scope.Assume(theory.FP.Of(step.K).Is(Primitives.PairOf<Node, Node>().Of(num.Of(step.A), num.Of(step.B))));
                        return "ok";
                    case ContainerProgramOp.Function:
                        scope.Assume(theory.FF.Of(step.K).Is(Primitives.UnstableFn<Node, Node>(theory.Add).Of(num.Of(step.A))));
                        return "ok";
                    case ContainerProgramOp.MakeL:
                        scope.Make(theory.L.Of(of.Of(num.Of(step.A), num.Of(step.B))));
                        return "ok";
                    case ContainerProgramOp.Max:
                        scope.Assume(theory.H.Of(of.Of(num.Of(step.A), num.Of(step.B))).Is(step.C));
                        return "ok";
                    case ContainerProgramOp.Go:
                        scope.Assume(theory.Go.Of(step.K1));
                        return "ok";
                    case ContainerProgramOp.VectorUnion:
                        scope.Assume(theory.VU.Of(step.K1, step.K2));
                        return "ok";
                    case ContainerProgramOp.PairUnion:
                        scope.Assume(theory.PU.Of(step.K1));
                        return "ok";
                    case ContainerProgramOp.Run:
                        scope.Run(Schedule.Run(theory.Rules));
                        return "ok";
                    case ContainerProgramOp.Saturate:
                        scope.Run(Schedule.Saturate(Schedule.Run(theory.Rules)));
                        return "ok";
                    case ContainerProgramOp.Batch:
                        scope.Write(batch =>
                        {
                            batch.Make(theory.L.Of(of.Of(num.Of(step.A))));
                            batch.Assume(theory.FV.Of(step.K).Is(of.Of(num.Of(step.B), num.Of(step.C))));
                            if (step.C % 2 == 0)
                                batch.Union(batch.Make(num.Of(step.A)), batch.Make(num.Of(step.C)));

                            batch.Assume(theory.H.Of(of.Of(num.Of(step.C), num.Of(step.A))).Is(step.B));
                        });
                        return "ok";
                    case ContainerProgramOp.Extract:
                    {
                        var cost = scope.Extract(scope.Get(num.Of(step.A))).Cost;
                        var l = scope.TryGet(theory.L.Of(of.Of(num.Of(step.A), num.Of(step.B))), out var node) ? scope.Extract(node).Cost.ToString() : "-";
                        return $"x:{cost}/{l}";
                    }
                    case ContainerProgramOp.HostVectorUnion:
                        scope.Assume(Fact.Exists(Primitives.VecUnion<Node>().Of(theory.FV.Of(step.K1), theory.FV.Of(step.K2))));
                        return "ok";
                    case ContainerProgramOp.CheckVectors:
                        return "q:" + scope.Check(theory.FV.Of(step.K1).Is(theory.FV.Of(step.K2)));
                    case ContainerProgramOp.CheckConstantVector:
                        return "q:" + scope.Check(theory.FV.Of(step.K1).Is(of.Of(num.Of(step.A), num.Of(step.B))));
                    case ContainerProgramOp.GetL:
                        return "g:" + scope.TryGet(theory.L.Of(of.Of(num.Of(step.A), num.Of(step.B))), out _);
                    case ContainerProgramOp.Query:
                    {
                        var name = ContainerProgramView.Names(theory, scope);
                        var matches = new List<string>();
                        scope.ForEachMatch(theory.Heads, match =>
                        {
                            matches.Add((name(match.Get(theory.Head)) ?? "?") + "/" + (name(match.Get(theory.First)) ?? "?"));
                            return true;
                        });

                        matches.Sort(StringComparer.Ordinal);
                        return "n:" + string.Join(";", matches);
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(step));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or RuleEvaluationException)
            {
                return "E:" + exception.GetType().Name + ":" + Regex.Replace(exception.Message, "#[0-9]+", "#");
            }
        }

    }

}
