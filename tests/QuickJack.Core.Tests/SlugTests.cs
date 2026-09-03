using QuickJack.Core.Storage;

namespace QuickJack.Core.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("Flush DNS", "flush-dns")]
    [InlineData("  Restart   IIS  ", "restart-iis")]
    [InlineData("Docker: prune -a!", "docker-prune-a")]
    [InlineData("Get-Process | Top 10", "get-process-top-10")]
    [InlineData("already-a-slug", "already-a-slug")]
    [InlineData("...", "command")]
    [InlineData("", "command")]
    public void From_produces_expected_slug(string input, string expected) =>
        Assert.Equal(expected, Slug.From(input));

    [Fact]
    public void From_never_leaves_a_trailing_dash()
    {
        Assert.Equal("trailing", Slug.From("trailing!!!"));
        Assert.Equal("leading", Slug.From("!!!leading"));
    }

    [Fact]
    public void From_truncates_without_a_trailing_dash()
    {
        var slug = Slug.From(new string('a', 200) + " tail");
        Assert.Equal(Slug.MaxLength, slug.Length);
        Assert.DoesNotContain('-', slug);
    }

    [Fact]
    public void Unique_suffixes_on_collision()
    {
        var taken = new HashSet<string> { "flush-dns", "flush-dns-2" };
        Assert.Equal("flush-dns-3", Slug.Unique("Flush DNS", taken.Contains));
    }

    [Fact]
    public void Unique_returns_the_base_slug_when_free() =>
        Assert.Equal("flush-dns", Slug.Unique("Flush DNS", _ => false));

    [Theory]
    [InlineData("flush-dns", true)]
    [InlineData("Flush-DNS", false)]  // upper case is not canonical
    [InlineData("flush dns", false)]
    [InlineData("../escape", false)]  // must never be usable as a path traversal
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_accepts_only_canonical_slugs(string? value, bool expected) =>
        Assert.Equal(expected, Slug.IsValid(value));
}
