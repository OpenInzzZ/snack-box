using Godot;
using System.Collections.Generic;

/// <summary>
/// 声波迷宫主控：生成迷宫、驱动玩家移动、发射声波并按波前点亮墙壁
/// 背景全黑，玩家与声波、墙壁均为白色；墙壁被点亮后逐渐衰减回黑色
/// 声波不考虑反弹与遮挡：波前扫过的墙壁即被点亮
/// 绘制全部交给子节点 MazeView（只读取本节点的状态）
/// </summary>
public partial class MazeGame : Node2D
{
	/// <summary>移动速度（格/秒）</summary>
	[Export] public float MoveSpeedCells = 5f;

	/// <summary>声波扩散速度（格/秒）</summary>
	[Export] public float WaveSpeedCells = 11f;

	/// <summary>每移动多少格发射一次声波</summary>
	[Export] public float EmitEveryCells = 1.5f;

	/// <summary>玩家方块边长占格宽的比例</summary>
	[Export] public float PlayerSizeRatio = 0.34f;

	/// <summary>墙厚占格宽的比例</summary>
	[Export] public float WallThicknessRatio = 0.12f;

	/// <summary>每滚一格滚轮缩放多少</summary>
	[Export] public float ZoomStep = 1.15f;

	/// <summary>缩放的平滑速度（越大越跟手）</summary>
	[Export] public float ZoomSmoothing = 12f;

	private const float ZoomMin = 0.2f;
	private const float ZoomMax = 1.6f;

	/// <summary>声波外形的方向采样数：越大轮廓越贴合墙面，代价是发射时的射线求交次数</summary>
	private const int WaveRayCount = 240;

	/// <summary>格宽（像素）。固定值，地图再大也不缩小；视图由跟随相机滚动。</summary>
	private const float CellPixels = 36f;

	/// <summary>一段墙：中心线两端点 + 绘制矩形 + 当前亮度（0=背景黑，1=纯白）</summary>
	public struct Wall
	{
		public Vector2 A;
		public Vector2 B;
		public Rect2 Rect;
		public float Intensity;
	}

	/// <summary>一圈正在扩散的声波；外形与可见性在发射瞬间算好，之后不再变</summary>
	public class SoundWave
	{
		public Vector2 Center;
		public float Radius;
		public float MaxRadius;

		/// <summary>
		/// 各方向射线撞到墙的距离（无墙则为 MaxRadius）。
		/// 波形轮廓取"当前半径"与它的较小值，撞到墙的方向便停在墙面不再外扩。
		/// </summary>
		public float[] FrontDistance;

		/// <summary>
		/// 与 Walls 同下标：波前到达该墙最近被击中处的距离。
		/// 无穷表示这段墙被别的墙整段挡住，这一圈波永远不会点亮它。
		/// </summary>
		public float[] RevealDistance;

		/// <summary>出口是否在声源的直线视线内</summary>
		public bool ExitVisible;
	}

	public IReadOnlyList<Wall> Walls => _walls;
	public IReadOnlyList<SoundWave> Waves => _waves;
	public MazeGrid Grid => _grid;
	public Vector2 PlayerPosition => _player.Position;
	public float PlayerSize => _playerSize;
	public float CellSize => _cellSize;
	public Vector2 ExitCenter => CellCenter(_grid.Exit);
	public float ExitSize => _cellSize * 0.34f;
	public float ExitIntensity => _exitIntensity;
	public bool Won => _won;

	/// <summary>通关后整座迷宫已点亮（用于额外绘制起点等全图信息）</summary>
	public bool Revealed => _revealed;

	/// <summary>玩家出生格的中心</summary>
	public Vector2 StartCenter => CellCenter(_grid.Start);

	public float StartRadius => _cellSize * 0.22f;

	/// <summary>本局用时（秒），供 Agent 桥读取</summary>
	public float ElapsedSeconds => _elapsed;

	/// <summary>本局地图种子（同一个种子 = 同一张图）</summary>
	public int Seed => _seed;

	/// <summary>当前相机缩放（1 = 原始格宽）</summary>
	public float Zoom => _zoom;

	/// <summary>世界坐标换算成"格坐标"（含小数），供小地图定位</summary>
	public Vector2 GridPositionOf(Vector2 worldPosition) => (worldPosition - _mazeOrigin) / _cellSize;

	/// <summary>本局走过的格数（换格才计数）</summary>
	public int MoveCount { get; private set; }

	/// <summary>玩家能否操作；Agent 模式时关掉，避免跟模型抢控制</summary>
	public bool HumanControllable => !GameSettings.AgentPlays;

	/// <summary>
	/// Agent 模式时，只有它自己行走的那一小段时间才接受按键。
	/// 这样人机走的是同一条输入路径（声波、碰撞都不变），玩家的按键则被忽略。
	/// </summary>
	public bool AcceptMoveInput { get; set; }

	/// <summary>Agent 模式时的思考面板（没开则为 null）</summary>
	public AgentPanel Panel { get; private set; }

	/// <summary>Agent 模式时的循环节点（没开则为 null）</summary>
	public AgentPlayer Agent { get; private set; }

	private MazeGrid _grid;
	private MazeShape _shape;
	private int _seed;
	private DifficultyProfile _profile;
	private readonly List<Wall> _walls = new();
	private readonly List<SoundWave> _waves = new();

	private CharacterBody2D _player;
	private CollisionShape2D _playerShape;
	private Camera2D _camera;
	private SubViewport _minimapViewport;
	private Camera2D _minimapCamera;
	private TextureRect _minimapDisplay;
	private PauseMenu _pauseMenu;
	private StaticBody2D _wallsBody;
	private Label _winLabel;
	private Label _timerLabel;
	private RichTextLabel _leaderboardLabel;
	private ColorRect _resultPanel;

	private float _cellSize;
	private float _wallThickness;
	private float _playerSize;
	private Vector2 _mazeOrigin;
	private float _exitIntensity;
	private bool _won;
	private float _travel;
	private Vector2 _lastPlayerPosition;

	/// <summary>本局用时（秒），从玩家第一次真正移动开始累计，通关即停</summary>
	private float _elapsed;
	private bool _timing;
	private Vector2I _lastCell;

	/// <summary>本局在排行榜上的名次（0 = 没进榜）</summary>
	private int _leaderboardRank;

	/// <summary>通关后整座迷宫点亮，不再衰减</summary>
	private bool _revealed;

	/// <summary>本局是否 Agent 模式：代打时玩家只能看，成绩也不计入排行榜</summary>
	private bool _agentRun;

	/// <summary>通关后把镜头对准地图中心（而不是继续跟着角色），这样缩到全图时整张图居中</summary>
	private bool _centerOnMaze;

	/// <summary>当前与目标缩放：滚轮改目标值，实际值每帧平滑逼近</summary>
	private float _zoom = 1f;
	private float _targetZoom = 1f;

	public override void _Ready()
	{
		AgentBridge.ApplyStartupArgs();
		DisplaySettings.ApplySaved();

		_player = GetNode<CharacterBody2D>("Player");
		_playerShape = GetNode<CollisionShape2D>("Player/Shape");
		_camera = GetNode<Camera2D>("Player/Camera");
		_minimapViewport = GetNode<SubViewport>("MinimapViewport");
		_minimapCamera = GetNode<Camera2D>("MinimapViewport/MinimapCamera");
		_minimapDisplay = GetNode<TextureRect>("UI/MinimapDisplay");
		_wallsBody = GetNode<StaticBody2D>("Walls");
		_winLabel = GetNode<Label>("UI/WinLabel");
		_timerLabel = GetNode<Label>("UI/TimerLabel");
		_leaderboardLabel = GetNode<RichTextLabel>("UI/LeaderboardLabel");
		_resultPanel = GetNode<ColorRect>("UI/ResultPanel");

		// 小地图视口看同一份世界，再额外叠一层只给它看的加粗几何（主视口剔除该层）
		GetViewport().CanvasCullMask = MinimapGeometry.MainViewportMask;
		_minimapViewport.CanvasCullMask = MinimapGeometry.MinimapViewportMask;
		_minimapViewport.World2D = GetViewport().World2D;
		_minimapDisplay.Texture = _minimapViewport.GetTexture();

		BuildNewMaze();

		_pauseMenu = GD.Load<PackedScene>("res://scenes/pause_menu.tscn").Instantiate<PauseMenu>();
		AddChild(_pauseMenu);
		_pauseMenu.ResumeRequested += Resume;
		_pauseMenu.MenuRequested += ReturnToMenu;

		// Agent 模式模式：挂上左侧思考面板与 Agent 循环
		if (GameSettings.AgentPlays) StartAgent();

		// 带 --agent-port 启动时挂上 Agent 桥，让 AI 也能通过 HTTP 玩这一局
		if (AgentBridge.RequestedPort > 0) AddChild(new AgentBridge());
	}

	/// <summary>进入 Agent 模式：实例化思考面板，并让 AgentPlayer 接管</summary>
	private void StartAgent()
	{
		_agentRun = true;

		Panel = GD.Load<PackedScene>("res://scenes/agent_panel.tscn").Instantiate<AgentPanel>();
		AddChild(Panel);

		Agent = new AgentPlayer();
		AddChild(Agent);
		Agent.Begin(this, Panel);
	}

	public override void _PhysicsProcess(double delta)
	{
		if (HumanControllable && Input.IsActionJustPressed("restart"))
		{
			BuildNewMaze();
			return;
		}

		Vector2 input = HumanControllable || AcceptMoveInput
			? Input.GetVector("move_left", "move_right", "move_up", "move_down")
			: Vector2.Zero;

		_player.Velocity = input * _cellSize * MoveSpeedCells;
		_player.MoveAndSlide();

		Vector2 position = _player.Position;
		float moved = position.DistanceTo(_lastPlayerPosition);
		_lastPlayerPosition = position;

		// 第一次真正挪动才开始计时（顶着墙推不算）
		if (!_timing && moved > 0.0001f) _timing = true;

		Vector2I cell = CellAt(position);
		if (cell != _lastCell)
		{
			_lastCell = cell;
			MoveCount++;
		}

		// 走够距离就发声波：声音由脚步带动，停下即安静
		float emitDistance = _cellSize * EmitEveryCells;
		if (emitDistance <= 0f) return;

		_travel += moved;
		while (_travel >= emitDistance)
		{
			_travel -= emitDistance;
			EmitWave();
		}
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;

		UpdateZoom(dt);
		if (_timing && !_won)
		{
			_elapsed += dt;
			UpdateTimerLabel();
		}

		AdvanceWaves(dt);
		FadeWalls(dt);
		CheckReachExit();
	}

	/// <summary>相机每帧更新：缩放平滑逼近目标值；通关后把镜头移到地图中心</summary>
	private void UpdateZoom(float dt)
	{
		float step = Mathf.Min(1f, dt * ZoomSmoothing);

		if (!Mathf.IsEqualApprox(_zoom, _targetZoom))
		{
			_zoom = Mathf.Abs(_zoom - _targetZoom) < 0.002f
				? _targetZoom
				: Mathf.Lerp(_zoom, _targetZoom, step);

			_camera.Zoom = Vector2.One * _zoom;
		}

		// 相机是玩家的子节点，所以"对准地图中心"要用相对玩家的偏移来表达
		Vector2 targetOffset = _centerOnMaze ? MazeCenter() - _player.Position : Vector2.Zero;
		if (_camera.Position.DistanceTo(targetOffset) > 0.5f)
			_camera.Position = _camera.Position.Lerp(targetOffset, step);
	}

	/// <summary>迷宫在世界坐标里的中心</summary>
	private Vector2 MazeCenter() => _mazeOrigin + new Vector2(_grid.Width, _grid.Height) * _cellSize * 0.5f;

	/// <summary>把小地图相机拉到刚好框住整张迷宫（地图中心就是世界原点）</summary>
	private void FrameMinimap()
	{
		Vector2 size = _minimapViewport.Size;
		float span = Mathf.Max(_grid.Width, _grid.Height) * _cellSize + _cellSize * 2f;

		_minimapCamera.Position = MazeCenter();
		_minimapCamera.Zoom = Vector2.One * (Mathf.Min(size.X, size.Y) / span);
	}

	/// <summary>按比例调整目标缩放，滚轮与 +/- 都走这里</summary>
	private void ZoomBy(float factor) => _targetZoom = Mathf.Clamp(_targetZoom * factor, ZoomMin, ZoomMax);

	/// <summary>缩到刚好装下整张迷宫的比例（通关看全貌用）</summary>
	private float FitZoom()
	{
		Vector2 viewport = GetViewportRect().Size;
		float mazeWidth = _grid.Width * _cellSize;
		float mazeHeight = _grid.Height * _cellSize;

		return Mathf.Clamp(Mathf.Min(viewport.X / mazeWidth, viewport.Y / mazeHeight) * 0.94f, ZoomMin, ZoomMax);
	}

	/// <summary>右上角状态：难度 + 种子 + 用时</summary>
	private void UpdateTimerLabel() =>
		_timerLabel.Text = $"难度 {_profile.Name}　种子 {_seed}　用时 {_elapsed:0.00} 秒";

	public override void _UnhandledInput(InputEvent @event)
	{
		// Agent 模式时只允许暂停，缩放 / 全屏 / 其它操作一律忽略，玩家只负责看
		if (_agentRun)
		{
			if (@event.IsActionPressed("ui_cancel"))
			{
				GetViewport().SetInputAsHandled();
				Pause();
			}

			return;
		}

		if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F11 })
		{
			DisplaySettings.ToggleFullscreen();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (@event.IsActionPressed("ui_cancel"))
		{
			// 必须先标记已处理、再动场景：ChangeSceneToFile 会立刻把本场景摘出树，
			// 之后再调 GetViewport() 就是空引用了
			GetViewport().SetInputAsHandled();
			Pause();
			return;
		}

		if (TryZoomInput(@event)) GetViewport().SetInputAsHandled();
	}

	/// <summary>暂停：整棵树停住，计时与声波自然一起冻结；暂停面板仍可交互</summary>
	public void Pause()
	{
		GetTree().Paused = true;
		_pauseMenu.Open();
	}

	public void Resume()
	{
		GetTree().Paused = false;
		_pauseMenu.Close();
	}

	/// <summary>从暂停面板返回开始界面；必须先解除暂停，否则新场景也不会跑</summary>
	private void ReturnToMenu()
	{
		GetTree().Paused = false;
		_pauseMenu.Close();
		GetTree().ChangeSceneToFile("res://scenes/main_menu.tscn");
	}

	/// <summary>滚轮或 +/- 调整缩放</summary>
	private bool TryZoomInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { Pressed: true } button)
		{
			if (button.ButtonIndex == MouseButton.WheelUp)
			{
				ZoomBy(ZoomStep);
				return true;
			}

			if (button.ButtonIndex == MouseButton.WheelDown)
			{
				ZoomBy(1f / ZoomStep);
				return true;
			}

			return false;
		}

		if (@event is not InputEventKey { Pressed: true, Echo: false } key) return false;

		if (key.PhysicalKeycode is Key.Equal or Key.KpAdd)
		{
			ZoomBy(ZoomStep);
			return true;
		}

		if (key.PhysicalKeycode is Key.Minus or Key.KpSubtract)
		{
			ZoomBy(1f / ZoomStep);
			return true;
		}

		return false;
	}

	/// <summary>
	/// 以玩家当前位置为圆心发射一圈声波
	/// 发射瞬间朝四面八方打射线：射线打到哪面墙，声波就在那个方向被挡住，
	/// 同时那面墙记下"被点亮距离"（取所有打到它的射线里最近的一发）。
	/// 被别的墙整段挡住的墙不会被任何射线打到，这一圈波永远不会点亮它。
	/// </summary>
	public void EmitWave()
	{
		Vector2 center = _player.Position;
		float maxRadius = _cellSize * _profile.WaveRevealCells;

		// 声波够得着的墙才需要参与判定（挡在中间的墙必然也在这个范围内）
		var candidates = new List<int>();
		for (int i = 0; i < _walls.Count; i++)
		{
			if (DistanceToSegment(center, _walls[i].A, _walls[i].B) <= maxRadius + _cellSize)
				candidates.Add(i);
		}

		var wave = new SoundWave
		{
			Center = center,
			Radius = 0f,
			MaxRadius = maxRadius,
			FrontDistance = new float[WaveRayCount],
			RevealDistance = new float[_walls.Count],
		};

		for (int i = 0; i < _walls.Count; i++) wave.RevealDistance[i] = float.PositiveInfinity;

		for (int k = 0; k < WaveRayCount; k++)
		{
			float angle = Mathf.Tau * k / WaveRayCount;
			var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

			float nearest = maxRadius;
			int hitWall = -1;

			foreach (int i in candidates)
			{
				if (!TryRayRect(center, direction, _walls[i].Rect, nearest, out float distance)) continue;

				nearest = distance;
				hitWall = i;
			}

			wave.FrontDistance[k] = nearest;
			if (hitWall >= 0 && nearest < wave.RevealDistance[hitWall]) wave.RevealDistance[hitWall] = nearest;
		}

		wave.ExitVisible = !IsBlocked(center, ExitCenter, -1, candidates);

		_waves.Add(wave);
	}

	/// <summary>从 from 到 to 的直线是否被候选墙挡住（ignoreIndex 为目标墙自身）</summary>
	private bool IsBlocked(Vector2 from, Vector2 to, int ignoreIndex, List<int> candidates)
	{
		foreach (int i in candidates)
		{
			if (i == ignoreIndex) continue;
			if (SegmentHitsRect(from, to, _walls[i].Rect)) return true;
		}

		return false;
	}

	/// <summary>线段是否穿过墙体矩形（贴边也算，宁可多挡不可漏）</summary>
	private static bool SegmentHitsRect(Vector2 from, Vector2 to, Rect2 rect)
	{
		Vector2 direction = to - from;
		float enter = 0f;
		float exit = 1f;

		return ClipAxis(from.X, direction.X, rect.Position.X, rect.End.X, ref enter, ref exit)
			&& ClipAxis(from.Y, direction.Y, rect.Position.Y, rect.End.Y, ref enter, ref exit);
	}

	/// <summary>射线是否命中墙体矩形，命中则给出到墙面的距离</summary>
	private static bool TryRayRect(Vector2 origin, Vector2 direction, Rect2 rect, float maxDistance, out float distance)
	{
		float enter = 0f;
		float exit = maxDistance;

		if (!ClipAxis(origin.X, direction.X, rect.Position.X, rect.End.X, ref enter, ref exit)
			|| !ClipAxis(origin.Y, direction.Y, rect.Position.Y, rect.End.Y, ref enter, ref exit))
		{
			distance = 0f;
			return false;
		}

		distance = Mathf.Max(enter, 0f);
		return true;
	}

	/// <summary>用一条 slab 裁剪线段参数区间，返回区间是否仍然有效</summary>
	private static bool ClipAxis(float origin, float direction, float min, float max, ref float enter, ref float exit)
	{
		if (Mathf.Abs(direction) < 0.00001f) return origin >= min && origin <= max;

		float t1 = (min - origin) / direction;
		float t2 = (max - origin) / direction;
		if (t1 > t2) (t1, t2) = (t2, t1);

		enter = Mathf.Max(enter, t1);
		exit = Mathf.Min(exit, t2);
		return enter <= exit;
	}

	/// <summary>重新生成迷宫、墙面碰撞与玩家出生点</summary>
	private void BuildNewMaze()
	{
		// 每次重建都重新读难度：Agent 桥可以在不重载场景的情况下换难度
		_profile = GameSettings.Profile;

		_waves.Clear();
		_won = false;
		_revealed = false;
		_agentRun = false;
		_exitIntensity = 0f;
		_winLabel.Visible = false;
		_leaderboardLabel.Visible = false;
		_resultPanel.Visible = false;
		_leaderboardRank = 0;
		_elapsed = 0f;
		_timing = false;

		// 指定了种子就固定用它（重开还是同一张图，便于刷成绩）；
		// 没指定就每次随机，外形也由种子决定，保证"同一个种子 = 同一张图"
		_seed = GameSettings.Seed != 0 ? GameSettings.Seed : 1 + (int)(GD.Randi() % GameSettings.MaxSeed);
		_shape = MazeGrid.ShapeForSeed(_seed);
		_grid = new MazeGrid(Mathf.Max(3, _profile.GridWidth), Mathf.Max(3, _profile.GridHeight), _shape, _seed);
		ComputeLayout();
		BuildWalls();
		PlacePlayer();
		FrameMinimap();

		// 新一局回到默认缩放、镜头跟着角色
		_zoom = 1f;
		_targetZoom = 1f;
		_centerOnMaze = false;
		_camera.Zoom = Vector2.One;
		_camera.Position = Vector2.Zero;

		UpdateTimerLabel();

		// 开局先响一声，给出周围轮廓
		EmitWave();
	}

	private void ComputeLayout()
	{
		_cellSize = CellPixels;
		_wallThickness = Mathf.Max(2f, _cellSize * WallThicknessRatio);
		_playerSize = _cellSize * PlayerSizeRatio;

		// 迷宫以世界原点为中心，视图由玩家身下的相机跟随
		_mazeOrigin = -new Vector2(_grid.Width * _cellSize, _grid.Height * _cellSize) * 0.5f;

		((CircleShape2D)_playerShape.Shape).Radius = _playerSize * 0.5f;
	}

	/// <summary>某个像素坐标落在哪一格</summary>
	public Vector2I CellAt(Vector2 position)
	{
		Vector2 local = (position - _mazeOrigin) / _cellSize;
		return new Vector2I(Mathf.FloorToInt(local.X), Mathf.FloorToInt(local.Y));
	}

	/// <summary>格子中心的像素坐标</summary>
	public Vector2 CellCenter(Vector2I cell) =>
		_mazeOrigin + new Vector2((cell.X + 0.5f) * _cellSize, (cell.Y + 0.5f) * _cellSize);

	private void PlacePlayer()
	{
		_player.Position = CellCenter(_grid.Start);
		_player.Velocity = Vector2.Zero;
		_lastPlayerPosition = _player.Position;
		_travel = 0f;
		_lastCell = _grid.Start;
		MoveCount = 0;
	}

	/// <summary>
	/// 重建墙体：先清掉旧碰撞体，再按当前布局重新生成。
	/// 清理由这里负责，否则窗口缩放重排时会只加不清，碰撞体越叠越多、旧墙把通道堵死。
	/// 每段墙只登记一次：形状内的格子出北墙/西墙，以及"对面不在形状内"时出东墙/南墙
	/// （对面也在形状内的话，那面墙交给对面那格生成）。
	/// </summary>
	private void BuildWalls()
	{
		ClearWalls();
		_walls.Clear();

		for (int x = 0; x < _grid.Width; x++)
		{
			for (int y = 0; y < _grid.Height; y++)
			{
				if (!_grid.IsActive(x, y)) continue;   // 形状外的格子没有墙

				Vector2 topLeft = _mazeOrigin + new Vector2(x * _cellSize, y * _cellSize);
				Vector2 topRight = topLeft + new Vector2(_cellSize, 0f);
				Vector2 bottomLeft = topLeft + new Vector2(0f, _cellSize);

				if (_grid.HasWall(x, y, MazeGrid.North)) AddWall(topLeft, topRight, horizontal: true);
				if (_grid.HasWall(x, y, MazeGrid.West)) AddWall(topLeft, bottomLeft, horizontal: false);

				if (!_grid.IsActive(x + 1, y) && _grid.HasWall(x, y, MazeGrid.East))
					AddWall(topRight, topRight + new Vector2(0f, _cellSize), horizontal: false);
				if (!_grid.IsActive(x, y + 1) && _grid.HasWall(x, y, MazeGrid.South))
					AddWall(bottomLeft, bottomLeft + new Vector2(_cellSize, 0f), horizontal: true);
			}
		}
	}

	private void AddWall(Vector2 a, Vector2 b, bool horizontal)
	{
		// 中心线两端各外扩半个墙厚以封住转角缝；碰撞、绘制、遮挡判定统一用这条线
		Vector2 extension = (b - a).Normalized() * (_wallThickness * 0.5f);
		Vector2 start = a - extension;
		Vector2 end = b + extension;

		Vector2 size = horizontal
			? new Vector2(end.X - start.X, _wallThickness)
			: new Vector2(_wallThickness, end.Y - start.Y);
		Vector2 corner = horizontal
			? new Vector2(start.X, start.Y - _wallThickness * 0.5f)
			: new Vector2(start.X - _wallThickness * 0.5f, start.Y);

		_wallsBody.AddChild(new CollisionShape2D
		{
			Shape = new RectangleShape2D { Size = size },
			Position = (start + end) * 0.5f,
		});

		_walls.Add(new Wall
		{
			A = start,
			B = end,
			Rect = new Rect2(corner, size),
			Intensity = 0f,
		});
	}

	private void ClearWalls()
	{
		foreach (Node child in _wallsBody.GetChildren())
		{
			_wallsBody.RemoveChild(child);
			child.QueueFree();
		}
	}

	/// <summary>声波扩散：波前在 (上一帧半径, 本帧半径] 之间的墙壁被点亮一次</summary>
	private void AdvanceWaves(float dt)
	{
		float speed = _cellSize * WaveSpeedCells;

		for (int i = _waves.Count - 1; i >= 0; i--)
		{
			SoundWave wave = _waves[i];
			float previous = wave.Radius;
			wave.Radius += speed * dt;
			float reach = Mathf.Min(wave.Radius, wave.MaxRadius);

			for (int w = 0; w < _walls.Count; w++)
			{
				float distance = wave.RevealDistance[w];
				if (float.IsPositiveInfinity(distance)) continue;   // 被墙挡住或超出范围

				if (distance > previous && distance <= reach)
				{
					Wall wall = _walls[w];
					wall.Intensity = 1f;
					_walls[w] = wall;
				}
			}

			if (wave.ExitVisible)
			{
				float exitDistance = wave.Center.DistanceTo(ExitCenter);
				if (exitDistance > previous && exitDistance <= reach) _exitIntensity = 1f;
			}

			if (wave.Radius >= wave.MaxRadius) _waves.RemoveAt(i);
		}
	}

	/// <summary>没有新的声波激活时，墙壁亮度线性衰减回黑色；通关揭示全图后不再衰减</summary>
	private void FadeWalls(float dt)
	{
		if (_revealed) return;

		float step = dt / Mathf.Max(0.01f, _profile.WallFadeSeconds);

		for (int i = 0; i < _walls.Count; i++)
		{
			Wall wall = _walls[i];
			if (wall.Intensity <= 0f) continue;

			wall.Intensity = Mathf.Max(0f, wall.Intensity - step);
			_walls[i] = wall;
		}

		if (_exitIntensity > 0f) _exitIntensity = Mathf.Max(0f, _exitIntensity - step);
	}

	private void CheckReachExit()
	{
		if (_won) return;

		Vector2 center = ExitCenter;
		float half = _cellSize * 0.5f;
		if (Mathf.Abs(_player.Position.X - center.X) >= half) return;
		if (Mathf.Abs(_player.Position.Y - center.Y) >= half) return;

		_won = true;

		// Agent 模式的成绩不计入排行榜，避免自动刷榜
		_leaderboardRank = _agentRun ? 0 : Leaderboard.Record(_elapsed, GameSettings.Difficulty);

		_winLabel.Text = _leaderboardRank > 0
			? $"抵达出口！用时 {_elapsed:0.00} 秒　第 {_leaderboardRank} 名　种子 {_seed}"
			: _agentRun
				? $"抵达出口！用时 {_elapsed:0.00} 秒　种子 {_seed}　（Agent 模式，不计入排行榜）"
				: $"抵达出口！用时 {_elapsed:0.00} 秒　种子 {_seed}";
		_winLabel.Visible = true;
		_leaderboardLabel.Text = Leaderboard.FormatResult(GameSettings.Difficulty, _leaderboardRank);
		_leaderboardLabel.Visible = true;
		_resultPanel.Visible = true;
		RevealAllWalls();

		// 缩到刚好装下整张迷宫，并把镜头对准地图中心，否则大地图上"看全貌"只看得到一小块
		_centerOnMaze = true;
		_targetZoom = FitZoom();
	}

	/// <summary>通关后把整座迷宫点亮，作为"看全貌"的奖励</summary>
	private void RevealAllWalls()
	{
		_revealed = true;
		_waves.Clear();

		for (int i = 0; i < _walls.Count; i++)
		{
			Wall wall = _walls[i];
			wall.Intensity = 1f;
			_walls[i] = wall;
		}

		_exitIntensity = 1f;
	}

	/// <summary>点到线段的最短距离</summary>
	private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b) =>
		point.DistanceTo(ClosestPointOnSegment(point, a, b));

	/// <summary>线段上距离给定点最近的位置</summary>
	private static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 a, Vector2 b)
	{
		Vector2 ab = b - a;
		float lengthSquared = ab.LengthSquared();
		if (lengthSquared <= 0.0001f) return a;

		float t = Mathf.Clamp((point - a).Dot(ab) / lengthSquared, 0f, 1f);
		return a + ab * t;
	}
}
