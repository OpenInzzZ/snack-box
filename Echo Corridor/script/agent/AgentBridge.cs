using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// 给 AI Agent 用的桥：在 127.0.0.1 上开一个极简 HTTP 服务，把迷宫状态暴露成 JSON，
/// 并允许 Agent 按键移动、重开、换难度。只有带 <c>--agent-port=&lt;端口&gt;</c> 启动时才挂上。
///
/// 端点：
///   GET  /state        玩家能看到的信息（位置、计时、当前亮着的墙）
///   GET  /truth        全图（含出口、每格通行方向），属作弊，供 Agent 规划用
///   GET  /leaderboard  三档难度的成绩
///   POST /move         {"direction":"north|south|east|west","frames":12} 按住方向键若干物理帧
///   POST /restart      重开一张迷宫（难度不变）
///   POST /reset        {"difficulty":"low|medium|high"} 换难度并重建迷宫
///
/// Agent 侧一般通过 tools/agent/mcp-echo-corridor.js 这个 MCP 服务来调用，而不是直接发 HTTP。
/// </summary>
public partial class AgentBridge : Node
{
	private const int DefaultPort = 45871;
	private const string BindAddress = "127.0.0.1";
	private const int MaxBodyBytes = 1 << 16;

	/// <summary>命令行里请求的端口；0 表示没开 Agent 模式</summary>
	public static int RequestedPort { get; private set; }

	/// <summary>把命令行参数（--difficulty=、--agent-port=）应用到全局设置，供迷宫 _Ready 之前调用</summary>
	public static void ApplyStartupArgs()
	{
		RequestedPort = 0;

		foreach (string argument in OS.GetCmdlineUserArgs())
		{
			if (argument.StartsWith("--difficulty=", StringComparison.Ordinal))
			{
				GameSettings.Difficulty = ParseDifficulty(argument["--difficulty=".Length..]);
			}
			else if (argument.StartsWith("--agent-port=", StringComparison.Ordinal))
			{
				RequestedPort = int.TryParse(argument["--agent-port=".Length..], out int port) ? port : DefaultPort;
			}
			else if (argument.StartsWith("--seed=", StringComparison.Ordinal))
			{
				GameSettings.Seed = int.TryParse(argument["--seed=".Length..], out int seed)
					? Mathf.Clamp(seed, 0, GameSettings.MaxSeed)
					: 0;
			}
			else if (argument == "--agent")
			{
				GameSettings.AgentPlays = true;   // 直接以 Agent 模式模式开局（自检 / 命令行用）
			}
		}
	}

	private sealed class Connection
	{
		public StreamPeerTcp Peer;
		public readonly List<byte> Buffer = new();
		public bool Busy;
	}

	private readonly List<Connection> _connections = new();
	private TcpServer _server;
	private int _moves;

	public override void _Ready()
	{
		int port = RequestedPort > 0 ? RequestedPort : DefaultPort;

		_server = new TcpServer();
		Error error = _server.Listen((ushort)port, BindAddress);
		if (error != Error.Ok)
		{
			GD.PrintErr($"[AGENT] 无法监听 {BindAddress}:{port}（{error}）");
			return;
		}

		GD.Print($"[AGENT] Agent 桥已开启：http://{BindAddress}:{port}/state" +
			$"（难度 {GameSettings.DisplayName(GameSettings.Difficulty)}）");
	}

	public override void _Process(double delta)
	{
		if (_server == null) return;

		while (_server.IsConnectionAvailable()) Accept(_server.TakeConnection());

		foreach (Connection connection in _connections.ToArray()) Pump(connection);
	}

	private void Accept(StreamPeerTcp peer)
	{
		if (peer == null) return;

		peer.SetNoDelay(true);
		_connections.Add(new Connection { Peer = peer });
	}

	private void Pump(Connection connection)
	{
		if (connection.Busy) return;

		// StreamPeerTcp 必须先 Poll 才会更新连接状态与可读字节数
		connection.Peer.Poll();

		StreamPeerTcp.Status status = connection.Peer.GetStatus();
		if (status == StreamPeerTcp.Status.Error || status == StreamPeerTcp.Status.None)
		{
			Drop(connection);
			return;
		}

		if (status != StreamPeerTcp.Status.Connected) return;   // 还在握手，下一帧再看

		int available = connection.Peer.GetAvailableBytes();
		if (available > 0)
		{
			byte[] chunk = Read(connection.Peer, available);
			if (chunk.Length == 0)
			{
				Drop(connection);
				return;
			}

			connection.Buffer.AddRange(chunk);
		}

		if (!TryReadRequest(connection, out string method, out string path, out string body))
		{
			if (connection.Buffer.Count > MaxBodyBytes) Drop(connection);
			return;
		}

		connection.Busy = true;
		GD.Print($"[AGENT] {method} {path}");
		_ = Respond(connection, method, path, body);
	}

	/// <summary>尝试从缓冲区里解析出一条完整请求（只支持 Content-Length 定长体）</summary>
	private static bool TryReadRequest(Connection connection, out string method, out string path, out string body)
	{
		method = null;
		path = null;
		body = "";

		byte[] raw = connection.Buffer.ToArray();
		int headerEnd = FindHeaderEnd(raw);
		if (headerEnd < 0) return false;

		string header = Encoding.ASCII.GetString(raw, 0, headerEnd);
		string[] lines = header.Split("\r\n");
		string[] requestLine = lines[0].Split(' ');
		if (requestLine.Length < 2) return false;

		method = requestLine[0].ToUpperInvariant();
		path = requestLine[1];

		int contentLength = 0;
		foreach (string line in lines)
		{
			if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
			int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
		}

		int bodyStart = headerEnd + 4;
		if (raw.Length - bodyStart < contentLength) return false;

		body = Encoding.UTF8.GetString(raw, bodyStart, contentLength);
		return true;
	}

	private static int FindHeaderEnd(byte[] raw)
	{
		for (int i = 0; i + 3 < raw.Length; i++)
		{
			if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n') return i;
		}

		return -1;
	}

	/// <summary>
	/// 读取一段数据。注意：Godot 4.7 的 C# 绑定里 StreamPeer.GetData 返回的是
	/// [错误码, PackedByteArray] 两项，而不是字节数组本身。
	/// </summary>
	private static byte[] Read(StreamPeerTcp peer, int bytes)
	{
		Godot.Collections.Array result = peer.GetData(bytes);
		var error = result.Count > 0 ? (Error)result[0].AsInt32() : Error.Failed;
		byte[] data = result.Count > 1 ? result[1].AsByteArray() : Array.Empty<byte>();

		return error == Error.Ok ? data : Array.Empty<byte>();
	}

	private async Task Respond(Connection connection, string method, string path, string body)
	{
		string json;

		try
		{
			json = await Dispatch(method, path, body);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[AGENT] 处理 {method} {path} 出错：{ex.Message}");
			json = Json.Stringify(new Godot.Collections.Dictionary { { "error", ex.Message } });
		}

		Write(connection, json);
		Drop(connection);
	}

	private static void Write(Connection connection, string json)
	{
		byte[] payload = Encoding.UTF8.GetBytes(json);
		string header = "HTTP/1.1 200 OK\r\n" +
			"Content-Type: application/json; charset=utf-8\r\n" +
			$"Content-Length: {payload.Length}\r\n" +
			"Connection: close\r\n\r\n";

		byte[] head = Encoding.ASCII.GetBytes(header);
		if (connection.Peer.PutData(head) != Error.Ok) return;

		int offset = 0;
		while (offset < payload.Length)
		{
			// PutPartialData 返回 [错误码, 已发送字节数]
			Godot.Collections.Array result = connection.Peer.PutPartialData(offset == 0 ? payload : payload[offset..]);
			int written = result.Count > 1 ? result[1].AsInt32() : 0;
			if (written <= 0) return;
			offset += written;
		}
	}

	private void Drop(Connection connection)
	{
		_connections.Remove(connection);
		if (connection.Peer.GetStatus() == StreamPeerTcp.Status.Connected) connection.Peer.DisconnectFromHost();
	}

	private async Task<string> Dispatch(string method, string path, string body)
	{
		var maze = GetParent<MazeGame>();
		if (maze == null) return ErrorJson("迷宫场景已不在");

		switch (path)
		{
			case "/state":
				return StateJson(maze, includeTruth: false);

			case "/truth":
				return StateJson(maze, includeTruth: true);

			case "/leaderboard":
				return LeaderboardJson();

			case "/move":
				return await Move(maze, body);

			case "/restart":
				await PressRestart();
				_moves = 0;
				return StateJson(maze, includeTruth: false);

			case "/reset":
				Godot.Collections.Dictionary change = ParseBody(body);
				if (change.TryGetValue("difficulty", out Variant level)) GameSettings.Difficulty = ParseDifficulty(level.AsString());
				if (change.TryGetValue("seed", out Variant wanted)) GameSettings.Seed = Mathf.Clamp(wanted.AsInt32(), 0, GameSettings.MaxSeed);

				// 难度与种子都在重建迷宫时读取，按一下重开键即可生效，不用重载场景
				await PressRestart();
				_moves = 0;
				return StateJson(maze, includeTruth: false);

			default:
				return ErrorJson($"未知路径 {path}，可用：/state /truth /leaderboard /move /restart /reset");
		}
	}

	/// <summary>
	/// 移动。默认"走到相邻格心就停"（这样转急弯不会卡在格边缘）；
	/// 也可以给 frames 精确控制按住多少物理帧。
	/// </summary>
	private async Task<string> Move(MazeGame maze, string body)
	{
		Godot.Collections.Dictionary request = ParseBody(body);
		string direction = request.TryGetValue("direction", out Variant value) ? value.AsString() : "";

		Key key = KeyFor(direction);
		if (key == Key.None)
			return ErrorJson($"direction 需要是 north/south/east/west（可用 n/s/up/down/left/right），收到「{direction}」");

		if (request.TryGetValue("frames", out Variant count))
		{
			await PressFor(key, Mathf.Clamp(count.AsInt32(), 1, 600));
		}
		else
		{
			await WalkToNextCenter(maze, direction, key);
		}

		// 等这一趟脚步声激起的声波散完再回报，否则 Agent 看到的是"什么都还没亮"
		await WaitForWavesToSettle(maze);

		_moves++;
		return StateJson(maze, includeTruth: false);
	}

	private async Task PressFor(Key key, int frames)
	{
		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = true });
		for (int frame = 0; frame < frames; frame++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = false });
	}

	/// <summary>按住方向键直到越过相邻格心（撞墙则最多等 1.5 秒后放弃）</summary>
	private async Task WalkToNextCenter(MazeGame maze, string direction, Key key)
	{
		Vector2I delta = DeltaFor(direction);
		Vector2 target = maze.CellCenter(maze.CellAt(maze.PlayerPosition) + delta);

		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = true });

		for (int frame = 0; frame < 90; frame++)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

			Vector2 position = maze.PlayerPosition;
			float along = delta.X != 0 ? (position.X - target.X) * delta.X : (position.Y - target.Y) * delta.Y;
			if (along >= 0f) break;
		}

		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = false });
	}

	private static Vector2I DeltaFor(string direction) => direction.Trim().ToLowerInvariant() switch
	{
		"north" or "n" or "up" => new Vector2I(0, -1),
		"south" or "s" or "down" => new Vector2I(0, 1),
		"east" or "right" => new Vector2I(1, 0),
		_ => new Vector2I(-1, 0),
	};

	/// <summary>等待当前所有声波扩散结束（最多 1.5 秒，避免卡住）</summary>
	private async Task WaitForWavesToSettle(MazeGame maze)
	{
		for (int frame = 0; frame < 90 && maze.Waves.Count > 0; frame++)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		}
	}

	/// <summary>按一下重开键（迷宫在 _PhysicsProcess 里轮询 R）</summary>
	private async Task PressRestart()
	{
		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.R, Pressed = true });
		await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.R, Pressed = false });
		await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
	}

	private string StateJson(MazeGame maze, bool includeTruth)
	{
		MazeGrid grid = maze.Grid;
		Vector2 position = maze.PlayerPosition;
		Vector2I cell = CellOf(maze, position);

		var visible = new Godot.Collections.Array();
		foreach (MazeGame.Wall wall in maze.Walls)
		{
			if (wall.Intensity <= 0.01f) continue;

			visible.Add(new Godot.Collections.Dictionary
			{
				{ "a", Point(wall.A) },
				{ "b", Point(wall.B) },
				{ "intensity", Mathf.Snapped(wall.Intensity, 0.01f) },
			});
		}

		var state = new Godot.Collections.Dictionary
		{
			{ "difficulty", GameSettings.DisplayName(GameSettings.Difficulty) },
			{ "shape", MazeGrid.ShapeName(grid.Shape) },
			{ "seed", maze.Seed },
			{ "zoom", Mathf.Snapped(maze.Zoom, 0.01f) },
			{ "grid", new Godot.Collections.Dictionary
				{
					{ "width", grid.Width },
					{ "height", grid.Height },
					{ "cells_in_shape", grid.ActiveCount },
					{ "cell_size", Mathf.Snapped(maze.CellSize, 0.01f) },
				} },
			{ "player", new Godot.Collections.Dictionary
				{
					{ "position", Point(position) },
					{ "cell", new Godot.Collections.Array { cell.X, cell.Y } },
				} },
			{ "won", maze.Won },
			{ "revealed", maze.Revealed },
			{ "elapsed_seconds", Mathf.Snapped(maze.ElapsedSeconds, 0.01f) },
			{ "moves", _moves },
			{ "walls_total", maze.Walls.Count },
			{ "visible_walls", visible },
		};

		if (!includeTruth) return Json.Stringify(state);

		state["note"] = "这是全图信息，按规则算作弊；只想凭声波摸索的话请用 /state。";
		state["start_cell"] = new Godot.Collections.Array { grid.Start.X, grid.Start.Y };
		state["exit_cell"] = new Godot.Collections.Array { grid.Exit.X, grid.Exit.Y };

		var cells = new Godot.Collections.Array();
		for (int y = 0; y < grid.Height; y++)
		{
			var row = new StringBuilder();
			for (int x = 0; x < grid.Width; x++) row.Append(MaskChar(grid, x, y));
			cells.Add(row.ToString());
		}

		state["cells"] = cells;
		state["cells_encoding"] = "每格一个十六进制字符，表示该格可通行的方向：bit0=北 bit1=东 bit2=南 bit3=西；'-' 表示该格在形状外";
		state["map_ascii"] = MapAscii(grid, cell);
		return Json.Stringify(state);
	}

	private static string MaskChar(MazeGrid grid, int x, int y)
	{
		if (!grid.IsActive(x, y)) return "-";

		int mask = 0;
		if (grid.IsOpen(x, y, MazeGrid.North)) mask |= 1;
		if (grid.IsOpen(x, y, MazeGrid.East)) mask |= 2;
		if (grid.IsOpen(x, y, MazeGrid.South)) mask |= 4;
		if (grid.IsOpen(x, y, MazeGrid.West)) mask |= 8;
		return "0123456789abcdef"[mask].ToString();
	}

	/// <summary>把迷宫画成文字图，标出起点 S、出口 E、玩家当前位置 P</summary>
	private static string MapAscii(MazeGrid grid, Vector2I player)
	{
		var lines = new List<string>();

		for (int y = 0; y < grid.Height; y++)
		{
			var top = new StringBuilder();
			var middle = new StringBuilder();

			for (int x = 0; x < grid.Width; x++)
			{
				if (!grid.IsActive(x, y))
				{
					top.Append("    ");
					middle.Append("    ");
					continue;
				}

				top.Append(grid.HasWall(x, y, MazeGrid.North) ? "+---" : "+   ");
				middle.Append(grid.HasWall(x, y, MazeGrid.West) ? "|" : " ");

				var cell = new Vector2I(x, y);
				if (cell == player) middle.Append(" P ");
				else if (cell == grid.Start) middle.Append(" S ");
				else if (cell == grid.Exit) middle.Append(" E ");
				else middle.Append("   ");
			}

			top.Append('+');
			middle.Append(grid.HasWall(grid.Width - 1, y, MazeGrid.East) ? "|" : " ");
			lines.Add(top.ToString());
			lines.Add(middle.ToString());
		}

		var bottom = new StringBuilder();
		for (int x = 0; x < grid.Width; x++) bottom.Append(grid.HasWall(x, grid.Height - 1, MazeGrid.South) ? "+---" : "+   ");
		bottom.Append('+');
		lines.Add(bottom.ToString());

		return string.Join("\n", lines);
	}

	private static string LeaderboardJson()
	{
		var boards = new Godot.Collections.Dictionary();

		foreach (MazeDifficulty difficulty in new[] { MazeDifficulty.Low, MazeDifficulty.Medium, MazeDifficulty.High })
		{
			var entries = new Godot.Collections.Array();
			foreach (float seconds in Leaderboard.Load(difficulty)) entries.Add(Mathf.Snapped(seconds, 0.01f));
			boards[GameSettings.DisplayName(difficulty)] = entries;
		}

		return Json.Stringify(boards);
	}

	private static Godot.Collections.Dictionary ParseBody(string body)
	{
		Variant parsed = Json.ParseString(body);
		return parsed.VariantType == Variant.Type.Dictionary ? parsed.AsGodotDictionary() : new Godot.Collections.Dictionary();
	}

	private static Godot.Collections.Array Point(Vector2 value) => new() { value.X, value.Y };

	private static string ErrorJson(string message) =>
		Json.Stringify(new Godot.Collections.Dictionary { { "error", message } });

	private static string AckJson(string message) =>
		Json.Stringify(new Godot.Collections.Dictionary { { "ok", true }, { "message", message } });

	/// <summary>玩家当前所在格</summary>
	private static Vector2I CellOf(MazeGame maze, Vector2 position) => maze.CellAt(position);

	private static Key KeyFor(string direction) => direction.Trim().ToLowerInvariant() switch
	{
		"north" or "n" or "up" => Key.W,
		"south" or "s" or "down" => Key.S,
		"east" or "right" => Key.D,
		"west" or "left" => Key.A,
		_ => Key.None,
	};

	private static MazeDifficulty ParseDifficulty(string name) => name.Trim().ToLowerInvariant() switch
	{
		"low" or "低" => MazeDifficulty.Low,
		"high" or "高" => MazeDifficulty.High,
		_ => MazeDifficulty.Medium,
	};
}
