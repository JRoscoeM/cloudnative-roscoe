namespace App;

public sealed record FloorPlanLayout(IReadOnlyDictionary<string, MapArea> Areas, int Height);

/// <summary>
/// Places any number of rooms and queues on the map: the largest room across
/// the top, the rest in two columns, and the queues in a band below the first
/// row. Organizers only set names and sizes, so this has to work for any venue.
/// </summary>
public static class FloorPlan
{
    private const int Left = 8;
    private const int FullWidth = 344;
    private const int HalfWidth = 168;
    private const int Gap = 8;
    private const int LeadHeight = 112;
    private const int RoomHeight = 96;
    private const int QueueHeight = 40;
    private const int QueueGap = 7;
    private const int QueuesPerRow = 3;

    public static FloorPlanLayout Compute(VenueSnapshot venue)
    {
        var areas = new Dictionary<string, MapArea>();
        var y = Gap;

        if (venue.Rooms.Count > 0)
        {
            areas[venue.Rooms[0].Id] = new MapArea(Left, y, FullWidth, LeadHeight);
            y += LeadHeight + Gap;
        }

        var rows = venue.Rooms.Skip(1).Chunk(2).ToList();
        if (rows.Count == 0)
        {
            y = PlaceQueues(venue.Queues, areas, y);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            y = PlaceRow(rows[index], areas, y);
            if (index == 0)
            {
                y = PlaceQueues(venue.Queues, areas, y);
            }
        }

        return new FloorPlanLayout(areas, y);
    }

    private static int PlaceRow(RoomDefinition[] rooms, Dictionary<string, MapArea> areas, int y)
    {
        if (rooms.Length == 1)
        {
            areas[rooms[0].Id] = new MapArea(Left, y, FullWidth, RoomHeight);
        }
        else
        {
            areas[rooms[0].Id] = new MapArea(Left, y, HalfWidth, RoomHeight);
            areas[rooms[1].Id] = new MapArea(Left + HalfWidth + Gap, y, HalfWidth, RoomHeight);
        }

        return y + RoomHeight + Gap;
    }

    private static int PlaceQueues(IReadOnlyList<QueueDefinition> queues, Dictionary<string, MapArea> areas, int y)
    {
        if (queues.Count == 0)
        {
            return y;
        }

        y += Gap;
        foreach (var row in queues.Chunk(QueuesPerRow))
        {
            var width = (FullWidth - (QueueGap * (row.Length - 1))) / row.Length;
            for (var index = 0; index < row.Length; index++)
            {
                areas[row[index].Id] = new MapArea(Left + (index * (width + QueueGap)), y, width, QueueHeight);
            }

            y += QueueHeight + Gap;
        }

        return y + Gap;
    }
}
