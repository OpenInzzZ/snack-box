using Godot;

/// <summary>
/// 显示模式与窗口分辨率的可选档位，以及持久化。
/// 选择写进 user://settings.cfg（普通配置文件即可，这里没有要防篡改的东西），下次启动自动套用。
/// </summary>
public static class DisplaySettings
{
	private const string SavePath = "user://settings.cfg";
	private const string Section = "display";
	private const string ResolutionKey = "resolution";
	private const string ModeKey = "mode";

	/// <summary>可选分辨率，第一项是默认值；只在窗口模式下有意义</summary>
	public static readonly Vector2I[] Resolutions =
	{
		new(1280, 720),
		new(1024, 768),
		new(1600, 900),
		new(1920, 1080),
		new(2560, 1440),
	};

	/// <summary>可选显示模式，第一项是默认值</summary>
	public static readonly (string Label, DisplayServer.WindowMode Mode)[] Modes =
	{
		("窗口", DisplayServer.WindowMode.Windowed),
		("无边框全屏", DisplayServer.WindowMode.Fullscreen),
		("独占全屏", DisplayServer.WindowMode.ExclusiveFullscreen),
	};

	public const int DefaultIndex = 0;
	public const int DefaultMode = 0;

	/// <summary>当前分辨率档位</summary>
	public static int CurrentIndex { get; private set; } = DefaultIndex;

	/// <summary>当前显示模式档位</summary>
	public static int CurrentMode { get; private set; } = DefaultMode;

	/// <summary>窗口模式下分辨率才有意义；全屏时用显示器自己的分辨率</summary>
	public static bool IsWindowed => Modes[CurrentMode].Mode == DisplayServer.WindowMode.Windowed;

	/// <summary>下拉框文案：默认项额外标注一下</summary>
	public static string ResolutionLabel(int index) =>
		$"{Resolutions[index].X} × {Resolutions[index].Y}{(index == DefaultIndex ? "（默认）" : "")}";

	public static string ModeLabel(int index) => Modes[index].Label;

	/// <summary>启动时套用上次选的显示模式与分辨率</summary>
	public static void ApplySaved() =>
		Apply(LoadIndex(ResolutionKey, Resolutions.Length), LoadIndex(ModeKey, Modes.Length), remember: false);

	/// <summary>套用显示模式与分辨率；remember 为 true 时写回配置文件</summary>
	public static void Apply(int resolutionIndex, int modeIndex, bool remember = true)
	{
		CurrentIndex = Mathf.Clamp(resolutionIndex, 0, Resolutions.Length - 1);
		CurrentMode = Mathf.Clamp(modeIndex, 0, Modes.Length - 1);

		// headless（自检 / Agent）没有真实窗口
		if (DisplayServer.GetName() != "headless")
		{
			DisplayServer.WindowSetMode(Modes[CurrentMode].Mode);

			if (IsWindowed)
			{
				Vector2I size = Resolutions[CurrentIndex];
				DisplayServer.WindowSetSize(size);
				DisplayServer.WindowSetPosition(
					(DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen()) - size) / 2);
			}
		}

		if (remember) Save();
	}

	/// <summary>窗口 ⇄ 无边框全屏 的快捷切换（F11 用），并记住</summary>
	public static void ToggleFullscreen() => Apply(CurrentIndex, IsWindowed ? 1 : DefaultMode);

	private static int LoadIndex(string key, int count)
	{
		var config = new ConfigFile();
		if (config.Load(SavePath) != Error.Ok) return 0;

		return Mathf.Clamp(config.GetValue(Section, key, 0).AsInt32(), 0, count - 1);
	}

	private static void Save()
	{
		var config = new ConfigFile();
		config.SetValue(Section, ResolutionKey, CurrentIndex);
		config.SetValue(Section, ModeKey, CurrentMode);
		config.Save(SavePath);
	}
}
