using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class RuleSetReportTests
    {

        [Fact]
        public void SumsTheRulesOfADescription()
        {
            var reports = new Dictionary<string, List<RuleReport>>
            {
                ["a"] = [new RuleReport(null, TimeSpan.FromMilliseconds(2), 3), new RuleReport("plan", TimeSpan.FromMilliseconds(5), 4)],
                ["b"] = [new RuleReport(null, TimeSpan.Zero, 0)],
            };

            var report = new RuleSetReport(true, reports, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            Assert.True(report.Changed);
            Assert.Equal(7, report.NumMatches("a"));
            Assert.Equal(0, report.NumMatches("b"));
            Assert.Equal(0, report.NumMatches("c"));
            Assert.Equal(TimeSpan.FromMilliseconds(7), report.RuleSearchAndApplyTime("a"));
            Assert.Equal(TimeSpan.Zero, report.RuleSearchAndApplyTime("c"));
            Assert.Equal(2, report.RuleReports["a"].Count);
            Assert.False(RuleSetReport.Empty.Changed);
            Assert.Empty(RuleSetReport.Empty.RuleReports);
        }

    }

}
