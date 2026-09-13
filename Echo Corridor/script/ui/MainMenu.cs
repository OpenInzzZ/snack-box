using Godot;
using System.Collections.Generic;

/// <summary>
/// 开始界面：三个页面共用这一个场景，切显隐来切换。
///   页面一「主页面」：开始游戏 / Agent 模式 / 排行榜 / 设置
///   页面二「选择模型」（仅 Agent 流程）：管理多套模型、挑一个
///   页面三「游戏配置」（两条流程共用）：难度 / 种子，（Agent 流程额外显示费用提醒）
/// 用同一场景而不是三个场景，页面之间的选择天然共享，不用来回传状态。
/// </summary>
public partial class MainMenu : Control
{
	private static readonly (string Path, MazeDifficulty Difficulty)[] DifficultyButtons =
	{
		("LowButton", MazeDifficulty.Low),
		("MediumButton", MazeDifficulty.Medium),
		("HighButton", MazeDifficulty.High),
	};

	private Control _home;
	private Control _modelsPage;
	private Control _setupPage;

	private Button _startButton;
	private Button _leaderboardButton;
	private Button _settingsButton;
	private Button _agentButton;
	private Label _boardLabel;
	private VBoxContainer _settingsPanel;
	private OptionButton _modePicker;
	private OptionButton _resolutionPicker;

	private VBoxContainer _modelList;
	private PanelContainer _editPanel;
	private LineEdit _nameInput;
	private LineEdit _urlInput;
	private LineEdit _modelInput;
	private LineEdit _keyInput;
	private OptionButton _formatPicker;
	private Label _formTitle;

	private Label _agentWarning;
	private Label _agentModelLabel;
	private LineEdit _seedInput;

	private int _editingIndex = -1;
	private bool _agentFlow;

	public override void _Ready()
	{
		DisplaySettings.ApplySaved();
		AgentStore.Load();

		_home = GetNode<Control>("Home");
		_modelsPage = GetNode<Control>("ModelsPage");
		_setupPage = GetNode<Control>("SetupPage");

		_startButton = GetNode<Button>("Home/StartButton");
		_agentButton = GetNode<Button>("Home/AgentButton");
		_leaderboardButton = GetNode<Button>("Home/ActionRow/LeaderboardButton");
		_settingsButton = GetNode<Button>("Home/ActionRow/SettingsButton");
		_boardLabel = GetNode<Label>("Home/BoardLabel");
		_settingsPanel = GetNode<VBoxContainer>("Home/SettingsPanel");
		_modePicker = GetNode<OptionButton>("Home/SettingsPanel/ModeRow/ModePicker");
		_resolutionPicker = GetNode<OptionButton>("Home/SettingsPanel/ResolutionRow/ResolutionPicker");

		_modelList = GetNode<VBoxContainer>("ModelsPage/ModelList");
		_editPanel = GetNode<PanelContainer>("EditPanel");
		_formTitle = GetNode<Label>("EditPanel/Form/FormTitle");
		_nameInput = GetNode<LineEdit>("EditPanel/Form/NameRow/NameInput");
		_urlInput = GetNode<LineEdit>("EditPanel/Form/UrlRow/UrlInput");
		_modelInput = GetNode<LineEdit>("EditPanel/Form/ModelRow/ModelInput");
		_keyInput = GetNode<LineEdit>("EditPanel/Form/KeyRow/KeyInput");
		_formatPicker = GetNode<OptionButton>("EditPanel/Form/FormatRow/FormatPicker");

		_agentWarning = GetNode<Label>("SetupPage/AgentWarning");
		_agentModelLabel = GetNode<Label>("SetupPage/AgentModelLabel");
		_seedInput = GetNode<LineEdit>("SetupPage/SeedRow/SeedInput");

		_startButton.Pressed += () => OpenSetup(false);
		_agentButton.Pressed += () => ShowPage(_modelsPage);
		_leaderboardButton.Pressed += ToggleLeaderboard;
		_settingsButton.Pressed += ToggleSettings;

		GetNode<Button>("ModelsPage/TopRow/BackButton").Pressed += () => ShowPage(_home);
		GetNode<Button>("ModelsPage/ListActions/AddButton").Pressed += () => OpenForm(-1);
		GetNode<Button>("ModelsPage/ListActions/EditButton").Pressed += () => OpenForm(AgentStore.Selected);
		GetNode<Button>("ModelsPage/ListActions/RemoveButton").Pressed += RemoveModel;
		GetNode<Button>("ModelsPage/NextButton").Pressed += () => OpenSetup(true);

		GetNode<Button>("EditPanel/Form/FormButtons/SaveButton").Pressed += SaveForm;
		GetNode<Button>("EditPanel/Form/FormButtons/CancelButton").Pressed += () => _editPanel.Visible = false;

		GetNode<Button>("SetupPage/TopRow/BackButton").Pressed += () => ShowPage(_agentFlow ? _modelsPage : _home);
		GetNode<Button>("SetupPage/BeginButton").Pressed += BeginGame;

		_seedInput.TextSubmitted += _ => BeginGame();
		_seedInput.Text = GameSettings.Seed != 0 ? GameSettings.Seed.ToString() : "";

		for (int i = 0; i < DisplaySettings.Resolutions.Length; i++)
		{
			_resolutionPicker.AddItem(DisplaySettings.ResolutionLabel(i), i);
		}

		for (int i = 0; i < DisplaySettings.Modes.Length; i++)
		{
			_modePicker.AddItem(DisplaySettings.ModeLabel(i), i);
		}

		for (int i = 0; i < AgentStore.Formats.Length; i++)
		{
			_formatPicker.AddItem(AgentStore.Formats[i].Label, i);
		}

		RefreshDisplayPickers();
		_modePicker.ItemSelected += OnModeSelected;
		_resolutionPicker.ItemSelected += OnResolutionSelected;

		foreach ((string name, MazeDifficulty difficulty) in DifficultyButtons)
		{
			Button button = GetNode<Button>($"SetupPage/DifficultyRow/{name}");
			button.ButtonPressed = GameSettings.Difficulty == difficulty;
			button.Pressed += () => GameSettings.Difficulty = difficulty;
		}

		ShowPage(_home);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F11 })
		{
			DisplaySettings.ToggleFullscreen();
			RefreshDisplayPickers();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (!@event.IsActionPressed("ui_cancel")) return;

		// Esc：先关表单 / 面板，其次回上一页
		if (_editPanel.Visible) _editPanel.Visible = false;
		else if (_boardLabel.Visible || _settingsPanel.Visible) HidePanels();
		else if (_setupPage.Visible) ShowPage(_agentFlow ? _modelsPage : _home);
		else if (_modelsPage.Visible) ShowPage(_home);

		GetViewport().SetInputAsHandled();
	}

	// ---------- 页面切换 ----------

	private void ShowPage(Control page)
	{
		_home.Visible = page == _home;
		_modelsPage.Visible = page == _modelsPage;
		_setupPage.Visible = page == _setupPage;
		_editPanel.Visible = false;
		HidePanels();

		if (page == _modelsPage) RefreshModelList();
	}

	private void OpenSetup(bool agentFlow)
	{
		_agentFlow = agentFlow;
		_agentWarning.Visible = agentFlow;
		_agentModelLabel.Visible = agentFlow;
		_agentModelLabel.Text = agentFlow ? $"当前模型：{AgentStore.Current.Name}" : "";
		ShowPage(_setupPage);
	}

	private void BeginGame()
	{
		if (_agentFlow && !AgentStore.IsReady)
		{
			_agentModelLabel.Text = "这个模型还缺接口地址或 Key，请先回去补全。";
			return;
		}

		int seed = ParseSeed();
		_seedInput.Text = seed != 0 ? seed.ToString() : "";
		GameSettings.Seed = seed;
		GameSettings.AgentPlays = _agentFlow;
		GetTree().ChangeSceneToFile("res://scenes/maze.tscn");
	}

	private int ParseSeed()
	{
		string text = _seedInput.Text.Trim();
		if (text.Length == 0) return 0;

		return int.TryParse(text, out int seed) && seed > 0 && seed <= GameSettings.MaxSeed ? seed : 0;
	}

	// ---------- 页面一：排行榜 / 设置 ----------

	private void ToggleLeaderboard()
	{
		bool show = !_boardLabel.Visible;
		_settingsPanel.Visible = false;
		_settingsButton.Text = "设置";

		if (show) _boardLabel.Text = Leaderboard.Format(GameSettings.Difficulty);

		_boardLabel.Visible = show;
		_leaderboardButton.Text = show ? "返回" : "排行榜";
	}

	private void ToggleSettings()
	{
		bool show = !_settingsPanel.Visible;
		_boardLabel.Visible = false;
		_leaderboardButton.Text = "排行榜";

		if (show) RefreshDisplayPickers();

		_settingsPanel.Visible = show;
		_settingsButton.Text = show ? "返回" : "设置";
	}

	private void HidePanels()
	{
		_boardLabel.Visible = false;
		_settingsPanel.Visible = false;
		_leaderboardButton.Text = "排行榜";
		_settingsButton.Text = "设置";
	}

	private void RefreshDisplayPickers()
	{
		_modePicker.Selected = DisplaySettings.CurrentMode;
		_resolutionPicker.Selected = DisplaySettings.CurrentIndex;
		_resolutionPicker.Disabled = !DisplaySettings.IsWindowed;
	}

	private void OnModeSelected(long index)
	{
		DisplaySettings.Apply(DisplaySettings.CurrentIndex, (int)index);
		RefreshDisplayPickers();
	}

	private void OnResolutionSelected(long index)
	{
		DisplaySettings.Apply((int)index, DisplaySettings.CurrentMode);
		RefreshDisplayPickers();
	}

	// ---------- 页面二：模型管理 ----------

	private void RefreshModelList()
	{
		foreach (Node child in _modelList.GetChildren())
		{
			_modelList.RemoveChild(child);
			child.QueueFree();
		}

		for (int i = 0; i < AgentStore.Count; i++)
		{
			AgentProfile profile = AgentStore.At(i);
			string mark = i == AgentStore.Selected ? "✓ " : "　";
			string suffix = profile.BaseUrl.StartsWith("mock://") ? "（内置演示，不联网）" : $"{profile.BaseUrl} · {profile.Model}";

			var button = new Button
			{
				Text = $"{mark}{profile.Name}　{suffix}",
				CustomMinimumSize = new Vector2(660, 40),
				ToggleMode = true,
				ButtonPressed = i == AgentStore.Selected,
			};

			int index = i;
			button.Pressed += () =>
			{
				AgentStore.Select(index);
				AgentStore.Save();
				RefreshModelList();
			};

			_modelList.AddChild(button);
		}
	}

	private void OpenForm(int index)
	{
		_editingIndex = index;
		_formTitle.Text = index < 0 ? "新增模型" : $"编辑模型：{AgentStore.At(index).Name}";

		AgentProfile profile = index < 0 ? new AgentProfile { Name = "我的模型" } : AgentStore.At(index).Copy();
		_nameInput.Text = profile.Name;
		_urlInput.Text = profile.BaseUrl;
		_modelInput.Text = profile.Model;
		_keyInput.Text = profile.ApiKey;
		_formatPicker.Selected = (int)AgentStore.Format;

		_editPanel.Visible = true;
	}

	private void SaveForm()
	{
		var profile = new AgentProfile
		{
			Name = _nameInput.Text.Trim().Length > 0 ? _nameInput.Text.Trim() : "未命名",
			BaseUrl = _urlInput.Text.Trim(),
			Model = _modelInput.Text.Trim(),
			ApiKey = _keyInput.Text.Trim(),
		};

		if (_editingIndex < 0)
		{
			AgentStore.Select(AgentStore.Add(profile));
		}
		else
		{
			AgentStore.Update(_editingIndex, profile);
		}

		AgentStore.Format = (AgentStore.ApiFormat)_formatPicker.Selected;
		AgentStore.Save();

		_editPanel.Visible = false;
		RefreshModelList();
	}

	private void RemoveModel()
	{
		AgentStore.Remove(AgentStore.Selected);
		AgentStore.Save();
		RefreshModelList();
	}
}
