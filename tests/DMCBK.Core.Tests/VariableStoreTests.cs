using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>Tests the D8 <see cref="VariableStore"/> (set/get/remove/expand/sanitize).</summary>
public sealed class VariableStoreTests
{
    [Fact]
    public void Set_Then_Get_RoundTrips()
    {
        var store = new VariableStore();
        Assert.True(store.Set("name", "value"));
        Assert.Equal("value", store.Get("name"));
    }

    [Fact]
    public void Set_IsCaseInsensitive()
    {
        var store = new VariableStore();
        store.Set("Foo", "1");
        Assert.Equal("1", store.Get("foo"));
    }

    [Fact]
    public void Set_SanitizesTrailingNonWordCharacters()
    {
        var store = new VariableStore();
        Assert.True(store.Set("na me", "v"));
        Assert.Equal("v", store.Get("na"));
    }

    [Fact]
    public void Set_RejectsEmptyName()
    {
        var store = new VariableStore();
        Assert.False(store.Set("  ", "v"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Remove_DeletesVariable()
    {
        var store = new VariableStore();
        store.Set("x", "1");
        Assert.True(store.Remove("x"));
        Assert.Null(store.Get("x"));
    }

    [Fact]
    public void Expand_ReplacesKnownVariable()
    {
        var store = new VariableStore();
        store.Set("who", "Steve");
        Assert.Equal("hello Steve!", store.Expand("hello %who%!"));
    }

    [Fact]
    public void Expand_LeavesUnknownVariableLiteral()
    {
        var store = new VariableStore();
        Assert.Equal("hi %missing%", store.Expand("hi %missing%"));
    }

    [Fact]
    public void Expand_LeavesLoneOrUnterminatedPercent()
    {
        var store = new VariableStore();
        Assert.Equal("100% sure", store.Expand("100% sure"));
    }

    [Fact]
    public void Expand_HandlesMultipleVariables()
    {
        var store = new VariableStore();
        store.Set("a", "1");
        store.Set("b", "2");
        Assert.Equal("1-2", store.Expand("%a%-%b%"));
    }

    [Fact]
    public void Constructor_SeedsFromDictionary()
    {
        var store = new VariableStore(new Dictionary<string, string> { ["k"] = "v" });
        Assert.Equal("v", store.Get("k"));
    }
}
