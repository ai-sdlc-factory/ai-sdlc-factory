namespace DevFoundry.Controller.Tests;

public sealed class PromptTextTests
{
    [Fact]
    public void Markers_are_broken_up_so_text_cannot_close_its_own_block()
    {
        Assert.Equal("a < < < b > > > c", PromptText.Clip("a <<< b >>> c", 100));
    }

    [Fact]
    public void Removing_a_nul_character_cannot_rebuild_a_marker()
    {
        var clean = PromptText.Clip("x<\0<<y>\0>>z", 100);

        Assert.DoesNotContain("<<<", clean);
        Assert.DoesNotContain(">>>", clean);
        Assert.DoesNotContain('\0', clean);
    }

    [Fact]
    public void Long_text_is_cut_and_says_so_and_short_text_is_only_trimmed()
    {
        Assert.Equal("abc [cut]", PromptText.Clip("abcdef", 3));
        Assert.Equal("abc", PromptText.Clip("  abc \n", 10));
        Assert.Equal("", PromptText.Clip(null, 10));
    }

    [Fact]
    public void A_line_value_cannot_start_a_new_line_of_the_prompt()
    {
        Assert.Equal("one  two three", PromptText.ClipLine("one\r\ntwo\nthree", 100)); // each line-break character becomes a space
        Assert.DoesNotContain('\n', PromptText.ClipLine("a\n\n\nb", 100));
        Assert.Equal("ab [cut]", PromptText.ClipLine("ab\ncd", 2));
    }

    [Fact]
    public void A_marker_split_by_a_line_break_cannot_come_back_together()
    {
        Assert.DoesNotContain("<<<", PromptText.ClipLine("<<\n<", 100));
    }
}
