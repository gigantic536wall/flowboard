using Flowboard.Core.Domain;

namespace Flowboard.Tests;

public class RankTests
{
    [Fact]
    public void First_key_is_in_the_middle()
    {
        Assert.Equal("i", Rank.Between(null, null));
    }

    [Theory]
    [InlineData(null, "i")]
    [InlineData("i", null)]
    [InlineData("a", "b")]
    [InlineData("a", "a1")]
    [InlineData("0z", "1")]
    [InlineData("zz", null)]
    [InlineData(null, "01")]
    public void Key_sorts_strictly_between_its_neighbours(string? before, string? after)
    {
        var key = Rank.Between(before, after);
        if (before is not null)
        {
            Assert.True(string.CompareOrdinal(before, key) < 0, $"{before} < {key}");
        }

        if (after is not null)
        {
            Assert.True(string.CompareOrdinal(key, after) < 0, $"{key} < {after}");
        }

        Assert.NotEqual('0', key[^1]);
    }

    [Fact]
    public void Random_inserts_keep_a_valid_strictly_increasing_order()
    {
        var random = new Random(42);
        var keys = new List<string>();
        for (var i = 0; i < 3000; i++)
        {
            var index = random.Next(keys.Count + 1);
            var key = Rank.Between(index > 0 ? keys[index - 1] : null, index < keys.Count ? keys[index] : null);
            keys.Insert(index, key);
        }

        for (var i = 1; i < keys.Count; i++)
        {
            Assert.True(string.CompareOrdinal(keys[i - 1], keys[i]) < 0);
        }

        Assert.All(keys, k => Assert.NotEqual('0', k[^1]));
        // Random positions keep keys short.
        Assert.True(keys.Max(k => k.Length) <= 8, $"longest key: {keys.Max(k => k.Length)}");
    }

    [Fact]
    public void Always_inserting_at_the_top_grows_keys_slowly_enough_to_need_a_rebalance_eventually()
    {
        string? top = null;
        var lengths = new List<int>();
        for (var i = 0; i < 100; i++)
        {
            top = Rank.Between(null, top);
            lengths.Add(top.Length);
        }

        Assert.True(lengths[^1] > Rank.RebalanceLength / 2);
        Assert.True(lengths[19] < 6, "twenty inserts at the top still give short keys");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(50)]
    [InlineData(1295)]
    [InlineData(3000)]
    public void Spread_gives_evenly_spaced_increasing_keys(int count)
    {
        var keys = Rank.Spread(count);
        Assert.Equal(count, keys.Count);
        for (var i = 1; i < keys.Count; i++)
        {
            Assert.True(string.CompareOrdinal(keys[i - 1], keys[i]) < 0, $"{keys[i - 1]} < {keys[i]}");
        }

        Assert.All(keys, k => Assert.NotEqual('0', k[^1]));
        // Spread keys leave room on both sides.
        Assert.NotNull(Rank.Between(null, keys[0]));
        Assert.NotNull(Rank.Between(keys[^1], null));
    }

    [Theory]
    [InlineData("b", "a")]
    [InlineData("a", "a")]
    [InlineData("A", null)]
    [InlineData("a0", null)]
    public void Invalid_input_is_rejected(string? before, string? after)
    {
        Assert.Throws<ArgumentException>(() => Rank.Between(before, after));
    }
}
