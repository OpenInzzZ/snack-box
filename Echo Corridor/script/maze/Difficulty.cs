using Godot;

/// <summary>迷宫难度</summary>
public enum MazeDifficulty
{
	Low = 0,
	Medium = 1,
	High = 2,
}

/// <summary>一个难度对应的迷宫参数</summary>
public readonly struct DifficultyProfile
{
	public string Name { get; init; }
	public int GridWidth { get; init; }
	public int GridHeight { get; init; }

	/// <summary>声波最大半径（格）：越大看得越远</summary>
	public float WaveRevealCells { get; init; }

	/// <summary>墙壁被点亮后由白衰减到黑所需秒数：越久越轻松</summary>
	public float WallFadeSeconds { get; init; }
}

/// <summary>
/// 难度配置与当前选择：开始界面写入，迷宫场景读取。
/// 切换场景没法传参，所以用一个静态字段在两者之间传递。
/// </summary>
public static class GameSettings
{
	public const MazeDifficulty Default = MazeDifficulty.Medium;

	public static MazeDifficulty Difficulty { get; set; } = Default;

	/// <summary>
	/// 本局使用的随机种子；0 表示每局随机。
	/// 非 0 时种子决定整张地图（含外形），换到同一个种子的玩家会玩到同一张图。
	/// </summary>
	public static int Seed { get; set; }

	/// <summary>随机种子的取值范围（留出 0 给"随机"）</summary>
	public const int MaxSeed = 999999;

	/// <summary>本局是否交给 Agent 模式（开始界面选择，进迷宫时读取）</summary>
	public static bool AgentPlays { get; set; }

	public static DifficultyProfile Profile => Describe(Difficulty);

	public static string DisplayName(MazeDifficulty difficulty) => Describe(difficulty).Name;

	/// <summary>
	/// 三档难度：迷宫越小、声波传得越远、墙面停留越久，就越容易。
	/// 格宽是固定的，视图跟随角色滚动，所以地图尺寸只受生成耗时和可行走距离限制。
	/// </summary>
	public static DifficultyProfile Describe(MazeDifficulty difficulty) => difficulty switch
	{
		MazeDifficulty.Low => new DifficultyProfile
		{
			Name = "低",
			GridWidth = 33,
			GridHeight = 33,
			WaveRevealCells = 7f,
			WallFadeSeconds = 3.5f,
		},
		MazeDifficulty.High => new DifficultyProfile
		{
			Name = "高",
			GridWidth = 63,
			GridHeight = 63,
			WaveRevealCells = 3.5f,
			WallFadeSeconds = 1.8f,
		},
		_ => new DifficultyProfile
		{
			Name = "中",
			GridWidth = 45,
			GridHeight = 45,
			WaveRevealCells = 5f,
			WallFadeSeconds = 2.5f,
		},
	};
}
