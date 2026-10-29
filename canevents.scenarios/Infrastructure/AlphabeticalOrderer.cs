using Xunit.Abstractions;
using Xunit.Sdk;

namespace canevents.scenarios.Infrastructure;

// xUnit gives no method order within a class; Atlas leaves ordering to the consumer (its ADR 0010).
// For classes whose later scenario runs on what an earlier one left behind, e.g. across a RestartWorld.
public class AlphabeticalOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
        => testCases.OrderBy(tc => tc.TestMethod.Method.Name, StringComparer.Ordinal);
}
