namespace Matchmate.Web.Experience;

// Authored illustration coordinates only; never football measurements or tool evidence.
public static class MatchSequenceLayout
{
    public static bool AttacksRight(int? periodOrder, string? team, string homeTeam) => (periodOrder != 2) == (team == homeTeam);
    public static (double X, double Y) Point(string kind, bool attacksRight, double length = 100, double width = 100)
    {
        var (x, y) = kind switch { "corner" => (.99, .02), "headed_assist" => (.90, .38), "header_finish" => (.96, .58),
            "advance" => (.73, .22), "keeper_out" => (.86, .43), "uncertain_goalmouth" => (.92, .60),
            "throw_in" => (.82, .02), "into_area" => (.84, .31), "release_pass" => (.64, .28), "shot" => (.85, .30), "save" => (.93, .48),
            "rebound_finish" => (.96, .62), "cross" => (.86, .16), "deflection" => (.92, .43), "reported_assist" => (.75, .32), "net" => (.998, .50), _ => (.96, .66) };
        return (length * (attacksRight ? x : 1 - x), width * y);
    }
}
