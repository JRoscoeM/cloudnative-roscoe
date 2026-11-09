namespace App.Tests;

public class OccupancyTests
{
    private static readonly MapArea Area = new(0, 0, 10, 10);

    private static RoomStatus Room(string id, int? people, int? minutesToFull = null) =>
        new(id, id, "Talk", 100, people, minutesToFull, Area);

    [Theory]
    [InlineData(59, Busyness.Open)]
    [InlineData(60, Busyness.Filling)]
    [InlineData(84, Busyness.Filling)]
    [InlineData(85, Busyness.Packed)]
    [InlineData(99, Busyness.Packed)]
    [InlineData(100, Busyness.Full)]
    [InlineData(130, Busyness.Full)]
    public void Fullness_maps_to_a_level(int people, Busyness expected)
    {
        Assert.Equal(expected, Room("a", people).Level);
    }

    [Fact]
    public void A_room_with_no_reading_is_unknown_not_empty()
    {
        var room = Room("a", null);

        Assert.Equal(Busyness.Unknown, room.Level);
        Assert.Equal("No reading yet", room.Outlook);
        Assert.Equal("–", room.PercentText);
    }

    [Fact]
    public void Percent_stops_at_100_when_a_room_is_over_capacity()
    {
        Assert.Equal(100, Room("a", 130).Percent);
    }

    [Fact]
    public void Percent_is_zero_for_a_room_without_a_capacity()
    {
        var room = new RoomStatus("a", "a", "Talk", 0, 5, null, Area);

        Assert.Equal(0, room.Percent);
    }

    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(1, 30, 1)]
    [InlineData(14, 20, 5)]
    public void Queue_wait_rounds_up_to_whole_minutes(int people, int secondsPerPerson, int expected)
    {
        var queue = new QueueStatus("q", "Queue", "Q", people, secondsPerPerson, Area);

        Assert.Equal(expected, queue.WaitMinutes);
    }

    [Fact]
    public void A_queue_with_no_sensor_has_no_wait_time()
    {
        var queue = new QueueStatus("q", "Queue", "Q", null, 30, Area);

        Assert.Null(queue.WaitMinutes);
        Assert.Equal("No data", queue.WaitText);
    }

    [Fact]
    public void Recommendations_skip_full_rooms_and_rooms_without_readings()
    {
        var board = new Board(
            [Room("full", 100, 1), Room("busy", 90, 5), Room("quiet", 20, 30), Room("unknown", null)],
            [],
            IsSample: true);

        Assert.Equal("quiet", board.MostSpace?.Id);
        Assert.Equal("busy", board.FillingFastest?.Id);
    }

    [Fact]
    public void Rooms_without_readings_sort_last()
    {
        var board = new Board([Room("unknown", null), Room("busy", 90), Room("quiet", 20)], [], IsSample: false);

        Assert.Equal(["quiet", "busy", "unknown"], board.RoomsWithMostSpaceFirst.Select(room => room.Id));
    }

    [Fact]
    public void Empty_board_has_no_recommendations()
    {
        var board = new Board([], [], IsSample: false);

        Assert.Null(board.MostSpace);
        Assert.Null(board.FillingFastest);
        Assert.Null(board.ShortestLine);
        Assert.False(board.HasAnyReading);
    }
}

public class OccupancyEstimatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero);

    private static Reading Sensor(int people, double minutesAgo = 0) =>
        new("room", ReadingSource.Sensor, people, 0.8, Now.AddMinutes(-minutesAgo));

    private static Reading Photo(int people, double minutesAgo = 0, double confidence = 1) =>
        new("room", ReadingSource.Snapshot, people, confidence, Now.AddMinutes(-minutesAgo));

    [Fact]
    public void No_readings_means_no_estimate()
    {
        Assert.Null(OccupancyEstimator.Estimate([], Now));
    }

    [Fact]
    public void Latest_fresh_sensor_wins()
    {
        var estimate = OccupancyEstimator.Estimate([Sensor(10, 1), Sensor(20, 0.5)], Now);

        Assert.Equal(20, estimate?.People);
        Assert.Equal(ReadingSource.Sensor, estimate?.Source);
    }

    [Fact]
    public void A_stale_sensor_is_ignored()
    {
        Assert.Null(OccupancyEstimator.Estimate([Sensor(10, 3)], Now));
    }

    [Fact]
    public void A_snapshot_stands_in_when_no_sensor_is_live()
    {
        var estimate = OccupancyEstimator.Estimate([Sensor(10, 3), Photo(40, 5)], Now);

        Assert.Equal(40, estimate?.People);
        Assert.Equal(ReadingSource.Snapshot, estimate?.Source);
    }

    [Fact]
    public void An_expired_snapshot_is_ignored()
    {
        Assert.Null(OccupancyEstimator.Estimate([Photo(40, 16)], Now));
    }

    [Fact]
    public void A_higher_snapshot_corrects_an_undercounting_sensor()
    {
        var estimate = OccupancyEstimator.Estimate([Sensor(20), Photo(60)], Now);

        Assert.Equal(60, estimate?.People);
    }

    [Fact]
    public void A_snapshot_counts_for_less_as_it_ages()
    {
        // Halfway through its life, a fully confident photo closes half the gap.
        var estimate = OccupancyEstimator.Estimate([Sensor(20), Photo(60, 7.5)], Now);

        Assert.Equal(40, estimate?.People);
    }

    [Fact]
    public void A_lower_snapshot_never_pulls_a_sensor_down()
    {
        var estimate = OccupancyEstimator.Estimate([Sensor(50), Photo(10)], Now);

        Assert.Equal(50, estimate?.People);
    }
}

public class FillForecastTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero);

    private static IEnumerable<Reading> Trend(params (double MinutesAgo, int People)[] points) =>
        points.Select(point => new Reading("room", ReadingSource.Sensor, point.People, 0.8, Now.AddMinutes(-point.MinutesAgo)));

    private static readonly Reading[] Rising = [.. Trend((4, 40), (2, 50), (0, 60))];

    [Fact]
    public void Projects_time_to_capacity_from_the_trend()
    {
        Assert.Equal(8, FillForecast.MinutesToFull(Rising, 60, 100, Now, inUseUntil: null));
    }

    [Fact]
    public void A_flat_room_has_no_forecast()
    {
        var flat = Trend((4, 60), (2, 60), (0, 60));

        Assert.Null(FillForecast.MinutesToFull(flat, 60, 100, Now, inUseUntil: null));
    }

    [Fact]
    public void An_emptying_room_has_no_forecast()
    {
        var falling = Trend((4, 80), (2, 70), (0, 60));

        Assert.Null(FillForecast.MinutesToFull(falling, 60, 100, Now, inUseUntil: null));
    }

    [Fact]
    public void Too_few_readings_give_no_forecast()
    {
        var sparse = Trend((2, 50), (0, 60));

        Assert.Null(FillForecast.MinutesToFull(sparse, 60, 100, Now, inUseUntil: null));
    }

    [Fact]
    public void Readings_bunched_together_give_no_forecast()
    {
        var bunched = Trend((0.4, 50), (0.2, 55), (0, 60));

        Assert.Null(FillForecast.MinutesToFull(bunched, 60, 100, Now, inUseUntil: null));
    }

    [Fact]
    public void A_room_that_empties_before_filling_has_no_forecast()
    {
        Assert.Null(FillForecast.MinutesToFull(Rising, 60, 100, Now, inUseUntil: Now.AddMinutes(5)));
    }

    [Fact]
    public void A_full_room_has_no_forecast()
    {
        Assert.Null(FillForecast.MinutesToFull(Rising, 100, 100, Now, inUseUntil: null));
    }

    [Fact]
    public void Snapshots_are_not_part_of_the_trend()
    {
        var photos = new[]
        {
            new Reading("room", ReadingSource.Snapshot, 40, 1, Now.AddMinutes(-4)),
            new Reading("room", ReadingSource.Snapshot, 50, 1, Now.AddMinutes(-2)),
            new Reading("room", ReadingSource.Snapshot, 60, 1, Now),
        };

        Assert.Null(FillForecast.PeoplePerMinute(photos, Now));
    }
}

public class ScheduleTests
{
    private static readonly DateOnly Monday = new(2026, 11, 9);
    private static readonly DateTimeOffset Afternoon = new(2026, 11, 9, 14, 30, 0, Venue.UtcOffset);

    private static readonly Session[] Talks =
    [
        new(1, Monday, "14:00", "15:00", "hall-de", "Hall DE", "Panel"),
        new(2, Monday, "15:05", "16:00", "hall-de", "Hall DE", "Next door"),
        new(3, Monday.AddDays(1), "14:00", "15:00", "hall-de", "Hall DE", "Tomorrow"),
    ];

    [Fact]
    public void Finds_the_talk_running_in_a_room()
    {
        Assert.Equal("Panel", Schedule.Current("hall-de", Afternoon, Talks)?.Title);
    }

    [Fact]
    public void A_talk_only_runs_on_its_own_day()
    {
        var tuesdayEvening = new DateTimeOffset(2026, 11, 10, 20, 0, 0, Venue.UtcOffset);

        Assert.Null(Schedule.Current("hall-de", tuesdayEvening, Talks));
    }

    [Fact]
    public void Back_to_back_talks_keep_a_room_in_use()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 16, 0, 0, Venue.UtcOffset), Schedule.InUseUntil("hall-de", Afternoon, Talks));
    }

    [Fact]
    public void Nothing_running_means_no_in_use_time()
    {
        var early = new DateTimeOffset(2026, 11, 9, 8, 0, 0, Venue.UtcOffset);

        Assert.Null(Schedule.Current("hall-de", early, Talks));
        Assert.Null(Schedule.InUseUntil("hall-de", early, Talks));
    }

    [Fact]
    public void The_next_talk_can_be_on_a_later_day()
    {
        var evening = new DateTimeOffset(2026, 11, 9, 20, 0, 0, Venue.UtcOffset);

        Assert.Equal("Tomorrow", Schedule.Next("hall-de", evening, Talks)?.Title);
    }

    [Fact]
    public void The_real_schedule_loads_with_sensible_dates()
    {
        Assert.True(Schedule.Sessions.Count > 500);
        Assert.Equal(Schedule.Sessions.Count, Schedule.Sessions.Select(session => session.Id).Distinct().Count());
        Assert.All(Schedule.Sessions, session =>
        {
            Assert.InRange(session.Date, new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 12));
            Assert.True(session.Ends > session.Starts, session.Title);
            Assert.False(string.IsNullOrWhiteSpace(session.Title));
        });
    }

    [Theory]
    [InlineData("Hall DE", "hall-de")]
    [InlineData("250 A-C", "250-a-c")]
    [InlineData("Halls A-C, Halls 1-5", "halls-a-c-halls-1-5")]
    public void Room_names_match_the_ids_on_the_schedule(string name, string expected)
    {
        Assert.Equal(expected, VenueIds.Slug(name));
    }
}

public class SensorAuthTests
{
    private readonly SensorAuth _auth = new("setup-key");

    [Fact]
    public void A_token_only_works_for_its_own_target()
    {
        var token = _auth.TokenFor("room-101");

        Assert.True(_auth.IsValidToken("room-101", token));
        Assert.False(_auth.IsValidToken("room-102", token));
    }

    [Fact]
    public void Without_a_setup_key_nothing_is_accepted()
    {
        var auth = new SensorAuth(null);

        Assert.False(auth.Enabled);
        Assert.False(auth.IsSetupKey(""));
        Assert.False(auth.IsValidToken("room-101", "anything"));
    }

    [Fact]
    public void A_wrong_setup_key_is_rejected()
    {
        Assert.False(_auth.IsSetupKey("wrong"));
        Assert.True(_auth.IsSetupKey("setup-key"));
    }
}

public class SnapshotParsingTests
{
    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var estimate = OpenAiSnapshotAnalyzer.Parse(
            """{"people_visible":-3,"fullness_percent":140,"open_seats":-1,"confidence":7}""");

        Assert.Equal(new SnapshotEstimate(0, 100, 0, 1), estimate);
    }
}
