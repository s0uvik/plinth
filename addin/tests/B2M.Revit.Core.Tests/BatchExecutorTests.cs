using System.Collections.Generic;
using System.Linq;
using B2M.Revit.Core.Execution;
using Newtonsoft.Json.Linq;
using Xunit;

namespace B2M.Revit.Core.Tests
{
    public class BatchExecutorTests
    {
        private const string LevelSchema = @"{""properties"":{""elevation"":{""type"":""number""}},""required"":[""elevation""]}";

        private static string SchemaOf(string name) => name == "create_level" ? LevelSchema : null;

        private static JArray Levels(int count) =>
            new JArray(Enumerable.Range(0, count).Select(i =>
                new JObject { ["command"] = "create_level", ["params"] = new JObject { ["elevation"] = i * 3000 } }));

        [Fact]
        public void PrecheckPassesValidBatch()
        {
            Assert.Null(BatchExecutor.Precheck(Levels(20), SchemaOf));
        }

        [Fact]
        public void PrecheckRejectsMoreThanTwentyCommands()
        {
            var outcome = BatchExecutor.Precheck(Levels(21), SchemaOf);
            Assert.Contains("BATCH_TOO_LARGE", outcome.FirstError);
        }

        [Fact]
        public void PrecheckRejectsUnknownNestedAndBadParamsBeforeAnythingRuns()
        {
            var batch = Levels(3);
            batch.Add(new JObject { ["command"] = "create_level", ["params"] = new JObject { ["elevaton"] = 1 } });
            var outcome = BatchExecutor.Precheck(batch, SchemaOf);
            Assert.Equal(3, outcome.FirstFailedIndex);
            Assert.Contains("Sub-command 3 (create_level)", outcome.FirstError);

            Assert.Contains("UNKNOWN_COMMAND", BatchExecutor.Precheck(new JArray(new JObject { ["command"] = "nope" }), SchemaOf).FirstError);
            Assert.Contains("NOT_ALLOWED_IN_BATCH", BatchExecutor.Precheck(new JArray(new JObject { ["command"] = "batch_execute" }), SchemaOf).FirstError);
            Assert.Contains("NOT_ALLOWED_IN_BATCH", BatchExecutor.Precheck(new JArray(new JObject { ["command"] = "send_code_to_revit" }), SchemaOf).FirstError);
            Assert.Contains("NOT_ALLOWED_IN_BATCH", BatchExecutor.Precheck(new JArray(new JObject { ["command"] = "create_stair" }), SchemaOf).FirstError);
            Assert.Contains("NOT_ALLOWED_IN_BATCH", BatchExecutor.Precheck(new JArray(new JObject { ["command"] = "ensure_family_type" }), SchemaOf).FirstError);
            Assert.Contains("NOT_ALLOWED_IN_BATCH", BatchExecutor.Precheck(new JArray(new JObject { ["command"] = "create_box_family" }), SchemaOf).FirstError);
        }

        [Fact]
        public void RunStopsAtFirstFailureAndRollsBack()
        {
            var calls = new List<string>();
            var outcome = BatchExecutor.Run(Levels(4), false, (name, p) =>
            {
                calls.Add(p);
                return calls.Count == 2 ? BatchExecutor.InvokeResult.Fail("boom") : BatchExecutor.InvokeResult.Ok(new JObject { ["elementId"] = calls.Count });
            });
            Assert.Equal(2, calls.Count);
            Assert.Equal(1, outcome.FirstFailedIndex);
            Assert.Equal("boom", outcome.FirstError);
            Assert.True(outcome.ShouldRollBack(false));
            Assert.Equal(2, outcome.Results.Count);
        }

        [Fact]
        public void RunWithContinueOnErrorRunsEverythingAndKeepsResults()
        {
            var n = 0;
            var outcome = BatchExecutor.Run(Levels(3), true, (name, p) =>
                ++n == 2 ? BatchExecutor.InvokeResult.Fail("boom") : BatchExecutor.InvokeResult.Ok(null));
            Assert.Equal(3, n);
            Assert.False(outcome.ShouldRollBack(true));
            Assert.True(outcome.AnyFailed);
        }

        [Fact]
        public void PartialFailureCountsAsFailure()
        {
            var outcome = BatchExecutor.Run(Levels(1), false, (name, p) => BatchExecutor.InvokeResult.Ok(new JObject { ["failedCount"] = 2 }));
            Assert.Contains("2 partial failure", outcome.FirstError);
        }

        [Fact]
        public void ExceptionInSubCommandIsAFailure()
        {
            var outcome = BatchExecutor.Run(Levels(1), false, (name, p) => throw new System.InvalidOperationException("bad"));
            Assert.Equal("bad", outcome.FirstError);
        }
    }
}
