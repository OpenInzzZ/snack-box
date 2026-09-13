using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Agent 游玩循环：每一步把"感知"交给模型 → 执行它要调的工具 → 把结果回灌 → 再问下一步。
/// 只给模型 echo_state / echo_move 两个工具（与外部 MCP 里的同名工具语义一致），
/// 也就是它只能靠声波感知摸索，看不到整张地图。
///
/// 行走沿用玩家的按键路径（发 WASD 事件、走到相邻格心），所以声波、淡出这些逻辑与玩家操作完全一致。
/// </summary>
public partial class AgentPlayer : Node
{
	private const int MaxApiErrors = 3;        // 连续报错到这个数就停（不是步数上限，是失败保护）
	private const int MaxNudges = 3;           // 模型只说话不动时，提醒几次
	private const int WalkFrameBudget = 90;    // 一格最多等多少物理帧

	private static readonly int[] WallBits =
	{
		MazeGrid.North, MazeGrid.East, MazeGrid.South, MazeGrid.West,
	};

	private readonly LlmClient _client = new();

	private MazeGame _maze;
	private AgentPanel _panel;
	private bool _stopped;

	public void Begin(MazeGame maze, AgentPanel panel)
	{
		_maze = maze;
		_panel = panel;

		AddChild(_client);
		_client.Reset();
		_panel.Begin();
		_ = Run();
	}

	/// <summary>由面板的"停止"按钮调用：置标志并打断正在飞的请求，不必等它返回</summary>
	public void RequestStop()
	{
		_stopped = true;
		_client.Cancel();
	}

	private async Task Run()
	{
		int errors = 0;
		int nudges = 0;

		try
		{
			// 不设步数上限：只在通关、玩家停止或连续请求失败时结束
			for (int step = 1; !_stopped && !_maze.Won; step++)
			{
				// 暂停时不推进（也不发请求），等继续后接着走
				await WaitWhilePaused();
				if (!IsInstanceValid(this) || _stopped) return;

				AgentProtocol.Perception perception = BuildPerception();
				_client.AddUser(AgentProtocol.Describe(perception));

				AgentProtocol.Reply reply = await _client.Send(perception);
				if (_stopped) break;

				if (reply.Error.Length > 0)
				{
					_panel.Error(step, reply.Error);

					if (++errors >= MaxApiErrors)
					{
						_panel.Finish(false, $"连续 {errors} 次请求失败，已停止。");
						return;
					}

					await Delay(1.5f);
					continue;
				}

				errors = 0;
				_panel.Assistant(step, reply);

				if (reply.Calls.Count == 0)
				{
					if (++nudges > MaxNudges)
					{
						_panel.Finish(false, "模型一直没有调用工具，已停止。");
						return;
					}

					_client.AddUser("请调用工具继续行动。");
					continue;
				}

				nudges = 0;
				AgentProtocol.ToolCall call = reply.Calls[0];
				string result = await ExecuteTool(call);
				_client.AddToolResult(call.Id, result);
				_panel.Tool(call, result);

				await Delay(AgentStore.StepDelay);
			}
		}
		catch (Exception ex)
		{
			// 场景可能已经被切走（比如暂停面板里点了返回菜单），这里只记日志
			GD.PrintErr($"[AGENT] 循环中断：{ex.Message}");
			return;
		}

		if (!IsInstanceValid(this) || !IsInstanceValid(_panel)) return;

		string summary = _maze.Won
			? $"已通关，用时 {_maze.ElapsedSeconds:0.00} 秒，共走 {_maze.MoveCount} 步。"
			: $"已停止，共走 {_maze.MoveCount} 步。";

		// 交回操作权，玩家可以接着逛（通关图已经揭示）
		GameSettings.AgentPlays = false;
		_panel.Finish(_maze.Won, summary);
	}

	/// <summary>执行模型要调的工具，返回给模型看的文字结果</summary>
	private async Task<string> ExecuteTool(AgentProtocol.ToolCall call)
	{
		switch (call.Name)
		{
			case "echo_move":
				return await Move(ParseDirection(call.Arguments));

			case "echo_state":
				return AgentProtocol.Describe(BuildPerception());

			default:
				return $"没有名为「{call.Name}」的工具。可用：echo_state、echo_move。";
		}
	}

	private async Task<string> Move(int direction)
	{
		if (direction < 0) return "direction 只能是 north / south / east / west。";

		Vector2I before = _maze.CellAt(_maze.PlayerPosition);
		Vector2I delta = AgentProtocol.Directions[direction].Delta;

		if (_maze.Grid.HasWall(before.X, before.Y, WallBits[direction]))
			return $"往{AgentProtocol.Directions[direction].Name}是墙，没走动。{ShortStatus()}";

		await Walk(direction);
		Vector2I after = _maze.CellAt(_maze.PlayerPosition);

		if (after == before) return $"往{AgentProtocol.Directions[direction].Name}没走动（可能被卡住）。{ShortStatus()}";

		return $"往{AgentProtocol.Directions[direction].Name}走到了 ({after.X},{after.Y})。{ShortStatus()}";
	}

	/// <summary>按住方向键走到相邻格心，与玩家操作同一条路径</summary>
	private async Task Walk(int direction)
	{
		await WaitWhilePaused();
		if (!IsInstanceValid(this)) return;

		Key key = direction switch
		{
			0 => Key.W,
			1 => Key.D,
			2 => Key.S,
			_ => Key.A,
		};

		Vector2I start = _maze.CellAt(_maze.PlayerPosition);
		Vector2 target = _maze.CellCenter(start + AgentProtocol.Directions[direction].Delta);
		Vector2I delta = AgentProtocol.Directions[direction].Delta;

		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = true });
		_maze.AcceptMoveInput = true;

		for (int frame = 0; frame < WalkFrameBudget; frame++)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

			Vector2 position = _maze.PlayerPosition;
			float along = delta.X != 0 ? (position.X - target.X) * delta.X : (position.Y - target.Y) * delta.Y;
			if (along >= 0f) break;
		}

		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = false });
		_maze.AcceptMoveInput = false;
		await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
	}

	private string ShortStatus()
	{
		AgentProtocol.Perception perception = BuildPerception();
		return $"当前在 ({perception.PlayerCell.X},{perception.PlayerCell.Y})，" +
			$"可走：{string.Join(' ', AgentProtocol.OpenDirections(perception))}。";
	}

	/// <summary>把当前能看到的信息整理成感知（只包含玩家本来看得到的东西）</summary>
	private AgentProtocol.Perception BuildPerception()
	{
		MazeGrid grid = _maze.Grid;
		Vector2I cell = _maze.CellAt(_maze.PlayerPosition);

		var perception = new AgentProtocol.Perception
		{
			Difficulty = GameSettings.DisplayName(GameSettings.Difficulty),
			Shape = MazeGrid.ShapeName(grid.Shape),
			GridWidth = grid.Width,
			GridHeight = grid.Height,
			PlayerCell = cell,
			Elapsed = _maze.ElapsedSeconds,
			Moves = _maze.MoveCount,
			Won = _maze.Won,
		};

		for (int i = 0; i < WallBits.Length; i++)
		{
			perception.CellWalls[i] = grid.IsActive(cell.X, cell.Y) && grid.HasWall(cell.X, cell.Y, WallBits[i]);
		}

		// 自己格子的墙会单独列，这里去掉避免重复
		var own = new HashSet<string>(perception.SelfWalls());
		foreach (MazeGame.Wall wall in _maze.Walls)
		{
			if (wall.Intensity <= 0.01f) continue;

			string name = AgentProtocol.WallName(
				_maze.GridPositionOf(wall.A), _maze.GridPositionOf(wall.B), grid.Width, grid.Height);

			if (own.Contains(name)) continue;

			perception.KnownWalls.Add(name);
		}

		return perception;
	}

	private static int ParseDirection(string arguments)
	{
		Variant parsed = Json.ParseString(arguments);
		if (parsed.VariantType != Variant.Type.Dictionary) return -1;

		return parsed.AsGodotDictionary().TryGetValue("direction", out Variant value)
			? AgentProtocol.DirectionIndex(value.AsString())
			: -1;
	}

	private async Task Delay(float seconds)
	{
		if (seconds <= 0f) return;

		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}

	/// <summary>暂停时挂起（不消耗、也不请求），恢复后继续</summary>
	private async Task WaitWhilePaused()
	{
		while (IsInstanceValid(this) && GetTree().Paused && !_stopped)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
	}
}
