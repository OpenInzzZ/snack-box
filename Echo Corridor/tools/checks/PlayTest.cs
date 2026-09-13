using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 自动试玩压测：三档难度各刷若干轮，用**真实按键输入**把玩家走到出口
/// （每步按 BFS 路径按下对应的 WASD / 方向键，走到目标格心才松手，偶数轮用 WASD、奇数轮用方向键）。
///
/// 检查点：难度档位确实生效、墙体数量与网格推算一致（抓重开时的墙体残留/重复）、
/// 碰撞体数量与墙段一致、起点在形状内、出口可达、按着开着的方向却走不动（卡墙）、
/// 单步耗时异常（蹭墙）、走到底会判定通关。
/// 同时统计每档难度/每种外形的耗时与声波发射开销，用于找优化点。
///
/// 注意：难度是每次重建迷宫时读取的，所以换难度只要改 GameSettings 再按重开键即可
/// （和 Agent 桥的 /reset 走同一条路径），不需要重载场景。
/// 用法：Godot_console.exe --headless --path . res://tools/checks/play_test.tscn --fixed-fps 60
/// </summary>
public partial class PlayTest : Node
{
	private static int RoundsPerDifficulty = 50;
	private const int EmitSampleCount = 100;
	private const int StepFrameBudget = 60;      // 一格最多给多少物理帧，超了算走不动
	private const int IdealFramesPerCell = 12;   // 5 格/秒、60Hz 下走一格约 12 帧
	private const string MazeScene = "res://scenes/maze.tscn";

	private static readonly MazeDifficulty[] Difficulties =
	{
		MazeDifficulty.Low,
		MazeDifficulty.Medium,
		MazeDifficulty.High,
	};

	private MazeGame _maze;
	private StaticBody2D _wallsBody;
	private int _failures;

	private readonly Dictionary<MazeShape, int> _roundsByShape = new();
	private readonly Dictionary<MazeDifficulty, int> _minWalls = new();
	private readonly Dictionary<MazeDifficulty, int> _maxWalls = new();
	private readonly Dictionary<MazeDifficulty, int> _steps = new();
	private readonly Dictionary<MazeDifficulty, int> _slowestStep = new();
	private readonly Dictionary<MazeDifficulty, float> _secondsByDifficulty = new();
	private readonly Dictionary<string, byte[]> _leaderboardBackup = new();

	public override async void _Ready()
	{
		// 地图很大时走一轮要很久，可以用 --rounds=N 少跑几轮
		foreach (string argument in OS.GetCmdlineUserArgs())
		{
			if (argument.StartsWith("--rounds=", StringComparison.Ordinal)
				&& int.TryParse(argument["--rounds=".Length..], out int rounds))
			{
				RoundsPerDifficulty = Mathf.Max(1, rounds);
			}
		}

		foreach (MazeDifficulty difficulty in Difficulties)
		{
			string path = Leaderboard.PathFor(difficulty);
			if (FileAccess.FileExists(path)) _leaderboardBackup[path] = FileAccess.GetFileAsBytes(path);
		}

		_maze = (MazeGame)GD.Load<PackedScene>(MazeScene).Instantiate();
		AddChild(_maze);
		_wallsBody = _maze.GetNode<StaticBody2D>("Walls");

		ulong started = Time.GetTicksUsec();

		try
		{
			foreach (MazeDifficulty difficulty in Difficulties)
			{
				// 难度在重建迷宫时读取，改完设置按一下重开键即可生效
				GameSettings.Difficulty = difficulty;

				ulong phaseStart = Time.GetTicksUsec();
				for (int round = 0; round < RoundsPerDifficulty; round++)
				{
					await PlayRound(difficulty, round);
				}

				_secondsByDifficulty[difficulty] = (Time.GetTicksUsec() - phaseStart) / 1_000_000f;
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[PLAY] 试玩过程中抛异常：{ex}");
			_failures++;
		}

		Report((Time.GetTicksUsec() - started) / 1_000_000f);
		RestoreLeaderboard();
		GetTree().Quit(_failures == 0 ? 0 : 1);
	}

	private async Task PlayRound(MazeDifficulty difficulty, int round)
	{
		await RestartMaze();

		MazeGrid grid = _maze.Grid;
		DifficultyProfile profile = GameSettings.Describe(difficulty);
		string label = $"{profile.Name}#{round + 1} {MazeGrid.ShapeName(grid.Shape)}";
		bool useArrows = round % 2 == 1;   // 偶数轮 WASD、奇数轮方向键，两套绑定都跑到
		_roundsByShape[grid.Shape] = _roundsByShape.GetValueOrDefault(grid.Shape) + 1;

		if (grid.Width != profile.GridWidth || grid.Height != profile.GridHeight)
			Fail($"{label}：难度档位没生效，迷宫 {grid.Width}x{grid.Height}，期望 {profile.GridWidth}x{profile.GridHeight}");

		int expected = ExpectedWallCount(grid);
		if (_maze.Walls.Count != expected)
			Fail($"{label}：墙段 {_maze.Walls.Count}，期望 {expected}（重开后是否有残留？）");

		if (_wallsBody.GetChildCount() != _maze.Walls.Count)
			Fail($"{label}：碰撞体 {_wallsBody.GetChildCount()} 个，墙段 {_maze.Walls.Count} 段");

		_minWalls[difficulty] = Mathf.Min(_minWalls.GetValueOrDefault(difficulty, int.MaxValue), _maze.Walls.Count);
		_maxWalls[difficulty] = Mathf.Max(_maxWalls.GetValueOrDefault(difficulty), _maze.Walls.Count);

		if (!grid.IsActive(grid.Start.X, grid.Start.Y))
			Fail($"{label}：起点 {grid.Start} 落在形状外");

		// 每 10 轮折腾一次窗口尺寸，覆盖重排路径（格宽变了、墙体重建、玩家要被安置到新格心）
		if (round % 10 == 9)
		{
			await BounceWindowSize();

			if (_maze.Walls.Count != ExpectedWallCount(grid))
				Fail($"{label}：缩放重排后墙段 {_maze.Walls.Count}，期望 {ExpectedWallCount(grid)}");

			if (_wallsBody.GetChildCount() != _maze.Walls.Count)
				Fail($"{label}：缩放重排后碰撞体 {_wallsBody.GetChildCount()} 个，墙段 {_maze.Walls.Count} 段");

			if (!grid.IsActive(PlayerCell().X, PlayerCell().Y))
				Fail($"{label}：缩放重排后玩家落到形状外 {PlayerCell()}");
		}

		// 真实走位：每次规划到出口的第一步，按住对应方向键走到目标格心
		Vector2I exit = grid.Exit;
		int maxSteps = grid.ActiveCount * 4 + 64;

		for (int step = 0; step < maxSteps; step++)
		{
			Vector2I cell = PlayerCell();
			if (cell == exit) break;

			if (!grid.IsActive(cell.X, cell.Y))
			{
				Fail($"{label}：第 {step} 步玩家跑到形状外 {cell}（位置 {_maze.PlayerPosition}）");
				return;
			}

			Vector2I next = NextStep(grid, cell, exit);
			if (next == cell)
			{
				Fail($"{label}：从 {cell} 找不到通往出口 {exit} 的路");
				return;
			}

			var delta = new Vector2I(next.X - cell.X, next.Y - cell.Y);
			Key key = KeyFor(delta, useArrows);

			// 每 7 步先朝一堵墙推一会儿再转向，验证贴着墙推不会把角色卡住
			if (step % 7 == 6 && !await PushIntoWall(grid, cell, useArrows))
			{
				Fail($"{label}：在 {cell} 顶墙后无法继续");
				return;
			}

			int frames = await WalkToCell(key, delta, next);

			_steps[difficulty] = _steps.GetValueOrDefault(difficulty) + 1;
			_slowestStep[difficulty] = Mathf.Max(_slowestStep.GetValueOrDefault(difficulty), frames);

			if (frames < 0)
			{
				Fail($"{label}：在 {cell} 朝 {delta} 按住 {key} 共 {StepFrameBudget} 帧仍没走到 {next}（位置 {_maze.PlayerPosition}）");
				return;
			}
		}

		if (PlayerCell() != exit)
		{
			Fail($"{label}：步数用尽仍没到出口 {exit}");
			return;
		}

		if (!_maze.Won)
		{
			Fail($"{label}：已站到出口 {exit} 但没有判定通关");
			return;
		}

		// 通关后应揭示全图：所有墙都点满，且进入揭示状态
		int dark = 0;
		foreach (MazeGame.Wall wall in _maze.Walls)
		{
			if (wall.Intensity < 1f) dark++;
		}

		if (!_maze.Revealed || dark > 0)
			Fail($"{label}：通关后未点亮全图（未点亮 {dark}/{_maze.Walls.Count} 段，揭示={_maze.Revealed}）");
	}

	/// <summary>按住方向键走到目标格心（越过格心才松手），返回耗时帧数；超预算返回 -1</summary>
	private async Task<int> WalkToCell(Key key, Vector2I delta, Vector2I targetCell)
	{
		Vector2 target = CellCenter(targetCell);
		Press(key, true);

		for (int frame = 0; frame < StepFrameBudget; frame++)
		{
			await PhysicsFrame();

			Vector2 position = _maze.PlayerPosition;
			float along = delta.X != 0 ? (position.X - target.X) * delta.X : (position.Y - target.Y) * delta.Y;
			if (along >= 0f)
			{
				Press(key, false);
				return frame + 1;   // 已经越过目标格心
			}
		}

		Press(key, false);
		return -1;
	}

	/// <summary>朝一堵相邻的墙按住方向键推若干帧，返回之后角色是否还在原来那格里</summary>
	private async Task<bool> PushIntoWall(MazeGrid grid, Vector2I cell, bool useArrows)
	{
		foreach (int dir in MazeGrid.Directions)
		{
			if (!grid.HasWall(cell.X, cell.Y, dir)) continue;

			Key key = KeyFor(MazeGrid.Offset(dir), useArrows);
			Press(key, true);

			for (int frame = 0; frame < 15; frame++) await PhysicsFrame();

			Press(key, false);
			return PlayerCell() == cell;
		}

		return true;
	}

	private async Task RestartMaze()
	{
		Press(Key.R, true);
		await PhysicsFrame();
		Press(Key.R, false);
		await PhysicsFrame();
	}

	/// <summary>反复改窗口尺寸再改回来，逼出重排路径上的问题</summary>
	private async Task BounceWindowSize()
	{
		Vector2I original = DisplayServer.WindowGetSize();

		foreach (Vector2I size in new[] { new Vector2I(880, 720), new Vector2I(1440, 900), original })
		{
			DisplayServer.WindowSetSize(size);
			await PhysicsFrame();
			await PhysicsFrame();
		}
	}

	private static void Press(Key key, bool pressed) =>
		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = pressed });

	private static Key KeyFor(Vector2I delta, bool useArrows) => delta switch
	{
		{ X: 0, Y: -1 } => useArrows ? Key.Up : Key.W,
		{ X: 0, Y: 1 } => useArrows ? Key.Down : Key.S,
		{ X: 1, Y: 0 } => useArrows ? Key.Right : Key.D,
		_ => useArrows ? Key.Left : Key.A,
	};

	/// <summary>玩家当前所在格（按格心归属计算，越界会返回形状外的坐标以便上层发现）</summary>
	private Vector2I PlayerCell()
	{
		Vector2 local = (_maze.PlayerPosition - Origin()) / _maze.CellSize;
		return new Vector2I(Mathf.FloorToInt(local.X), Mathf.FloorToInt(local.Y));
	}

	/// <summary>由出口格与出口像素位置反推网格原点</summary>
	private Vector2 Origin()
	{
		MazeGrid grid = _maze.Grid;
		float size = _maze.CellSize;
		return _maze.ExitCenter - new Vector2((grid.Exit.X + 0.5f) * size, (grid.Exit.Y + 0.5f) * size);
	}

	private Vector2 CellCenter(Vector2I cell) =>
		Origin() + new Vector2((cell.X + 0.5f) * _maze.CellSize, (cell.Y + 0.5f) * _maze.CellSize);

	/// <summary>BFS 求从 from 出发通往 to 的第一步；不可达时返回 from</summary>
	private static Vector2I NextStep(MazeGrid grid, Vector2I from, Vector2I to)
	{
		var previous = new Dictionary<Vector2I, Vector2I> { [from] = from };
		var queue = new Queue<Vector2I>();
		queue.Enqueue(from);

		while (queue.Count > 0)
		{
			Vector2I cur = queue.Dequeue();
			if (cur == to) break;

			foreach (int dir in MazeGrid.Directions)
			{
				if (!grid.IsOpen(cur.X, cur.Y, dir)) continue;

				Vector2I off = MazeGrid.Offset(dir);
				var next = new Vector2I(cur.X + off.X, cur.Y + off.Y);
				if (previous.ContainsKey(next)) continue;

				previous[next] = cur;
				queue.Enqueue(next);
			}
		}

		if (!previous.ContainsKey(to)) return from;

		Vector2I step = to;
		while (previous[step] != from) step = previous[step];
		return step;
	}

	/// <summary>按墙体生成规则从网格推算应有的墙段数（与 BuildWalls 同规则）</summary>
	private static int ExpectedWallCount(MazeGrid grid)
	{
		int count = 0;

		for (int x = 0; x < grid.Width; x++)
		{
			for (int y = 0; y < grid.Height; y++)
			{
				if (!grid.IsActive(x, y)) continue;

				if (grid.HasWall(x, y, MazeGrid.North)) count++;
				if (grid.HasWall(x, y, MazeGrid.West)) count++;
				if (!grid.IsActive(x + 1, y) && grid.HasWall(x, y, MazeGrid.East)) count++;
				if (!grid.IsActive(x, y + 1) && grid.HasWall(x, y, MazeGrid.South)) count++;
			}
		}

		return count;
	}

	private SignalAwaiter PhysicsFrame() => ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

	private void Fail(string message)
	{
		_failures++;
		GD.PrintErr($"[PLAY] 失败：{message}");
	}

	private void Report(float totalSeconds)
	{
		GD.Print("=== 自动试玩报告 ===");
		GD.Print($"[PLAY] 总计 {Difficulties.Length * RoundsPerDifficulty} 轮，用时 {totalSeconds:0.0} 秒");

		foreach (MazeDifficulty difficulty in Difficulties)
		{
			DifficultyProfile profile = GameSettings.Describe(difficulty);
			float seconds = _secondsByDifficulty.GetValueOrDefault(difficulty);
			int steps = _steps.GetValueOrDefault(difficulty);

			GD.Print($"[PLAY] {profile.Name}：{RoundsPerDifficulty} 轮 / {seconds:0.0} 秒" +
				$"（平均 {seconds * 1000f / RoundsPerDifficulty:0} ms/轮），" +
				$"迷宫 {profile.GridWidth}x{profile.GridHeight}，墙段 {_minWalls.GetValueOrDefault(difficulty)}~{_maxWalls.GetValueOrDefault(difficulty)}，" +
				$"共走 {steps} 格，最慢一格 {_slowestStep.GetValueOrDefault(difficulty)} 帧（理想 {IdealFramesPerCell} 帧）");
		}

		foreach (KeyValuePair<MazeShape, int> entry in _roundsByShape)
		{
			GD.Print($"[PLAY] 外形 {MazeGrid.ShapeName(entry.Key)}：{entry.Value} 轮");
		}

		foreach (MazeDifficulty difficulty in Difficulties)
		{
			GD.Print($"[PLAY] {GameSettings.Describe(difficulty).Name} 难度发射声波 {MeasureEmitCost(difficulty):0.00} ms/次");
		}

		GD.Print(_failures == 0
			? "[PLAY] 未发现异常"
			: $"[PLAY] 共发现 {_failures} 项异常");
	}

	/// <summary>为某档难度单独建一个迷宫实例，测它连发声波的平均开销</summary>
	private float MeasureEmitCost(MazeDifficulty difficulty)
	{
		GameSettings.Difficulty = difficulty;

		var probe = (MazeGame)GD.Load<PackedScene>(MazeScene).Instantiate();
		AddChild(probe);

		ulong started = Time.GetTicksUsec();
		for (int i = 0; i < EmitSampleCount; i++) probe.EmitWave();
		float milliseconds = (Time.GetTicksUsec() - started) / 1000f / EmitSampleCount;

		RemoveChild(probe);
		probe.QueueFree();
		return milliseconds;
	}

	private void RestoreLeaderboard()
	{
		foreach (MazeDifficulty difficulty in Difficulties)
		{
			string path = Leaderboard.PathFor(difficulty);

			if (_leaderboardBackup.TryGetValue(path, out byte[] content))
			{
				using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
				file?.StoreBuffer(content);
				continue;
			}

			if (!FileAccess.FileExists(path)) continue;

			Error error = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
			if (error != Error.Ok || FileAccess.FileExists(path))
				GD.PrintErr($"[PLAY] 排行榜残留清理失败：{path}（{error}）");
		}

		GD.Print("[PLAY] 已还原试玩前的排行榜文件");
	}
}
