using Rigsight.Agent;

namespace Rigsight.Tests.Agent;

/// <summary>A part of the agent's pass that fails every second is noted, not written to the log every second.</summary>
public class RepeatedErrorsTests
{
    [Fact]
    public void The_same_failure_is_written_once_then_once_every_ten_minutes_with_a_count()
    {
        long now = 0;
        var lines = new List<string>();
        var errors = new RepeatedErrors("sampler", () => now, lines.Add);

        for (int second = 0; second < 600; second++, now += 1000)
            errors.Failed("activity", new InvalidOperationException("database or disk is full"));
        Assert.Single(lines);
        Assert.StartsWith("activity failed: System.InvalidOperationException: database or disk is full", lines[0]);

        errors.Failed("activity", new InvalidOperationException("database or disk is full"));
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("activity failed again, 599 more times in the last 10 minutes", lines[1]);
    }

    [Fact]
    public void Another_part_or_another_kind_of_error_is_written_at_once()
    {
        long now = 0;
        var lines = new List<string>();
        var errors = new RepeatedErrors("sampler", () => now, lines.Add);
        errors.Failed("activity", new InvalidOperationException("full"));
        errors.Failed("sensors", new InvalidOperationException("full"));
        errors.Failed("activity", new IOException("busy"));
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void Run_notes_a_failure_and_lets_the_caller_go_on()
    {
        var lines = new List<string>();
        var errors = new RepeatedErrors("sampler", () => 0, lines.Add);
        bool after = false;
        errors.Run("daily recap", () => throw new InvalidOperationException("no"));
        errors.Run("Rewinds", () => after = true);
        Assert.True(after);
        Assert.Single(lines);
    }
}
