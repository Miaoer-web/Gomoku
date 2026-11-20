using System.Security.Cryptography;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var rooms = new Dictionary<string, Room>();
var gate = new object();

app.MapGet("/", () => Results.Ok("Gomoku room service"));
app.MapPost("/rooms", () =>
{
    lock (gate)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var expired in rooms.Where(pair => pair.Value.LastAction < cutoff).Select(pair => pair.Key).ToArray())
            rooms.Remove(expired);
        if (rooms.Count >= 9000)
            return Error("房间已满，请稍后再试。", 503);
        string code;
        do { code = RandomNumberGenerator.GetInt32(1000, 10000).ToString(); }
        while (rooms.ContainsKey(code));
        var room = new Room();
        rooms.Add(code, room);
        return Results.Ok(new { code, token = room.Host });
    }
});

app.MapPost("/rooms/{code}/join", (string code) =>
{
    lock (gate)
    {
        if (!TryRoom(code, out var room)) return Error("找不到这个房间。", 404);
        var index = Array.FindIndex(room.Seats, seat => seat == null);
        if (index < 0) return Error("对局席和观战席都已满。", 409);
        var player = new Player();
        room.Seats[index] = player;
        room.LastAction = DateTime.UtcNow;
        return Results.Ok(new { code, token = player.Token });
    }
});

app.MapGet("/rooms/{code}", (string code, HttpRequest request) =>
    WithRoom(code, request, Snapshot));

app.MapPost("/rooms/{code}/leave", (string code, HttpRequest request) =>
    WithRoom(code, request, (room, side) =>
    {
        if (room.IsHost(side)) rooms.Remove(code);
        else
        {
            room.Seats[side - 1] = null;
        }
        return Results.NoContent();
    }));

app.MapPost("/rooms/{code}/seat", (string code, Seat selection, HttpRequest request) =>
    WithRoom(code, request, (room, side) =>
    {
        var target = selection.Side;
        if (target is < 1 or > 3 || room.Seats[target - 1] != null)
            return Error("请选择空席。", 409);
        room.Seats[target - 1] = room.Seats[side - 1];
        room.Seats[side - 1] = null;
        return Snapshot(room, target);
    }));

app.MapPost("/rooms/{code}/mode", (string code, Mode selection, HttpRequest request) =>
    WithRoom(code, request, (room, side) =>
    {
        if (side == 3 || room.InProgress || selection.Value is not ("rotate" or "swap"))
            return Error("请在开局前或本局结束后切换模式。", 409);
        room.Mode = selection.Value;
        return Snapshot(room, side);
    }));

app.MapPost("/rooms/{code}/swap", (string code, Restart change, HttpRequest request) =>
    WithRoom(code, request, (room, side) =>
    {
        if (side == 3 || !room.Ready || room.InProgress || room.Mode != "swap" ||
            room.Round != change.Round || change.Side != side)
            return Error("当前无法互换黑白。", 409);
        room.Swap();
        return Snapshot(room, 3 - side);
    }));

app.MapPost("/rooms/{code}/skin", (string code, Skin skin, HttpRequest request) =>
    WithRoom(code, request, (room, side) =>
    {
        if (skin.Value == null || skin.Value.Length > 48000 ||
            (skin.Value is not ("classic" or "jade" or "metal" or "candy" or "porcelain" or "amber" or
                "kitten" or "puppy" or "orange" or "apple" or "burger" or "chiikawa" or "hachiware" or "usagi") &&
             !skin.Value.StartsWith("png:iVBORw0KGgo", StringComparison.Ordinal)))
            return Error("棋子皮肤格式无效或图片过大。", 400);
        room.Seats[side - 1]!.Skin = skin.Value;
        return Snapshot(room, side);
    })).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(50000));

app.MapPost("/rooms/{code}/start", (string code, Restart start, HttpRequest request) =>
    WithRoom(code, request, (room, side) =>
    {
        if (!room.IsHost(side) || !room.Ready || room.Round != start.Round || room.Started)
            return Error("请等好友加入后，由房主开始对局。", 409);
        room.Started = true;
        room.LastAction = DateTime.UtcNow;
        return Snapshot(room, side);
    }));

app.MapPost("/rooms/{code}/resign", (string code, Restart resign, HttpRequest request) =>
    WithRoom(code, request, (room, side) =>
    {
        if (side == 3 || !room.Ready || !room.InProgress || room.Round != resign.Round || resign.Side != side)
            return Error("当前没有可以认输的对局。", 409);
        room.ResignedBy = side;
        room.Finish(3 - side);
        room.LastAction = DateTime.UtcNow;
        return Snapshot(room, side);
    }));

app.MapPost("/rooms/{code}/move", (string code, Move move, HttpRequest request) =>
    WithRoom(code, request, (room, side) =>
    {
        if (!room.Ready || !room.InProgress || room.Turn != side || move.Side != side ||
            room.Round != move.Round || room.MoveCount != move.MoveCount)
            return Error("棋局状态已变化，请刷新后再试。", 409);
        if (move.X is < 0 or >= Room.Size || move.Y is < 0 or >= Room.Size)
            return Error("落子超出棋盘。", 400);
        var index = move.Y * Room.Size + move.X;
        if (room.Board[index] != 0) return Error("这里已有棋子。", 409);

        room.Board[index] = side;
        room.LastMove = index;
        room.MoveCount++;
        room.LastAction = DateTime.UtcNow;
        if (room.HasExactFive(move.X, move.Y, side)) room.Finish(side);
        else if (room.MoveCount == room.Board.Length) room.Finish(3);
        else room.Turn = 3 - side;
        return Snapshot(room, side);
    }));

app.MapPost("/rooms/{code}/restart", (string code, Restart restart, HttpRequest request) =>
    WithRoom(code, request, (room, side) =>
    {
        if (side == 3 || !room.Ready || !room.Started || room.Round != restart.Round || restart.Side != side)
            return Error("棋局状态已变化，请刷新后再试。", 409);
        room.LastAction = DateTime.UtcNow;
        room.Reset();
        if (room.Mode == "rotate")
        {
            room.Swap();
            side = 3 - side;
        }
        return Snapshot(room, side);
    }));

if (int.TryParse(Environment.GetEnvironmentVariable("GOMOKU_HOST_PROCESS_ID"), out var hostProcessId))
{
    app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine("GOMOKU_ROOM_READY"));
    _ = WatchHostProcess(hostProcessId, app.Lifetime);
}
app.Run();

static async Task WatchHostProcess(int hostProcessId, IHostApplicationLifetime lifetime)
{
    try
    {
        using var parent = Process.GetProcessById(hostProcessId);
        await parent.WaitForExitAsync(lifetime.ApplicationStopping);
    }
    catch (OperationCanceledException) { return; }
    catch (ArgumentException) { }
    lifetime.StopApplication();
}

IResult WithRoom(string code, HttpRequest request, Func<Room, int, IResult> action)
{
    lock (gate)
    {
        if (!TryRoom(code, out var room)) return Error("房间已关闭，请重新创建或加入。", 404);
        var header = request.Headers.Authorization.ToString();
        var index = Array.FindIndex(room.Seats, player => player != null && header == "Bearer " + player.Token);
        if (index < 0) return Error("你已离开房间，请重新加入。", 401);
        room.Seats[index]!.LastSeen = room.LastAction = DateTime.UtcNow;
        return action(room, index + 1);
    }
}

bool TryRoom(string code, out Room room)
{
    if (!rooms.TryGetValue(code, out room!)) return false;
    var cutoff = DateTime.UtcNow.AddSeconds(-60);
    for (var index = 0; index < room.Seats.Length; index++)
    {
        if (room.Seats[index] is not { } player || player.LastSeen >= cutoff) continue;
        if (player.Token == room.Host)
        {
            rooms.Remove(code);
            return false;
        }
        room.Seats[index] = null;
    }
    return true;
}

static IResult Error(string message, int status) => Results.Json(new { message }, statusCode: status);
static IResult Snapshot(Room room, int side) => Results.Ok(new
{
    board = room.Board.ToArray(), room.Turn, room.Result, room.Round, room.MoveCount,
    room.Ready, room.Started, room.ResignedBy,
    skins = room.Seats.Take(2).Select(player => player?.Skin ?? "classic").ToArray(),
    room.LastMove, side, host = room.IsHost(side), room.Mode,
    outcome = room.OutcomeFor(side),
    seats = room.Seats.Select(player => player != null).ToArray()
});

record Move(int X, int Y, int Round, int MoveCount, int Side);
record Restart(int Round, int Side);
record Skin(string? Value);
record Seat(int Side);
record Mode(string Value);

sealed class Player
{
    public readonly string Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Skin = "classic";
    public DateTime LastSeen = DateTime.UtcNow;
}

sealed class Room
{
    public const int Size = 15;
    private static readonly (int X, int Y)[] Directions = [(1, 0), (0, 1), (1, 1), (1, -1)];
    public readonly Player?[] Seats = [new Player(), null, null];
    public readonly string Host;
    public readonly int[] Board = new int[Size * Size];
    public string Mode = "rotate";
    public int Turn = 1;
    public int Result;
    public int ResignedBy;
    public bool Started;
    public int Round = 1;
    public int MoveCount;
    public int? LastMove;
    private string? winner;
    private string? loser;
    public DateTime LastAction = DateTime.UtcNow;

    public Room() => Host = Seats[0]!.Token;

    public bool Ready => Seats[0] != null && Seats[1] != null;
    public bool InProgress => Started && Result == 0;
    public bool IsHost(int side) => Seats[side - 1]?.Token == Host;

    public void Swap()
    {
        (Seats[0], Seats[1]) = (Seats[1], Seats[0]);
    }

    public void Finish(int result)
    {
        Result = result;
        if (result == 3) return;
        winner = Seats[result - 1]?.Token;
        loser = Seats[2 - result]?.Token;
    }

    public string OutcomeFor(int side)
    {
        var token = Seats[side - 1]!.Token;
        if (token == winner) return "win";
        if (token == loser) return "loss";
        return "watch";
    }

    public void Reset()
    {
        Array.Clear(Board);
        Round++;
        MoveCount = 0;
        LastMove = null;
        Result = 0;
        winner = loser = null;
        ResignedBy = 0;
        Started = true;
        Turn = 1;
    }

    public bool HasExactFive(int x, int y, int side) =>
        Directions.Any(d => 1 + Count(x, y, d.X, d.Y, side) + Count(x, y, -d.X, -d.Y, side) == 5);

    private int Count(int x, int y, int dx, int dy, int side)
    {
        var count = 0;
        for (x += dx, y += dy; x >= 0 && x < Size && y >= 0 && y < Size &&
             Board[y * Size + x] == side; x += dx, y += dy)
            count++;
        return count;
    }
}
