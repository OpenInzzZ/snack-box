using Godot;
using System.Collections.Generic;

/// <summary>
/// 开发用自检 / 截图工具，不参与游戏逻辑
/// headless 模式：校验迷宫结构与输入映射，并检查声波点亮 / 衰减是否生效
/// 窗口模式：额外按帧截图到 tools/out/
/// 用法：
///   headless 自检：Godot_console.exe --headless --path . res://tools/checks/maze_shot.tscn --fixed-fps 60
///   截图：        Godot_console.exe --path . res://tools/checks/maze_shot.tscn --fixed-fps 60 --disable-vsync
/// </summary>
public partial class MazeShot : Node
{
	private static readonly MazeDifficulty[] Difficulties =
	{
		MazeDifficulty.Low,
		MazeDifficulty.Medium,
		MazeDifficulty.High,
	};

	private const string SettingsPath = "user://settings.cfg";

	private MazeGame _maze;
	private bool _headless;
	private int _frame;
	private int _shots;
	private readonly Dictionary<string, byte[]> _leaderboardBackup = new();
	private bool _hadSettings;
	private byte[] _settingsBackup;

	public override void _Ready()
	{
		_headless = DisplayServer.GetName() == "headless";

		// 自检会模拟通关、按 F11（都会写用户数据），先按字节存下来，退出前还原
		foreach (MazeDifficulty difficulty in Difficulties)
		{
			string path = Leaderboard.PathFor(difficulty);
			if (FileAccess.FileExists(path)) _leaderboardBackup[path] = FileAccess.GetFileAsBytes(path);
		}

		_hadSettings = FileAccess.FileExists(SettingsPath);
		if (_hadSettings) _settingsBackup = FileAccess.GetFileAsBytes(SettingsPath);

		CheckMazeGrid();
		CheckSeedReproducibility();
		DumpShapeGallery();
		CheckInputMap();

		_maze = (MazeGame)GD.Load<PackedScene>("res://scenes/maze.tscn").Instantiate();
		AddChild(_maze);

		GD.Print($"[SELFCHECK] 显示驱动={DisplayServer.GetName()}，难度 {GameSettings.DisplayName(GameSettings.Difficulty)}，" +
			$"外形 {MazeGrid.ShapeName(_maze.Grid.Shape)}，迷宫格 {_maze.Grid.Width}x{_maze.Grid.Height}（{_maze.Grid.ActiveCount} 格）");
		DumpMaze();
	}

	/// <summary>把生成的迷宫与出口打成 ASCII 图，确认出口确实在最远端</summary>
	private void DumpMaze()
	{
		MazeGrid grid = _maze.Grid;
		Vector2I exit = grid.Exit;
		float exitDistance = Mathf.Round(_maze.ExitCenter.DistanceTo(_maze.PlayerPosition) / _maze.CellSize);

		GD.Print($"[SELFCHECK] 起点 {grid.Start}，出口 {exit}，与玩家距离约 {exitDistance} 格");

		var lines = new List<string>();
		for (int y = 0; y < grid.Height; y++)
		{
			string top = "";
			string middle = "";
			for (int x = 0; x < grid.Width; x++)
			{
				if (!grid.IsActive(x, y))
				{
					top += "    ";      // 形状外的格子留空，方便看出外形轮廓
					middle += "    ";
					continue;
				}

				top += grid.HasWall(x, y, MazeGrid.North) ? "+---" : "+   ";
				middle += grid.HasWall(x, y, MazeGrid.West) ? "|" : " ";

				if (grid.Start == new Vector2I(x, y)) middle += " S ";
				else if (exit == new Vector2I(x, y)) middle += " E ";
				else middle += "   ";
			}

			lines.Add(top + "+");
			lines.Add(middle + (grid.HasWall(grid.Width - 1, y, MazeGrid.East) ? "|" : " "));
		}

		string bottom = "";
		for (int x = 0; x < grid.Width; x++)
		{
			bottom += grid.HasWall(x, grid.Height - 1, MazeGrid.South) ? "+---" : "+   ";
		}

		lines.Add(bottom + "+");

		foreach (string line in lines) GD.Print($"[MAZE] {line}");
	}

	public override void _Process(double delta)
	{
		_frame++;
		switch (_frame)
		{
			case 12:
				Shoot("01_wave.png");
				break;
			case 30:
				// 开局声波此时刚好扩散完（11 格/秒 × 5 格 ≈ 27 帧），且玩家尚未移动，
				// 所以亮着的墙必然来自玩家当前位置发出的那一圈波，可直接校验遮挡
				ReportLit("开局声波扩散完");
				ReportTimer("移动前");
				CheckNoSeeThrough();
				break;
			case 36:
				Press(Key.D, true);
				Press(Key.S, true);
				break;
			case 66:
				Shoot("02_moving.png");
				ReportLit("持续移动中（不断有新声波点亮墙面）");
				ReportTimer("移动约 0.5 秒后");
				break;
			case 72:
				Press(Key.D, false);
				Press(Key.S, false);
				break;
			case 140:
				Shoot("03_fading.png");
				break;
			case 150:
				// 滚轮连滚三次：默认 1.0 → 1/1.15³ ≈ 0.66
				for (int i = 0; i < 3; i++)
					Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true });
				break;
			case 200:
				ReportZoom("滚轮缩小三次后");
				break;
			case 210:
				Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.F11, Pressed = true });
				break;
			case 240:
				if (DisplaySettings.IsWindowed) GD.PrintErr("[SELFCHECK] 迷宫内按 F11 没有切到全屏");
				else GD.Print("[SELFCHECK] 迷宫内 F11：已切到全屏");

				Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.F11, Pressed = true });
				break;
			case 250:
				if (!DisplaySettings.IsWindowed) GD.PrintErr("[SELFCHECK] 再按 F11 没有切回窗口");
				else GD.Print("[SELFCHECK] 迷宫内 F11：已切回窗口");
				break;
			case 260:
				Shoot("04_dark.png");
				ReportLit("停止移动 3 秒后（墙面应已全部回到黑色）");
				ReportTimer("停止移动后");
				break;
			case 268:
				MeasureEmitCost();
				break;
			case 272:
				CheckLeaderboard();
				break;
			case 276:
				// 直接把玩家挪到出口，验证通关结算与排行榜展示
				_maze.GetNode<CharacterBody2D>("Player").Position = _maze.ExitCenter;
				break;
			case 283:
				Shoot("05_won_full.png");
				break;
			case 290:
				ReportWin();
				RestoreUserData();
				break;
			case 300:
				if (!_headless) GD.Print($"[SHOT] 共输出 {_shots} 张截图");
				GetTree().Quit();
				break;
		}
	}

	/// <summary>
	/// 独立校验遮挡：每段亮着的墙，都必须存在一条从玩家到它表面的、不穿过任何其它墙内部的直线。
	/// 做法是在墙体矩形上铺样点（沿中心线排开、再向两侧墙面偏移），逐像素沿线检查——
	/// 刻意不复用游戏的 slab 裁剪逻辑，作为交叉验证。
	/// </summary>
	private void CheckNoSeeThrough()
	{
		Vector2 from = _maze.PlayerPosition;
		IReadOnlyList<MazeGame.Wall> walls = _maze.Walls;
		int lit = 0;
		int violations = 0;

		for (int i = 0; i < walls.Count; i++)
		{
			if (walls[i].Intensity <= 0.01f) continue;
			lit++;
			if (HasClearPath(from, walls, i)) continue;

			violations++;
			GD.PrintErr($"[SELFCHECK] 穿墙：墙#{i} A={walls[i].A} B={walls[i].B} Rect={walls[i].Rect} " +
				$"玩家={from} 出口={_maze.Grid.Exit}");
		}

		GD.Print(violations == 0
			? $"[SELFCHECK] 遮挡校验：亮墙 {lit} 段，无穿墙"
			: $"[SELFCHECK] 遮挡校验：亮墙 {lit} 段，穿墙 {violations} 段");
	}

	/// <summary>墙面上是否存在一个取样点，与玩家之间是畅通直线</summary>
	private static bool HasClearPath(Vector2 from, IReadOnlyList<MazeGame.Wall> walls, int target)
	{
		Rect2 rect = walls[target].Rect;
		float half = walls[target].A.DistanceTo(walls[target].B) <= 0f ? 0f : rect.Size.Y * 0.5f;
		Vector2 axis = (walls[target].B - walls[target].A).Normalized();
		Vector2 normal = new(-axis.Y, axis.X);

		// 取样要够密：贴角时墙上"看得见"的窗口可能只有一两像素宽，稀疏取样会误报
		const int AlongSamples = 40;
		float[] offsets = { 0f, half - 0.05f, -(half - 0.05f) };

		for (int s = 0; s <= AlongSamples; s++)
		{
			Vector2 center = walls[target].A.Lerp(walls[target].B, (float)s / AlongSamples);
			foreach (float offset in offsets)
			{
				Vector2 point = center + normal * offset;
				if (IsStrictlyInsideOtherWall(walls, target, point)) continue;
				if (IsLineClear(from, point, walls, target)) return true;
			}
		}

		return false;
	}

	/// <summary>点是否落在别的墙内部（贴边不算，避免把共面拼接处误判成穿墙）</summary>
	private static bool IsStrictlyInsideOtherWall(IReadOnlyList<MazeGame.Wall> walls, int target, Vector2 point)
	{
		for (int j = 0; j < walls.Count; j++)
		{
			if (j != target && walls[j].Rect.Grow(-0.05f).HasPoint(point)) return true;
		}

		return false;
	}

	private static bool IsLineClear(Vector2 from, Vector2 to, IReadOnlyList<MazeGame.Wall> walls, int target)
	{
		float length = from.DistanceTo(to);
		Vector2 direction = length <= 0f ? Vector2.Zero : (to - from) / length;

		for (float travelled = 0f; travelled <= length; travelled += 1f)
		{
			Vector2 point = from + direction * travelled;
			if (IsStrictlyInsideOtherWall(walls, target, point)) return false;
		}

		return true;
	}

	/// <summary>读取界面上的计时标签，验证计时确实是从第一次移动才开始</summary>
	private void ReportTimer(string label)
	{
		GD.Print($"[SELFCHECK] {label}计时：{_maze.GetNode<Label>("UI/TimerLabel").Text}");
	}

	/// <summary>报告当前相机缩放，附带视口与格宽，便于核对"装下全图"的比例</summary>
	private void ReportZoom(string label)
	{
		Vector2I viewport = (Vector2I)GetViewport().GetVisibleRect().Size;
		GD.Print($"[SELFCHECK] {label}缩放：{_maze.Zoom:0.00}（视口 {viewport.X}x{viewport.Y}，格宽 {_maze.CellSize:0}，" +
			$"迷宫 {_maze.Grid.Width * _maze.CellSize:0}x{_maze.Grid.Height * _maze.CellSize:0}）");
	}

	/// <summary>发射一圈声波要做 O(候选墙²) 的视线判定，测一下别让玩家感到卡顿</summary>
	private void MeasureEmitCost()
	{
		const int Times = 200;
		ulong started = Time.GetTicksUsec();
		for (int i = 0; i < Times; i++) _maze.EmitWave();
		ulong elapsed = Time.GetTicksUsec() - started;

		GD.Print($"[SELFCHECK] 发射一圈声波平均耗时 {elapsed / 1000.0 / Times:0.00} ms（{Times} 次）");
	}

	/// <summary>排行榜逻辑自检：排序、截断、名次、落盘重载（用临时文件，不碰真实成绩）</summary>
	private static void CheckLeaderboard()
	{
		const string path = "user://leaderboard_selftest.txt";
		RemoveFile(path);

		for (int i = 0; i < 12; i++) Leaderboard.Record(10f + i, path);   // 写入 10~21 秒，故意超出容量
		List<float> kept = Leaderboard.LoadFrom(path);
		bool ok = kept.Count == Leaderboard.MaxEntries && IsSorted(kept) && Mathf.IsEqualApprox(kept[0], 10f);

		int fastRank = Leaderboard.Record(5f, path);    // 比所有成绩都快
		int slowRank = Leaderboard.Record(99f, path);   // 比所有成绩都慢，应进不了榜
		List<float> after = Leaderboard.LoadFrom(path);
		ok = ok && fastRank == 1 && slowRank == 0 && Mathf.IsEqualApprox(after[0], 5f);

		// 落盘的必须是密文：用普通方式打开不应该能读出一行行成绩
		bool plainReadable = false;
		using (FileAccess plain = FileAccess.Open(path, FileAccess.ModeFlags.Read))
		{
			if (plain != null)
			{
				foreach (string line in plain.GetAsText().Split('\n'))
				{
					if (float.TryParse(line.Trim(), out float _))
					{
						plainReadable = true;
						break;
					}
				}
			}
		}

		ok = ok && !plainReadable;

		GD.Print(ok
			? $"[SELFCHECK] 排行榜：只留最快 {kept.Count} 条、已排序、名次与落盘均正确，且文件为密文"
			: $"[SELFCHECK] 排行榜异常：count={kept.Count} first={kept[0]:0.00} fast={fastRank} " +
				$"slow={slowRank} 明文可读={plainReadable}");

		RemoveFile(path);
	}

	/// <summary>把玩家挪到出口后，检查通关提示、排行榜与全图揭示</summary>
	private void ReportWin()
	{
		var win = _maze.GetNode<Label>("UI/WinLabel");
		var board = _maze.GetNode<RichTextLabel>("UI/LeaderboardLabel");

		int dark = 0;
		foreach (MazeGame.Wall wall in _maze.Walls)
		{
			if (wall.Intensity < 1f) dark++;
		}

		GD.Print($"[SELFCHECK] 通关提示 可见={win.Visible}：{win.Text}");
		GD.Print($"[SELFCHECK] 排行榜 可见={board.Visible}：{board.GetParsedText().Replace("\n", " ｜ ")}");
		GD.Print($"[SELFCHECK] 通关揭示：{_maze.Walls.Count} 段墙中未点亮 {dark} 段，揭示状态={_maze.Revealed}");
		ReportZoom("通关后（应缩到装下全图）");
	}

	private static bool IsSorted(List<float> values)
	{
		for (int i = 1; i < values.Count; i++)
		{
			if (values[i] < values[i - 1]) return false;
		}

		return true;
	}

	private static void RemoveFile(string path)
	{
		if (!FileAccess.FileExists(path)) return;

		Error error = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
		if (error != Error.Ok || FileAccess.FileExists(path))
			GD.PrintErr($"[SELFCHECK] 排行榜残留清理失败：{path}（{error}）");
	}

	/// <summary>自检会把真实排行榜当作通关结果写一次、按 F11 改显示设置，退出前还原回去</summary>
	private void RestoreUserData()
	{
		RestoreLeaderboard();

		if (_hadSettings)
		{
			using FileAccess file = FileAccess.Open(SettingsPath, FileAccess.ModeFlags.Write);
			file?.StoreBuffer(_settingsBackup);
			return;
		}

		if (!FileAccess.FileExists(SettingsPath)) return;

		Error error = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(SettingsPath));
		if (error != Error.Ok)
			GD.PrintErr($"[SELFCHECK] 分辨率配置清理失败：{SettingsPath}（{error}）");
	}

	/// <summary>自检会把真实排行榜当作通关结果写一次，退出前还原回去</summary>
	private void RestoreLeaderboard()
	{
		int restored = 0;

		foreach (MazeDifficulty difficulty in Difficulties)
		{
			string path = Leaderboard.PathFor(difficulty);

			if (!_leaderboardBackup.TryGetValue(path, out byte[] content))
			{
				RemoveFile(path);
				continue;
			}

			using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
			file?.StoreBuffer(content);
			restored++;
		}

		GD.Print($"[SELFCHECK] 已还原自检前的 {restored} 个排行榜文件");
	}

	private void ReportLit(string label)
	{
		int lit = 0;
		foreach (MazeGame.Wall wall in _maze.Walls)
		{
			if (wall.Intensity > 0.01f) lit++;
		}

		GD.Print($"[SELFCHECK] {label}：亮墙 {lit}/{_maze.Walls.Count}，声波 {_maze.Waves.Count} 圈，" +
			$"玩家格 {PlayerCell()}，出口格 {_maze.Grid.Exit}");
	}

	/// <summary>由出口格与出口像素位置反推网格原点，再换算玩家所在格</summary>
	private Vector2I PlayerCell()
	{
		MazeGrid grid = _maze.Grid;
		Vector2 origin = _maze.ExitCenter - new Vector2((grid.Exit.X + 0.5f) * _maze.CellSize, (grid.Exit.Y + 0.5f) * _maze.CellSize);
		Vector2 local = (_maze.PlayerPosition - origin) / _maze.CellSize;
		return new Vector2I(Mathf.FloorToInt(local.X), Mathf.FloorToInt(local.Y));
	}

	private static void Press(Key key, bool pressed)
	{
		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = pressed });
	}

	private async void Shoot(string name)
	{
		if (_headless) return;

		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = GetViewport().GetTexture().GetImage();
		image.SavePng($"res://tools/out/{name}");
		_shots++;
		GD.Print($"[SHOT] {name}");

		if (name == "02_moving.png") DumpFrame(image, _maze.PlayerPosition, 72f, 4f);
	}

	/// <summary>把渲染结果在指定区域内降采样成 ASCII，用于核对画面上究竟画了什么</summary>
	private static void DumpFrame(Image image, Vector2 center, float halfSize, float step)
	{
		var line = new System.Text.StringBuilder();
		int count = (int)(halfSize * 2f / step);

		GD.Print($"[FRAME] 以玩家为中心 {halfSize * 2f:0}px 区域，每字符 {step:0}px（#=亮 +=灰 .=黑）");
		for (int row = 0; row < count; row++)
		{
			line.Clear();
			for (int col = 0; col < count; col++)
			{
				int x = Mathf.Clamp((int)(center.X - halfSize + col * step), 0, image.GetWidth() - 1);
				int y = Mathf.Clamp((int)(center.Y - halfSize + row * step), 0, image.GetHeight() - 1);
				float luminance = image.GetPixel(x, y).R;
				line.Append(luminance > 0.75f ? '#' : luminance > 0.25f ? '+' : '.');
			}
			GD.Print($"[FRAME] {line}");
		}
	}

	/// <summary>批量校验：每档难度 × 每种外形各跑一批种子，检查形状内全格可达、出口即最远格、通路数 = 格数-1</summary>
	private static void CheckMazeGrid()
	{
		int failures = 0;
		int cases = 0;

		foreach (MazeDifficulty difficulty in Difficulties)
		{
			DifficultyProfile profile = GameSettings.Describe(difficulty);

			foreach (MazeShape shape in MazeGrid.AllShapes)
			{
				for (int seed = 0; seed < 12; seed++)
				{
					cases++;
					var grid = new MazeGrid(profile.GridWidth, profile.GridHeight, shape, seed);
					failures += CheckOneMaze(grid, $"{profile.Name} 难度 / {MazeGrid.ShapeName(shape)} / seed {seed}");
				}
			}
		}

		GD.Print(failures == 0
			? $"[SELFCHECK] 迷宫结构：{Difficulties.Length} 档难度 × {MazeGrid.AllShapes.Length} 种外形共 {cases} 组全部通过"
			: $"[SELFCHECK] 迷宫结构：失败 {failures} 项");
	}

	/// <summary>把每种外形的格子分布打成文字图，确认裁形符合预期</summary>
	private static void DumpShapeGallery()
	{
		const int size = 15;
		GD.Print($"[SHAPE] {size}x{size} 网格下各外形的范围（# = 形状内，. = 裁掉）：");

		foreach (MazeShape shape in MazeGrid.AllShapes)
		{
			var grid = new MazeGrid(size, size, shape, 0);
			GD.Print($"[SHAPE] {MazeGrid.ShapeName(shape)}：{grid.ActiveCount} 格");

			for (int y = 0; y < grid.Height; y++)
			{
				string row = "";
				for (int x = 0; x < grid.Width; x++) row += grid.IsActive(x, y) ? "#" : ".";
				GD.Print($"[SHAPE] {row}");
			}
		}
	}

	private static int CheckOneMaze(MazeGrid grid, string label)
	{
		int failures = 0;
		var distance = new Dictionary<Vector2I, int> { [grid.Start] = 0 };
		var queue = new Queue<Vector2I>();
		queue.Enqueue(grid.Start);
		int farthest = 0;

		while (queue.Count > 0)
		{
			Vector2I cur = queue.Dequeue();
			foreach (int dir in MazeGrid.Directions)
			{
				if (!grid.IsOpen(cur.X, cur.Y, dir)) continue;

				Vector2I off = MazeGrid.Offset(dir);
				var next = new Vector2I(cur.X + off.X, cur.Y + off.Y);
				if (distance.ContainsKey(next)) continue;

				distance[next] = distance[cur] + 1;
				farthest = Mathf.Max(farthest, distance[next]);
				queue.Enqueue(next);
			}
		}

		if (distance.Count != grid.ActiveCount)
		{
			GD.PrintErr($"[SELFCHECK] {label}：仅 {distance.Count}/{grid.ActiveCount} 格可达");
			failures++;
		}

		if (!distance.TryGetValue(grid.Exit, out int exitDistance) || exitDistance != farthest)
		{
			GD.PrintErr($"[SELFCHECK] {label}：出口不是最远格");
			failures++;
		}

		int passages = 0;
		for (int x = 0; x < grid.Width; x++)
		{
			for (int y = 0; y < grid.Height; y++)
			{
				if (grid.IsOpen(x, y, MazeGrid.East)) passages++;
				if (grid.IsOpen(x, y, MazeGrid.South)) passages++;
			}
		}

		if (passages != grid.ActiveCount - 1)
		{
			GD.PrintErr($"[SELFCHECK] {label}：通路数 {passages}，期望 {grid.ActiveCount - 1}");
			failures++;
		}

		return failures;
	}

	/// <summary>把整张图压成一个字符串，用于比较两张图是否完全一致</summary>
	private static string Fingerprint(MazeGrid grid)
	{
		var text = new System.Text.StringBuilder(
			$"{grid.Width}x{grid.Height}/{MazeGrid.ShapeName(grid.Shape)}/start{grid.Start}/exit{grid.Exit}/{grid.ActiveCount}|");

		for (int y = 0; y < grid.Height; y++)
		{
			for (int x = 0; x < grid.Width; x++)
			{
				if (!grid.IsActive(x, y))
				{
					text.Append('-');
					continue;
				}

				int mask = 0;
				if (grid.IsOpen(x, y, MazeGrid.North)) mask |= 1;
				if (grid.IsOpen(x, y, MazeGrid.East)) mask |= 2;
				if (grid.IsOpen(x, y, MazeGrid.South)) mask |= 4;
				if (grid.IsOpen(x, y, MazeGrid.West)) mask |= 8;
				text.Append("0123456789abcdef"[mask]);
			}
		}

		return text.ToString();
	}

	/// <summary>
	/// 种子可复现：同一个种子必须生成完全一样的图（不同玩家/不同启动都一致），
	/// 换一个种子则应当换成另一张图。
	/// </summary>
	private static void CheckSeedReproducibility()
	{
		int failures = 0;
		int cases = 0;

		foreach (MazeDifficulty difficulty in Difficulties)
		{
			DifficultyProfile profile = GameSettings.Describe(difficulty);

			for (int seed = 1; seed <= 12; seed++)
			{
				cases++;

				var first = new MazeGrid(profile.GridWidth, profile.GridHeight, MazeGrid.ShapeForSeed(seed), seed);
				var again = new MazeGrid(profile.GridWidth, profile.GridHeight, MazeGrid.ShapeForSeed(seed), seed);

				if (Fingerprint(first) != Fingerprint(again))
				{
					GD.PrintErr($"[SELFCHECK] {profile.Name} 难度 seed {seed}：同一颗种子生成了不同的迷宫");
					failures++;
				}

				var other = new MazeGrid(profile.GridWidth, profile.GridHeight, MazeGrid.ShapeForSeed(seed + 1), seed + 1);
				if (Fingerprint(first) == Fingerprint(other))
				{
					GD.PrintErr($"[SELFCHECK] {profile.Name} 难度 seed {seed} 与 {seed + 1}：不同种子生成了同一张迷宫");
					failures++;
				}
			}
		}

		GD.Print(failures == 0
			? $"[SELFCHECK] 种子可复现：{cases} 组同种子完全一致、异种子各不相同"
			: $"[SELFCHECK] 种子可复现：失败 {failures} 项");
	}

	private static void CheckInputMap()
	{
		string[] actions = { "move_left", "move_right", "move_up", "move_down", "restart" };
		int missing = 0;

		foreach (string action in actions)
		{
			if (!InputMap.HasAction(action))
			{
				GD.PrintErr($"[SELFCHECK] 缺少输入动作 {action}");
				missing++;
				continue;
			}

			GD.Print($"[SELFCHECK] {action} 绑定 {InputMap.ActionGetEvents(action).Count} 个按键");
		}

		GD.Print(missing == 0 ? "[SELFCHECK] 输入映射：完整" : $"[SELFCHECK] 输入映射：缺失 {missing} 项");
	}
}
