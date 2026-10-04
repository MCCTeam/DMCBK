using DMCBK.Core.Beacon;
using Umpk.Nbt;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Sign NBT extraction: modern front_text joins, legacy Text1-4 fallback, non-sign NBT yields none, empty lines skipped.
/// Drives the tolerant JSON-component extractor behind GetSignTextAsync.
/// </summary>
public sealed class SignTextTests
{
    private static NbtCompound ModernFront(params string[] messages)
    {
        var list = new NbtList();
        foreach (string message in messages)
            list.Add(new NbtString(message));

        var front = new NbtCompound();
        front.Put("messages", list);
        var nbt = new NbtCompound();
        nbt.Put("front_text", front);
        return nbt;
    }

    private static NbtCompound Legacy(string? text1, string? text2, string? text3, string? text4)
    {
        var nbt = new NbtCompound();
        if (text1 is not null)
            nbt.PutString("Text1", text1);

        if (text2 is not null)
            nbt.PutString("Text2", text2);

        if (text3 is not null)
            nbt.PutString("Text3", text3);

        if (text4 is not null)
            nbt.PutString("Text4", text4);

        return nbt;
    }

    [Fact]
    public void ModernFrontText_JoinsLines()
    {
        NbtCompound nbt = ModernFront(
            "{\"text\":\"Storage\"}", "{\"text\":\"diamonds\"}", "\"\"", "{\"text\":\"\"}");
        Assert.Equal("Storage\ndiamonds", SignComponentText.FromSignNbt(nbt));
    }

    [Fact]
    public void ModernFrontText_ExtraNesting_Concats()
    {
        NbtCompound nbt = ModernFront(
            "{\"text\":\"\",\"extra\":[{\"text\":\"St\"},{\"text\":\"orage\"}]}",
            "\"\"",
            "\"\"",
            "\"\"");
        Assert.Equal("Storage", SignComponentText.FromSignNbt(nbt));
    }

    [Fact]
    public void ModernFrontText_TranslateFallback_UsesKey()
    {
        NbtCompound nbt = ModernFront(
            "{\"translate\":\"container.chest\"}", "\"\"", "\"\"", "\"\"");
        Assert.Equal("container.chest", SignComponentText.FromSignNbt(nbt));
    }

    [Fact]
    public void ModernFrontText_PlainJsonString_AsIs()
    {
        NbtCompound nbt = ModernFront("\"Storage\"", "\"\"", "\"\"", "\"\"");
        Assert.Equal("Storage", SignComponentText.FromSignNbt(nbt));
    }

    [Fact]
    public void ModernFrontText_InvalidJson_TolerantAsIs()
    {
        NbtCompound nbt = ModernFront("Storage", "\"\"", "\"\"", "\"\"");
        Assert.Equal("Storage", SignComponentText.FromSignNbt(nbt));
    }

    [Fact]
    public void LegacyText1Through4_JoinsNonEmpty()
    {
        NbtCompound nbt = Legacy("{\"text\":\"Stor\"}", "{\"text\":\"age\"}", "\"\"", "{\"text\":\"\"}");
        Assert.Equal("Stor\nage", SignComponentText.FromSignNbt(nbt));
    }

    [Fact]
    public void NonSignNbt_ReturnsNull()
    {
        var nbt = new NbtCompound();
        nbt.PutString("id", "minecraft:chest");
        Assert.Null(SignComponentText.FromSignNbt(nbt));
    }

    [Fact]
    public void NullNbt_ReturnsNull()
    {
        Assert.Null(SignComponentText.FromSignNbt(null));
    }

    [Fact]
    public void EmptyLines_Skipped()
    {
        NbtCompound nbt = ModernFront(
            "{\"text\":\"Hello\"}", "{\"text\":\"\"}", "{\"text\":\"World\"}", "\"\"");
        Assert.Equal("Hello\nWorld", SignComponentText.FromSignNbt(nbt));
    }

    [Fact]
    public void ComponentArray_Concats()
    {
        Assert.Equal("ab", SignComponentText.ToPlainText("[\"a\",{\"text\":\"b\"}]"));
    }

    [Fact]
    public void ComponentNumbersAndBools_ToString()
    {
        Assert.Equal("42", SignComponentText.ToPlainText("42"));
        Assert.Equal("true", SignComponentText.ToPlainText("true"));
    }
}
