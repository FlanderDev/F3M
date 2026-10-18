using F3M.Shared.Helpers;

namespace F3M.Shared.Tests;

public class TagGraphTests
{
    // 1 Clothing → 2 Underwear → 3 Lingerie, 1 Clothing → 4 Shoes, 5 Beach → 3 Lingerie
    private static TagGraph Sample() => new([1, 2, 3, 4, 5], [(1, 2), (2, 3), (1, 4), (5, 3)]);

    private static string Name(int id) => $"T{id}";

    [Fact]
    public void SelfAndDescendants_FollowsEveryLevel()
    {
        Assert.Equal([1, 2, 3, 4], Sample().SelfAndDescendants(1).Order());
        Assert.Equal([3], Sample().SelfAndDescendants(3).Order());
    }

    [Fact]
    public void SelfAndAncestors_FollowsEveryParent()
    {
        Assert.Equal([1, 2, 3, 5], Sample().SelfAndAncestors(3).Order());
    }

    [Fact]
    public void Problem_ValidTree_IsNull()
    {
        Assert.Null(Sample().Problem(Name));
    }

    [Fact]
    public void Problem_SelfInclusion_IsALoop()
    {
        var graph = Sample().WithInclusions(4, [4], [1]);
        Assert.Contains("loop", graph.Problem(Name));
    }

    [Fact]
    public void Problem_LoopThroughOtherTags_NamesTheLoop()
    {
        // Lingerie including Clothing: Clothing → Underwear → Lingerie → Clothing.
        var graph = Sample().WithInclusions(3, [1], [2, 5]);
        var problem = graph.Problem(Name);
        Assert.NotNull(problem);
        Assert.Contains("T1 → T2 → T3 → T1", problem);
    }

    [Fact]
    public void Problem_ChainLongerThanTheLimit_IsRefused()
    {
        var ids = Enumerable.Range(1, TagRules.MaxDepth + 1).ToList();
        var chain = ids.Zip(ids.Skip(1)).ToList();

        Assert.Null(new TagGraph(ids.Take(TagRules.MaxDepth), chain.Take(TagRules.MaxDepth - 1)).Problem(Name));
        Assert.Contains("chain", new TagGraph(ids, chain).Problem(Name));
    }

    [Fact]
    public void WithInclusions_ReplacesOnlyThatTagsInclusions()
    {
        // Underwear now includes Shoes instead of Lingerie, and stays under Clothing.
        var graph = Sample().WithInclusions(2, [4], [1]);
        Assert.Equal([4], graph.ChildrenOf(2));
        Assert.Equal([1], graph.ParentsOf(2));
        Assert.Equal([5], graph.ParentsOf(3));
    }

    [Fact]
    public void WithInclusions_NewTag_IsAdded()
    {
        var graph = Sample().WithInclusions(0, [3], [5]);
        Assert.True(graph.Contains(0));
        Assert.Null(graph.Problem(Name));
        Assert.Equal([0, 1, 2, 3, 5], graph.SelfAndAncestors(3).Order());
    }

    [Fact]
    public void ImpliedThrough_FindsTheIndirectPath()
    {
        var graph = Sample().WithInclusions(1, [2, 3, 4], []);
        Assert.Equal(2, graph.ImpliedThrough(1, 3));
        Assert.Null(graph.ImpliedThrough(1, 4));
    }

    [Fact]
    public void UnknownTagsInInclusions_AreIgnored()
    {
        var graph = new TagGraph([1, 2], [(1, 2), (1, 9), (9, 2)]);
        Assert.Equal([2], graph.ChildrenOf(1));
        Assert.Null(graph.Problem(Name));
    }

    [Theory]
    [InlineData("  Cosplay   outfits ", "Cosplay outfits")]
    [InlineData("Textures", "Textures")]
    public void Clean_TrimsAndCollapsesSpaces(string input, string expected)
    {
        Assert.Equal(expected, TagRules.Clean(input));
    }

    [Fact]
    public void Normalize_IgnoresCaseAndSpacing()
    {
        Assert.Equal(TagRules.Normalize("cosplay  Outfits"), TagRules.Normalize(" Cosplay outfits"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Textures", true)]
    [InlineData("Shoes & Socks", true)]
    [InlineData("a,b", false)]
    [InlineData("a/b", false)]
    public void NameProblem_AllowsOnlyUsableNames(string name, bool usable)
    {
        Assert.Equal(usable, TagRules.NameProblem(name) is null);
    }

    [Fact]
    public void NameProblem_TooLong_IsRefused()
    {
        Assert.NotNull(TagRules.NameProblem(new string('a', TagRules.MaxNameLength + 1)));
    }
}
