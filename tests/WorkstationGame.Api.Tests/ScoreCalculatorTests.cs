using WorkstationGame.Api.Services;
using Xunit;

namespace WorkstationGame.Api.Tests;

public sealed class ScoreCalculatorTests
{
    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 75)]
    [InlineData(3, 50)]
    [InlineData(4, 25)]
    [InlineData(100, 25)]
    [InlineData(0, 0)]
    public void ReturnsExpectedScore(int rank, int expected)
    {
        Assert.Equal(expected, ScoreCalculator.ForCorrectRank(rank));
    }
}
