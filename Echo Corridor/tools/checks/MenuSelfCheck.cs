using Godot;

/// <summary>
/// 开始界面自检，headless 即可运行。覆盖三页流程：
///   页面一：主页面（排行榜 / 设置面板）
///   页面二：选择模型（列表 / 新增 / 选中），仅 Agent 流程
///   页面三：游戏配置（难度 / 种子 / Agent 费用提醒）
/// 以及"正常游戏（Esc 暂停 → 返回菜单）"和"Agent 流程（玩家操作被锁）"两条路径。
/// 用 SceneTree 子类而非场景里的节点，这样切换场景时自检本身不会被销毁。
/// 用法：
///   逻辑自检：Godot_console.exe --headless --path . --script res://tools/checks/MenuSelfCheck.cs
///   带截图：  去掉 --headless，截图输出到 tools/out/
/// </summary>
public partial class MenuSelfCheck : SceneTree
{
	private const string MenuPath = "res://scenes/main_menu.tscn";
	private const string SettingsPath = "user://settings.cfg";
	private const string AgentPath = "user://agent.dat";
	private const double StepDelay = 0.15;
	private const double WatchdogSeconds = 30.0;

	private bool _headless;
	private int _failures;
	private int _shots;
	private bool _finished;

	private bool _hadSettings;
	private byte[] _settingsBackup;
	private bool _hadAgent;
	private byte[] _agentBackup;

	public override void _Initialize()
	{
		_headless = DisplayServer.GetName() == "headless";

		// 自检会改分辨率与模型库，先按字节存下来，退出前还原
		_hadSettings = FileAccess.FileExists(SettingsPath);
		if (_hadSettings) _settingsBackup = FileAccess.GetFileAsBytes(SettingsPath);

		_hadAgent = FileAccess.FileExists(AgentPath);
		if (_hadAgent) _agentBackup = FileAccess.GetFileAsBytes(AgentPath);

		string mainScene = (string)ProjectSettings.GetSetting("application/run/main_scene");
		Check(mainScene == MenuPath, $"启动场景指向开始界面（实际 {mainScene}）");
		Check(ResourceLoader.Exists(MenuPath), "开始界面场景文件存在");

		ChangeSceneToFile(MenuPath);

		// 看门狗：任何一步卡住或抛异常都不至于让进程一直挂着
		CreateTimer(WatchdogSeconds).Timeout += () =>
		{
			if (_finished) return;

			Check(false, "自检超时，流程没有走完");
			Finish();
		};

		Next(0.05, VerifyHome);
	}

	private Node Menu => CurrentScene;
	private Control Home => Menu.GetNode<Control>("Home");
	private Control ModelsPage => Menu.GetNode<Control>("ModelsPage");
	private Control SetupPage => Menu.GetNode<Control>("SetupPage");

	// ---------- 页面一 ----------

	private void VerifyHome()
	{
		Check(Menu != null && Menu.Name == "MainMenu", $"开始界面已成为当前场景（实际 {Menu?.Name}）");
		if (Menu == null)
		{
			Finish();
			return;
		}

		Check(Home.Visible && !ModelsPage.Visible && !SetupPage.Visible, "默认停在主页面");
		Shoot("menu.png");

		// 设置面板：显示模式 + 分辨率
		var settingsPanel = Menu.GetNode<Control>("Home/SettingsPanel");
		var modePicker = Menu.GetNode<OptionButton>("Home/SettingsPanel/ModeRow/ModePicker");
		var resolutionPicker = Menu.GetNode<OptionButton>("Home/SettingsPanel/ResolutionRow/ResolutionPicker");

		Check(!settingsPanel.Visible, "设置面板初始隐藏");
		Check(modePicker.ItemCount == DisplaySettings.Modes.Length, $"显示模式共 {modePicker.ItemCount} 项");
		Check(resolutionPicker.ItemCount == DisplaySettings.Resolutions.Length,
			$"分辨率档位共 {resolutionPicker.ItemCount} 项");

		Menu.GetNode<Button>("Home/ActionRow/SettingsButton").EmitSignal(BaseButton.SignalName.Pressed);
		Check(settingsPanel.Visible, "点设置后面板展开");
		Shoot("menu_settings.png");

		// 换分辨率，验证真的改窗口（无窗口环境跳过）
		int resolution = DisplaySettings.Resolutions.Length - 1;
		resolutionPicker.Selected = resolution;
		resolutionPicker.EmitSignal(OptionButton.SignalName.ItemSelected, resolution);
		Check(DisplaySettings.CurrentIndex == resolution,
			$"选中「{DisplaySettings.ResolutionLabel(resolution)}」后档位跟着切");

		Menu.GetNode<Button>("Home/ActionRow/SettingsButton").EmitSignal(BaseButton.SignalName.Pressed);
		Check(!settingsPanel.Visible, "再点一次收起设置");

		DisplaySettings.Apply(DisplaySettings.DefaultIndex, DisplaySettings.DefaultMode);
		modePicker.Selected = DisplaySettings.CurrentMode;
		resolutionPicker.Selected = DisplaySettings.CurrentIndex;

		Next(StepDelay, VerifyHumanSetup);
	}

	// ---------- 页面三（正常流程） ----------

	private void VerifyHumanSetup()
	{
		Menu.GetNode<Button>("Home/StartButton").EmitSignal(BaseButton.SignalName.Pressed);
		Check(SetupPage.Visible && !Home.Visible, "点开始游戏进入游戏配置页");
		Check(!Menu.GetNode<Control>("SetupPage/AgentWarning").Visible, "正常流程不显示 Agent 费用提醒");
		Shoot("menu_setup.png");

		// 截图在帧末落盘，后续会切场景，所以放到下一段继续
		Next(StepDelay, () =>
		{
			Button low = Menu.GetNode<Button>("SetupPage/DifficultyRow/LowButton");
			Button medium = Menu.GetNode<Button>("SetupPage/DifficultyRow/MediumButton");
			Button high = Menu.GetNode<Button>("SetupPage/DifficultyRow/HighButton");

			Check(low.ToggleMode && medium.ToggleMode && high.ToggleMode, "三个难度按钮都是可切换按钮");
			Check(!low.ButtonPressed && medium.ButtonPressed && !high.ButtonPressed, "默认选中「中」难度");

			var seedInput = Menu.GetNode<LineEdit>("SetupPage/SeedRow/SeedInput");
			Check(seedInput.PlaceholderText.Length > 0, $"种子输入框存在（占位提示：{seedInput.PlaceholderText}）");
			seedInput.Text = "424242";

			high.ButtonPressed = true;
			high.EmitSignal(BaseButton.SignalName.Pressed);
			Check(GameSettings.Difficulty == MazeDifficulty.High, "点「高」后难度档位切到高");

			Menu.GetNode<Button>("SetupPage/BeginButton").EmitSignal(BaseButton.SignalName.Pressed);
			Next(StepDelay, VerifyNormalGame);
		});
	}

	private void VerifyNormalGame()
	{
		if (Menu is not MazeGame maze)
		{
			Check(false, $"开始游戏后应进入迷宫（实际 {Menu?.Name}）");
			Finish();
			return;
		}

		DifficultyProfile expected = GameSettings.Describe(MazeDifficulty.High);
		Check(maze.Grid.Width == expected.GridWidth && maze.Grid.Height == expected.GridHeight,
			$"迷宫沿用「高」难度尺寸（{maze.Grid.Width}x{maze.Grid.Height}）");
		Check(GameSettings.Seed == 424242 && maze.Seed == 424242,
			$"种子输入生效（设置={GameSettings.Seed}，本局地图={maze.Seed}）");
		Check(maze.HumanControllable, "正常流程玩家可以操作");
		Shoot("menu_to_maze.png");

		Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
		Next(StepDelay, VerifyPaused);
	}

	/// <summary>Esc 现在是"暂停"：整棵树停住、暂停面板出现；再从面板返回菜单</summary>
	private void VerifyPaused()
	{
		Check(Paused, "按 Esc 后游戏暂停");

		var pauseMenu = ((Node)CurrentScene)?.GetNodeOrNull<CanvasLayer>("PauseMenu");
		Check(pauseMenu != null && pauseMenu.Visible, "暂停面板已显示");
		Shoot("menu_pause.png");

		// 截图在帧末落盘，点按钮必须放到下一段，否则拍到的是切场景之后的帧
		Next(StepDelay, () =>
		{
			pauseMenu.GetNode<Button>("Panel/MenuButton").EmitSignal(BaseButton.SignalName.Pressed);
			Check(!Paused, "暂停面板点返回后解除暂停");

			Next(StepDelay, VerifyBackToMenu);
		});
	}

	private void VerifyBackToMenu()
	{
		Check(!Paused, "返回菜单时暂停已解除");
		Check(Menu != null && Menu.Name == "MainMenu", "从暂停面板回到开始界面");
		Shoot("menu_back.png");

		Next(StepDelay, VerifyAgentModels);
	}

	// ---------- 页面二（Agent 流程）----------

	private void VerifyAgentModels()
	{
		Menu.GetNode<Button>("Home/AgentButton").EmitSignal(BaseButton.SignalName.Pressed);
		Check(ModelsPage.Visible && !Home.Visible, "Agent 模式进入选择模型页");

		var list = Menu.GetNode<VBoxContainer>("ModelsPage/ModelList");
		int before = list.GetChildCount();
		Check(before >= 1, $"模型列表至少有一套内置演示（{before} 套）");
		Shoot("menu_models.png");

		// 新增一套模型
		Menu.GetNode<Button>("ModelsPage/ListActions/AddButton").EmitSignal(BaseButton.SignalName.Pressed);
		var form = Menu.GetNode<Control>("EditPanel");
		Check(form.Visible, "点新增后弹出编辑表单");

		Menu.GetNode<LineEdit>("EditPanel/Form/NameRow/NameInput").Text = "自检模型";
		Menu.GetNode<LineEdit>("EditPanel/Form/UrlRow/UrlInput").Text = AgentStore.DemoBaseUrl;
		Menu.GetNode<LineEdit>("EditPanel/Form/ModelRow/ModelInput").Text = "demo";

		int countBefore = AgentStore.Count;
		Menu.GetNode<Button>("EditPanel/Form/FormButtons/SaveButton").EmitSignal(BaseButton.SignalName.Pressed);

		Check(!form.Visible, "保存后表单关闭");
		Check(AgentStore.Count == countBefore + 1, $"模型库多了一套（{countBefore} → {AgentStore.Count}）");
		Check(AgentStore.Current.Name == "自检模型", $"新增的模型被自动选中（当前 {AgentStore.Current.Name}）");

		// 切回内置演示，保证 Agent 跑起来不联网
		for (int i = 0; i < AgentStore.Count; i++)
		{
			if (AgentStore.At(i).BaseUrl == AgentStore.DemoBaseUrl)
			{
				AgentStore.Select(i);
				break;
			}
		}

		Next(StepDelay, VerifyAgentSetup);
	}

	private void VerifyAgentSetup()
	{
		Menu.GetNode<Button>("ModelsPage/NextButton").EmitSignal(BaseButton.SignalName.Pressed);
		Check(SetupPage.Visible && !ModelsPage.Visible, "下一步进入游戏配置页");

		Check(Menu.GetNode<Control>("SetupPage/AgentWarning").Visible, "Agent 流程醒目提示会消耗 token 产生费用");
		Shoot("menu_agent_setup.png");

		// 截图在帧末落盘，切场景要放到下一段
		Next(StepDelay, () =>
		{
			Menu.GetNode<Button>("SetupPage/BeginButton").EmitSignal(BaseButton.SignalName.Pressed);
			Next(StepDelay * 3, VerifyAgentPlay);
		});
	}

	private void VerifyAgentPlay()
	{
		if (Menu is not MazeGame maze || maze.Agent == null || maze.Panel == null)
		{
			Check(false, $"Agent 模式没有正常开局（当前 {Menu?.Name}）");
			Finish();
			return;
		}

		Check(!maze.HumanControllable, "Agent 模式下玩家操作被屏蔽");

		// 代打期间除暂停外的操作都要被忽略：模拟滚轮缩放与 F11
		float zoomBefore = maze.Zoom;
		bool windowedBefore = DisplaySettings.IsWindowed;
		Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true });
		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.F11, Pressed = true });

		Next(StepDelay, () =>
		{
			Check(Mathf.IsEqualApprox(maze.Zoom, zoomBefore) && DisplaySettings.IsWindowed == windowedBefore,
				$"Agent 模式下滑块与全屏都被忽略（缩放 {zoomBefore:0.00}→{maze.Zoom:0.00}）");

			// 暂停后返回菜单：此时 Agent 的循环正挂起
			maze.Pause();
			maze.GetNode<CanvasLayer>("PauseMenu").GetNode<Button>("Panel/MenuButton")
				.EmitSignal(BaseButton.SignalName.Pressed);

			Next(StepDelay * 2, () =>
			{
				Check(Menu != null && Menu.Name == "MainMenu", "Agent 模式中暂停后能正常返回开始界面");
				Finish();
			});
		});
	}

	// ---------- 工具 ----------

	private void Next(double delay, System.Action action) =>
		CreateTimer(delay).Timeout += () =>
		{
			// 单步异常不能把整个流程挂死
			try
			{
				action();
			}
			catch (System.Exception ex)
			{
				Check(false, $"流程异常：{ex.Message}");
				Finish();
			}
		};

	private async void Shoot(string name)
	{
		if (_headless) return;

		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = Root.GetTexture().GetImage();
		image.SavePng($"res://tools/out/{name}");
		_shots++;
		GD.Print($"[SHOT] {name}");
	}

	private void Check(bool ok, string what)
	{
		if (ok)
		{
			GD.Print($"[SELFCHECK] 通过：{what}");
			return;
		}

		_failures++;
		GD.PrintErr($"[SELFCHECK] 失败：{what}");
	}

	private void Finish()
	{
		if (_finished) return;
		_finished = true;

		RestoreFile(SettingsPath, _hadSettings, _settingsBackup);
		RestoreFile(AgentPath, _hadAgent, _agentBackup);

		if (!_headless) GD.Print($"[SHOT] 共输出 {_shots} 张截图");
		GD.Print(_failures == 0
			? "=== 开始界面自检全部通过 ==="
			: $"=== 开始界面自检失败 {_failures} 项 ===");
		Quit(_failures == 0 ? 0 : 1);
	}

	/// <summary>还原自检前的用户数据（原本没有就删掉）</summary>
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
		if (error != Error.Ok) GD.PrintErr($"[SELFCHECK] 清理失败：{path}（{error}）");
	}
}
