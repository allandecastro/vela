using System;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class TestClassAppenderTests
{
    // A CLI built on Windows compiles its templates' raw literals with CRLF: the inserted members must still follow the
    // file's own line endings, LF or CRLF, whatever the members arrive with.
    [Theory]
    [InlineData("\n", "\r\n")]
    [InlineData("\r\n", "\n")]
    [InlineData("\n", "\n")]
    [InlineData("\r\n", "\r\n")]
    public void Inserted_members_follow_the_files_line_endings(string fileNewline, string memberNewline)
    {
        string source = string.Join(fileNewline, "namespace N", "{", "    public sealed class T", "    {", "    }", "}", "");
        Assert.True(TestClassAppender.TryFindClass(source, "T", out TestClassAppender.ClassSpan span, out string error), error);

        string members = string.Join(memberNewline, "        [Fact]", "        public void Fires_On_Create_PostOperation() { }", "");
        string updated = TestClassAppender.Insert(source, span, members, []);

        Assert.Contains("public void Fires_On_Create_PostOperation()", updated);
        int lineFeeds = updated.Split('\n').Length - 1;
        int crlfs = updated.Split("\r\n").Length - 1;
        Assert.Equal(fileNewline == "\r\n" ? lineFeeds : 0, crlfs);
        Assert.Equal(crlfs, updated.Split('\r').Length - 1);
    }
}
