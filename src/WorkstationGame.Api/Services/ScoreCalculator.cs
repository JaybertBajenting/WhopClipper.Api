namespace WorkstationGame.Api.Services;

public static class ScoreCalculator
{
    public static int ForCorrectRank(int rank) => rank switch
    {
        1 => 100,
        2 => 75,
        3 => 50,
        > 3 => 25,
        _ => 0
    };
}

