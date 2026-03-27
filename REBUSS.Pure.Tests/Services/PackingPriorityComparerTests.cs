using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Services.ResponsePacking;

namespace REBUSS.Pure.Tests.Services.ResponsePacking;

public class PackingPriorityComparerTests
{
    private readonly PackingPriorityComparer _comparer = PackingPriorityComparer.Instance;

    // --- Primary sort: FileCategory ascending ---

    [Fact]
    public void Compare_SourceBeforeTest()
    {
        var source = new PackingCandidate("a.cs", 100, FileCategory.Source, 10);
        var test = new PackingCandidate("b.cs", 100, FileCategory.Test, 10);

        Assert.True(_comparer.Compare(source, test) < 0);
    }

    [Fact]
    public void Compare_TestBeforeConfig()
    {
        var test = new PackingCandidate("a.test.cs", 100, FileCategory.Test, 10);
        var config = new PackingCandidate("app.json", 100, FileCategory.Config, 10);

        Assert.True(_comparer.Compare(test, config) < 0);
    }

    [Fact]
    public void Compare_ConfigBeforeDocs()
    {
        var config = new PackingCandidate("app.json", 100, FileCategory.Config, 10);
        var docs = new PackingCandidate("readme.md", 100, FileCategory.Docs, 10);

        Assert.True(_comparer.Compare(config, docs) < 0);
    }

    [Fact]
    public void Compare_DocsBeforeBinary()
    {
        var docs = new PackingCandidate("readme.md", 100, FileCategory.Docs, 10);
        var binary = new PackingCandidate("logo.png", 100, FileCategory.Binary, 10);

        Assert.True(_comparer.Compare(docs, binary) < 0);
    }

    [Fact]
    public void Compare_BinaryBeforeGenerated()
    {
        var binary = new PackingCandidate("logo.png", 100, FileCategory.Binary, 10);
        var generated = new PackingCandidate("file.g.cs", 100, FileCategory.Generated, 10);

        Assert.True(_comparer.Compare(binary, generated) < 0);
    }

    // --- Secondary sort: TotalChanges descending ---

    [Fact]
    public void Compare_MoreChangesFirst_WithinSameCategory()
    {
        var moreChanges = new PackingCandidate("a.cs", 100, FileCategory.Source, 50);
        var fewerChanges = new PackingCandidate("b.cs", 100, FileCategory.Source, 10);

        Assert.True(_comparer.Compare(moreChanges, fewerChanges) < 0);
    }

    // --- Tertiary sort: Path ascending ---

    [Fact]
    public void Compare_PathAscending_WhenCategoryAndChangesEqual()
    {
        var alpha = new PackingCandidate("alpha.cs", 100, FileCategory.Source, 10);
        var beta = new PackingCandidate("beta.cs", 100, FileCategory.Source, 10);

        Assert.True(_comparer.Compare(alpha, beta) < 0);
    }

    // --- Identity ---

    [Fact]
    public void Compare_ReturnsZero_ForSameReference()
    {
        var item = new PackingCandidate("a.cs", 100, FileCategory.Source, 10);

        Assert.Equal(0, _comparer.Compare(item, item));
    }

    // --- Null handling ---

    [Fact]
    public void Compare_NullFirst_ReturnsNegative()
    {
        var item = new PackingCandidate("a.cs", 100, FileCategory.Source, 10);

        Assert.True(_comparer.Compare(null, item) < 0);
    }

    [Fact]
    public void Compare_NullSecond_ReturnsPositive()
    {
        var item = new PackingCandidate("a.cs", 100, FileCategory.Source, 10);

        Assert.True(_comparer.Compare(item, null) > 0);
    }

    [Fact]
    public void Compare_BothNull_ReturnsZero()
    {
        Assert.Equal(0, _comparer.Compare(null, null));
    }
}
