using System;
using System.IO;
using Plinth.Revit.Core.Execution;
using Plinth.Revit.Core.Security;
using Plinth.Revit.Core.Transport;
using Xunit;

namespace Plinth.Revit.Core.Tests
{
    public class SmallPartsTests
    {
        [Fact]
        public void TokenVerifiesOnlyItself()
        {
            var token = SessionToken.Generate();
            Assert.True(token.Verify(token.Value));
            Assert.False(token.Verify(SessionToken.Generate().Value));
            Assert.False(token.Verify(null));
            Assert.False(token.Verify(""));
        }

        [Theory]
        [InlineData(null, 60)]
        [InlineData(0, 60)]
        [InlineData(-5, 60)]
        [InlineData(10, 10)]
        [InlineData(5000, 900)]
        public void TimeoutsAreClamped(int? seconds, int expected)
        {
            Assert.Equal(TimeSpan.FromSeconds(expected), RequestTimeouts.Effective(seconds));
        }

        [Fact]
        public void RequestStartsOrCancelsExactlyOnce()
        {
            var a = new PendingRequest("1", "x", null, null);
            Assert.True(a.TryStart());
            Assert.False(a.TryCancelBeforeStart());

            var b = new PendingRequest("2", "x", null, " ");
            Assert.True(b.TryCancelBeforeStart());
            Assert.False(b.TryStart());
            Assert.Equal("{}", b.ParamsJson);
            Assert.Null(b.ExpectedDocumentId);
        }

        [Fact]
        public void SanitizerKeepsOnlyFileNames()
        {
            Assert.Equal("Failed to open model.rvt here", ErrorSanitizer.Sanitize(@"Failed to open C:\Users\me\Projects\model.rvt here"));
            Assert.Equal("see share.rvt", ErrorSanitizer.Sanitize(@"see \\server\projects\share.rvt"));
        }

        [Fact]
        public void BoundedReaderSplitsLinesAndStopsAtLimit()
        {
            var reader = new StringReader("abc\r\ndef\nlonglonglong\n");
            Assert.Equal("abc", ConnectionSession.ReadLineBounded(reader, 100, out var o1));
            Assert.Equal("def", ConnectionSession.ReadLineBounded(reader, 100, out _));
            Assert.Null(ConnectionSession.ReadLineBounded(reader, 4, out var o3));
            Assert.False(o1);
            Assert.True(o3);
        }
    }
}
