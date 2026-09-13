using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Agent 模式自检。三种用法：
///   --agent-demo                      用内置演示脑跑完一局（不需要联网），断言最终通关
///   --agent-stop                      用演示脑跑几步后点「停止」，断言循环真的退出
///   --agent-cancel --mock-url=...     假模型故意慢响应，断言点停止能立刻打断请求，不必等它返回
///   --mock-url=... [--format=chat|responses]
///                                     指向本地假模型，断言多轮工具调用跑通（验证两种接口格式的请求与解析）
/// 用法：Godot_console.exe --headless --path . res://tools/checks/agent_check.tscn --fixed-fps 60 -- --agent-demo
/// </summary>
public partial class AgentCheck : Node
{
	private const int MaxFrames = 400000;
	private const string AgentPath = "user://agent.dat";

	private MazeGame _maze;
	private bool _demo;
	private bool _shotOnly;
	private int _wantedSteps = 6;
	private int _frame;
	private bool _hadAgent;
	private byte[] _agentBackup;
	private readonly List<(string Path, bool Existed, byte[] Backup)> _leaderboardBackup = new();
	private int _frozenSteps;
	private float _frozenElapsed;
	private bool _resumed;
	private int _boardBefore;
	private bool _stopTest;
	private int _afterStop;
	private bool _cancelTest;

	public override void _Ready()
	{
		// 暂停时整棵树都会停，自检本身必须继续跑才能验证"暂停期间确实冻结"
		ProcessMode = ProcessModeEnum.Always;

		// 自检会改 Agent 配置、演示脑通关还会写排行榜，先按字节存下来，退出前还原
		_hadAgent = FileAccess.FileExists(AgentPath);
		if (_hadAgent) _agentBackup = FileAccess.GetFileAsBytes(AgentPath);

		foreach (MazeDifficulty difficulty in new[] { MazeDifficulty.Low, MazeDifficulty.Medium, MazeDifficulty.High })
		{
			string path = Leaderboard.PathFor(difficulty);
			_leaderboardBackup.Add((path, FileAccess.FileExists(path),
				FileAccess.FileExists(path) ? FileAccess.GetFileAsBytes(path) : null));
		}

		_demo = false;
		string mockUrl = "";
		var format = AgentStore.ApiFormat.Auto;

		foreach (string argument in OS.GetCmdlineUserArgs())
		{
			if (argument == "--agent-demo") _demo = true;
			else if (argument == "--shot") _shotOnly = true;
			else if (argument == "--agent-stop") _stopTest = true;
			else if (argument == "--agent-cancel") _cancelTest = true;
			else if (argument.StartsWith("--mock-url=", StringComparison.Ordinal)) mockUrl = argument["--mock-url=".Length..];
			else if (argument == "--format=responses") format = AgentStore.ApiFormat.Responses;
			else if (argument == "--format=chat") format = AgentStore.ApiFormat.ChatCompletions;
		}

		if (!_demo) mockUrl = mockUrl.Length > 0 ? mockUrl : AgentStore.DemoBaseUrl;

		if (_shotOnly)
		{
			// 截图用：节奏放慢一点，看得到面板在滚
			UseProfile(AgentStore.DemoBaseUrl, "demo", "", 0.4f, AgentStore.ApiFormat.Auto);
			GD.Print("[AGENTCHECK] 模式=演示脑（截图）");
		}
		else if (_stopTest)
		{
			// 只验证「停止」按钮能否中断循环，不跑到通关
			UseProfile(AgentStore.DemoBaseUrl, "demo", "", 0f, AgentStore.ApiFormat.Auto);
			GD.Print("[AGENTCHECK] 模式=演示脑（只测停止按钮）");
		}
		else if (_cancelTest)
		{
			// 假模型故意慢响应，验证停止能立刻打断请求、而不是干等它回来
			UseProfile(mockUrl, "mock-model", "test-key", 0f, AgentStore.ApiFormat.Auto);
			GD.Print($"[AGENTCHECK] 模式=慢响应假模型 {mockUrl}（测立即中断）");
		}
		else if (_demo)
		{
			// 演示脑不花钱，间隔设 0 让自检跑得快
			UseProfile(AgentStore.DemoBaseUrl, "demo", "", 0f, AgentStore.ApiFormat.Auto);
			GD.Print("[AGENTCHECK] 模式=演示脑，跑完一局");
		}
		else
		{
			UseProfile(mockUrl, "mock-model", "test-key", 0f, format);
			GD.Print($"[AGENTCHECK] 模式=假模型 {mockUrl}，格式={AgentStore.FormatLabel(AgentStore.EffectiveFormat)}，" +
				$"请求地址={AgentStore.RequestUrl}");
		}

		GameSettings.Difficulty = MazeDifficulty.Low;
		GameSettings.Seed = 1;
		GameSettings.AgentPlays = true;
		_boardBefore = Leaderboard.Load(MazeDifficulty.Low).Count;

		_maze = GD.Load<PackedScene>("res://scenes/maze.tscn").Instantiate<MazeGame>();
		AddChild(_maze);

		// 自检本身要一直跑（否则没法验证"暂停期间确实冻结"），但迷宫必须显式设成 Pausable，
		// 否则它会跟着自检一起变成 Always、暂停就失效了。真实游戏里根节点是 Inherit，本来就会停。
		_maze.ProcessMode = ProcessModeEnum.Pausable;

		if (_cancelTest)
		{
			// 按真实时间判定（假模型的延迟也是真实时间），不跟帧数挂钩
			GetTree().CreateTimer(0.5).Timeout += PressStop;
			GetTree().CreateTimer(1.2).Timeout += CheckCancelled;
		}
	}

	private void PressStop()
	{
		if (!IsInstanceValid(this) || _maze?.Panel == null) return;

		_maze.Panel.GetNode<Button>("StopButton").EmitSignal(BaseButton.SignalName.Pressed);
		GD.Print("[AGENTCHECK] 已点停止，此时那个慢请求还没返回");
	}

	private void CheckCancelled()
	{
		if (!IsInstanceValid(this) || _maze?.Panel == null)
		{
			Report(false, "取消测试：面板已不存在");
			return;
		}

		Button button = _maze.Panel.GetNode<Button>("StopButton");
		bool finished = button.Text == "已结束";
		Report(finished, $"慢请求下点停止：1.2 秒内面板已收尾（按钮=\"{button.Text}\"，没等请求返回）");
	}

	public override void _Process(double delta)
	{
		_frame++;

		if (_shotOnly)
		{
			if (_frame == 240) Shoot("agent_panel.png");
			else if (_frame == 280) Report(true, $"截图完成（面板 {_maze.Panel?.StepCount ?? 0} 步）");
			return;
		}

		if (_frame == 2)
		{
			if (!_demo && _maze.Agent == null) Fail("没有挂上 Agent 循环");
			if (!_demo && _maze.Panel == null) Fail("没有挂上思考面板");
			if (_maze.HumanControllable) Fail("Agent 模式时玩家操作没有被屏蔽");
		}

		if (_cancelTest)
		{
			// 判定走 CreateTimer（真实时间），这里只兜底防止挂死
			if (_frame > MaxFrames) Report(false, "取消测试超时");
			return;
		}

		if (_stopTest)
		{
			if (_frame == 60)
			{
				_maze.Panel.GetNode<Button>("StopButton").EmitSignal(BaseButton.SignalName.Pressed);
			}
			else if (_frame == 120)
			{
				_afterStop = _maze.Panel.StepCount;
			}
			else if (_frame == 160)
			{
				Button button = _maze.Panel.GetNode<Button>("StopButton");
				bool finished = button.Text == "已结束";
				bool frozen = _maze.Panel.StepCount == _afterStop;
				bool notWon = !_maze.Won;

				Report(finished && frozen && notWon,
					$"点停止后循环退出：按钮=\"{button.Text}\"，步数冻结在 {_afterStop}（现 {_maze.Panel.StepCount}），未通关={notWon}");
				return;
			}

			if (_frame > MaxFrames) Report(false, "停止测试超时");
			return;
		}

		if (_demo)
		{
			if (_frame == 60)
			{
				_frozenSteps = _maze.Panel?.StepCount ?? 0;
				_frozenElapsed = _maze.ElapsedSeconds;
				PressEscape();
			}
			else if (_frame == 130)
			{
				if (!GetTree().Paused) Fail("按 Esc 没有暂停");
				else if ((_maze.Panel?.StepCount ?? 0) != _frozenSteps || _maze.ElapsedSeconds - _frozenElapsed > 0.05f)
				{
					// 允许 3 帧以内的漂移：记录与 Esc 生效之间会差一帧
					Fail($"暂停期间仍在推进：步数 {_frozenSteps}→{_maze.Panel?.StepCount}，" +
						$"用时 {_frozenElapsed:0.00}→{_maze.ElapsedSeconds:0.00}");
				}
				else
				{
					GD.Print($"[AGENTCHECK] 暂停生效：步数与用时冻结在 {_frozenSteps} 步 / {_frozenElapsed:0.00} 秒");
					PressEscape();   // 再按一次从暂停面板继续
				}
			}

			if (_frame > 140 && _maze.Won)
			{
				if (!_resumed) { _resumed = true; GD.Print("[AGENTCHECK] 已恢复并继续跑到通关"); }

				// Agent 模式的成绩不应进排行榜，结算也要标明
				var win = _maze.GetNode<Label>("UI/WinLabel");
				bool boardUntouched = Leaderboard.Load(MazeDifficulty.Low).Count == _boardBefore;
				bool marked = win.Text.Contains("不计入排行榜");

				Report(boardUntouched && marked,
					$"演示脑通关：走 {_maze.MoveCount} 步，用时 {_maze.ElapsedSeconds:0.00} 秒，" +
					$"面板 {_maze.Panel?.StepCount ?? 0} 步；排行榜未被写入={boardUntouched}，结算已标注={marked}");
				return;
			}
		}
		else if (_maze.Panel != null && _maze.Panel.StepCount >= _wantedSteps)
		{
			Report(true, $"假模型往返 {_maze.Panel.StepCount} 步成功，角色实际走 {_maze.MoveCount} 步");
			return;
		}

		if (_frame > MaxFrames) Report(false, $"超过 {MaxFrames} 帧仍未达成目标（已走 {_maze.MoveCount} 步）");
	}

	/// <summary>把自检用的模型配置写进模型库并选中它</summary>
	private static void UseProfile(string url, string model, string key, float delay, AgentStore.ApiFormat format)
	{
		AgentStore.Select(AgentStore.Add(new AgentProfile
		{
			Name = "自检用",
			BaseUrl = url,
			Model = model,
			ApiKey = key,
		}));

		AgentStore.StepDelay = delay;
		AgentStore.Format = format;
		AgentStore.Save();
	}

	private static void PressEscape() =>
		Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });

	private async void Shoot(string name)
	{
		if (DisplayServer.GetName() == "headless") return;

		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = GetViewport().GetTexture().GetImage();
		image.SavePng($"res://tools/out/{name}");
		GD.Print($"[AGENTCHECK] 截图 {name}");
	}

	private void Report(bool ok, string message)
	{
		RestoreUserData();
		GD.Print(ok ? $"[AGENTCHECK] 通过：{message}" : $"[AGENTCHECK] 失败：{message}");
		GetTree().Quit(ok ? 0 : 1);
	}

	private void Fail(string message)
	{
		RestoreUserData();
		GD.PrintErr($"[AGENTCHECK] 失败：{message}");
		GetTree().Quit(1);
	}

	/// <summary>还原自检前的用户数据（原本没有就删掉），别覆盖玩家真实的 key 与成绩</summary>
	private void RestoreUserData()
	{
		RestoreFile(AgentPath, _hadAgent, _agentBackup);

		foreach ((string path, bool existed, byte[] backup) in _leaderboardBackup)
		{
			RestoreFile(path, existed, backup);
		}
	}

	/// <summary>还原单个文件（原本没有就删掉）</summary>
	private static void RestoreFile(string path, bool existed, byte[] backup)
	{
		if (existed)
		{
			using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
			file?.StoreBuffer(backup);
			return;
		}

		if (!FileAccess.FileExists(path)) return;

		Error error = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
		if (error != Error.Ok) GD.PrintErr($"[AGENTCHECK] 清理失败：{path}（{error}）");
	}
}
