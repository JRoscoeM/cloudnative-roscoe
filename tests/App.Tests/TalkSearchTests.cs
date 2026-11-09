using System.Text;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace App.Tests;

public class ElasticTalkSearchTests
{
    private const string OneHit = """{"hits":{"total":{"value":1,"relation":"eq"},"hits":[{"_id":"4","_source":{"sessionId":4}}]}}""";

    private static readonly Dictionary<string, IEnumerable<string>> ProductHeader = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x-elastic-product"] = ["Elasticsearch"],
    };

    private readonly List<(string Path, string Body)> _requests = [];

    private ElasticTalkSearch Search(string responseJson, int status = 200)
    {
        var settings = new ElasticsearchClientSettings(
                new SingleNodePool(new Uri("http://elastic.test:9200")),
                new InMemoryRequestInvoker(Encoding.UTF8.GetBytes(responseJson), status, headers: ProductHeader))
            .DisableDirectStreaming()
            .ResponseHeadersToParse(new HeadersList("x-elastic-product"))
            .OnRequestCompleted(call => _requests.Add((
                call.Uri!.AbsolutePath,
                call.RequestBodyInBytes is { } body ? Encoding.UTF8.GetString(body) : "")));

        return new ElasticTalkSearch(new ElasticsearchClient(settings));
    }

    [Fact]
    public async Task Search_combines_keywords_and_meaning()
    {
        var ids = await Search(OneHit).SearchAsync("cheaper clusters", CancellationToken.None);

        var (path, body) = _requests.Single();
        Assert.Equal("/livevision-talks/_search", path);
        Assert.Contains("\"rrf\"", body);
        Assert.Contains("\"multi_match\"", body);
        Assert.Contains("\"semantic\"", body);
        Assert.Equal([4], ids);
    }

    [Fact]
    public async Task A_query_cannot_break_out_of_the_request()
    {
        const string hostile = "x\"}},{\"evil\":\"y";

        await Search(OneHit).SearchAsync(hostile, CancellationToken.None);

        using var body = System.Text.Json.JsonDocument.Parse(_requests.Single().Body);
        var keywordQuery = body.RootElement
            .GetProperty("retriever").GetProperty("rrf").GetProperty("retrievers")[0]
            .GetProperty("standard").GetProperty("query").GetProperty("multi_match").GetProperty("query");
        Assert.Equal(hostile, keywordQuery.GetString());
    }

    [Fact]
    public async Task Similar_talks_exclude_the_talk_itself()
    {
        var session = Schedule.Sessions[0];

        await Search(OneHit).SimilarAsync(session, CancellationToken.None);

        Assert.Contains($"\"sessionId\": {session.Id}", _requests.Single().Body);
        Assert.Contains("must_not", _requests.Single().Body);
    }

    private static readonly Session[] OneTalk =
    [
        new(1, new DateOnly(2026, 11, 10), "10:00", "11:00", "hall-de", "Hall DE", "A talk"),
    ];

    [Fact]
    public async Task Indexing_writes_every_talk_with_stable_ids()
    {
        await Search("""{"errors":false,"items":[]}""").IndexAllAsync(OneTalk, CancellationToken.None);

        Assert.Equal("/livevision-talks", _requests[0].Path);
        Assert.Contains("semantic_text", _requests[0].Body);
        Assert.Equal("/livevision-talks/_count", _requests[1].Path);
        Assert.Equal("/livevision-talks/_bulk", _requests[2].Path);
        Assert.Contains("""{"index":{"_id":"1"}}""", _requests[2].Body);
        Assert.Contains("semantic_content", _requests[2].Body);
    }

    [Fact]
    public async Task An_index_that_is_already_complete_is_not_rewritten()
    {
        await Search("""{"count":1}""").IndexAllAsync(OneTalk, CancellationToken.None);

        Assert.DoesNotContain(_requests, request => request.Path.EndsWith("/_bulk"));
    }

    [Fact]
    public async Task Talks_are_written_in_batches()
    {
        var many = Enumerable.Range(1, 120)
            .Select(id => new Session(id, new DateOnly(2026, 11, 10), "10:00", "11:00", "hall-de", "Hall DE", $"Talk {id}"))
            .ToList();

        await Search("""{"errors":false,"items":[]}""").IndexAllAsync(many, CancellationToken.None);

        Assert.Equal(3, _requests.Count(request => request.Path.EndsWith("/_bulk")));
    }

    [Fact]
    public async Task A_rejected_search_is_reported()
    {
        await Assert.ThrowsAsync<TalkSearchException>(() =>
            Search("""{"error":"nope"}""", 400).SearchAsync("x", CancellationToken.None));
    }
}

public class TalkFinderTests
{
    private static readonly Session Known = Schedule.Sessions[3];

    // A real title, for tests of the keyword fallback.
    private static readonly string Keyword = Schedule.Sessions[0].Title;

    private sealed class BrokenSearch : ITalkSearch
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<int>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            Calls++;
            throw new TalkSearchException("down");
        }

        public Task<IReadOnlyList<int>> SimilarAsync(Session session, CancellationToken cancellationToken) =>
            throw new TalkSearchException("down");
    }

    private sealed class CountingSearch : ITalkSearch
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<int>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<int>>([Known.Id, 999]);
        }

        public Task<IReadOnlyList<int>> SimilarAsync(Session session, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>([]);
    }

    private sealed class EmptyThenFilledSearch : ITalkSearch
    {
        public IReadOnlyList<int> Hits { get; set; } = [];

        public Task<IReadOnlyList<int>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult(Hits);

        public Task<IReadOnlyList<int>> SimilarAsync(Session session, CancellationToken cancellationToken) => Task.FromResult(Hits);
    }

    [Fact]
    public async Task An_empty_index_falls_back_to_keywords_and_is_not_cached()
    {
        var search = new EmptyThenFilledSearch();
        var finder = Finder(search);

        var whileIndexing = await finder.SearchAsync(Keyword, CancellationToken.None);
        search.Hits = [Known.Id];
        var afterIndexing = await finder.SearchAsync(Keyword, CancellationToken.None);

        Assert.Contains(whileIndexing, session => session.Title == Keyword);
        Assert.Equal(Known.Id, Assert.Single(afterIndexing).Id);
    }

    [Fact]
    public async Task No_similar_talks_are_not_cached()
    {
        var search = new EmptyThenFilledSearch();
        var finder = Finder(search);

        Assert.Empty(await finder.SimilarAsync(Schedule.Sessions[0], CancellationToken.None));
        search.Hits = [Known.Id];

        Assert.Equal(Known.Id, Assert.Single(await finder.SimilarAsync(Schedule.Sessions[0], CancellationToken.None)).Id);
    }

    private static TalkFinder Finder(ITalkSearch search) => new(
        search,
        new SearchBudget(),
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<TalkFinder>.Instance);

    [Fact]
    public async Task A_search_outage_falls_back_to_keywords()
    {
        var results = await Finder(new BrokenSearch()).SearchAsync(Keyword, CancellationToken.None);

        Assert.Contains(results, session => session.Title == Keyword);
    }

    [Fact]
    public async Task A_suggestion_outage_means_no_suggestion()
    {
        Assert.Empty(await Finder(new BrokenSearch()).SimilarAsync(Schedule.Sessions[0], CancellationToken.None));
    }

    [Fact]
    public async Task Repeat_searches_use_the_cache_and_unknown_ids_are_dropped()
    {
        var search = new CountingSearch();
        var finder = Finder(search);

        await finder.SearchAsync("Tracing", CancellationToken.None);
        var results = await finder.SearchAsync("tracing ", CancellationToken.None);

        Assert.Equal(1, search.Calls);
        Assert.Equal(Known.Id, Assert.Single(results).Id);
    }

    [Fact]
    public async Task A_blank_search_never_reaches_elasticsearch()
    {
        var search = new CountingSearch();

        var results = await Finder(search).SearchAsync("   ", CancellationToken.None);

        Assert.Equal(0, search.Calls);
        Assert.Equal(Schedule.Sessions.Count, results.Count);
    }
}

public class TalkSuggestionTests
{
    // 14:30 at the venue: talks 5 (Room 101), 6 (Room 102) and 7 (Main Hall) are running.
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 14, 30, 0, Venue.UtcOffset);
    private static readonly MapArea Area = new(0, 0, 1, 1);

    private static RoomStatus Room(string id, int? people) => new(id, id, null, 10, people, null, Area);

    private static readonly Session[] Talks =
    [
        Fixture(4, "13:00", "14:00", "room-102", "Tracing without tears"),
        Fixture(5, "14:00", "15:00", "room-101", "GitOps in practice"),
        Fixture(6, "14:00", "15:00", "room-102", "Logs, metrics and sanity"),
        Fixture(7, "14:00", "15:00", "main-hall", "Panel"),
        Fixture(12, "16:00", "17:00", "room-102", "Agents in production"),
    ];

    private static Session Fixture(int id, string time, string end, string room, string title) =>
        new(id, new DateOnly(2026, 11, 9), time, end, room, room, title);

    private static Session Talk(int id) => Talks.Single(session => session.Id == id);

    [Fact]
    public void Suggests_the_closest_running_talk_with_space()
    {
        RoomStatus[] rooms = [Room("room-101", 10), Room("room-102", 3), Room("main-hall", 2)];

        var pick = TalkSuggestions.Pick(rooms[0], [Talk(6), Talk(7)], rooms, Now);

        Assert.Equal("Logs, metrics and sanity", pick?.Title);
        Assert.Equal(30, pick?.Percent);
    }

    [Fact]
    public void Skips_rooms_that_are_also_busy_or_unknown()
    {
        RoomStatus[] rooms = [Room("room-101", 10), Room("room-102", 9), Room("main-hall", null)];

        Assert.Null(TalkSuggestions.Pick(rooms[0], [Talk(6), Talk(7)], rooms, Now));
    }

    [Fact]
    public void Skips_talks_that_have_ended_or_are_far_off()
    {
        RoomStatus[] rooms = [Room("room-101", 10), Room("room-102", 3)];

        // Talk 4 ended at 14:00, and talk 12 doesn't start until 16:00.
        Assert.Null(TalkSuggestions.Pick(rooms[0], [Talk(4), Talk(12)], rooms, Now));
    }

    [Fact]
    public void Search_puts_talks_you_can_attend_first()
    {
        var board = new Board([Room("room-101", 10), Room("room-102", 3)], [], IsSample: false);

        var results = TalkSuggestions.ForSearch([Talk(4), Talk(5), Talk(6)], board, Now);

        // Ended (4) goes last, the full room (5) after the open one (6).
        Assert.Equal([6, 5, 4], results.Select(result => result.Session.Id));
        Assert.Equal("Full · 100%", results[1].Status);
        Assert.Equal("Ended", results[2].Status);
    }

    [Fact]
    public void A_room_matches_talks_by_its_current_name()
    {
        var renamed = new RoomStatus("main-hall", "Room 102", null, 10, 3, null, Area);
        RoomStatus[] rooms = [Room("room-101", 10), renamed];

        var pick = TalkSuggestions.Pick(rooms[0], [Talk(6)], rooms, Now);

        Assert.Equal("main-hall", pick?.RoomId);
    }

    [Fact]
    public void Search_results_are_capped()
    {
        var many = Enumerable.Range(101, 200)
            .Select(id => new Session(id, new DateOnly(2026, 11, 10), "10:00", "11:00", "x", "X", $"Talk {id}"));
        var board = new Board([], [], IsSample: false);

        Assert.Equal(30, TalkSuggestions.ForSearch(many, board, Now).Count);
    }

    [Fact]
    public void The_schedule_list_shows_what_is_next_not_the_whole_week()
    {
        var many = Enumerable.Range(101, 40)
            .Select(id => new Session(id, new DateOnly(2026, 11, 10), "10:00", "11:00", "x", "X", $"Talk {id}"))
            .Append(Talk(4))
            .ToList();
        var board = new Board([], [], IsSample: false);

        var results = TalkSuggestions.ForDisplay(many, board, Now);

        Assert.Equal(30, results.Count);
        Assert.DoesNotContain(results, result => result.Session.Id == 4);
    }
}
