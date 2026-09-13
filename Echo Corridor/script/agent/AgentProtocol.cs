using Godot;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Agent 协议里共用的小数据结构与文本：感知（玩家看得到的信息）、模型要调的工具、
/// 以及给模型看的提示词。放在一处，保证"演示脑"和真模型看到的是同一份信息。
/// </summary>
public static class AgentProtocol
{
	/// <summary>方向：与迷宫里的北东南西一致</summary>
	public static readonly (string Key, string Name, Vector2I Delta)[] Directions =
	{
		("north", "北", new Vector2I(0, -1)),
		("east", "东", new Vector2I(1, 0)),
		("south", "南", new Vector2I(0, 1)),
		("west", "西", new Vector2I(-1, 0)),
	};

	/// <summary>一次感知：只包含玩家本来看得到的东西</summary>
	public sealed class Perception
	{
		public string Difficulty = "";
		public string Shape = "";
		public int GridWidth;
		public int GridHeight;
		public Vector2I PlayerCell;
		public float Elapsed;
		public int Moves;
		public bool Won;

		/// <summary>自己所在格的四道墙是否存在（北东南西），一定都是已知的</summary>
		public readonly bool[] CellWalls = new bool[4];

		/// <summary>本步声波照到的墙，形如 "格(12,7)的北墙"</summary>
		public readonly List<string> KnownWalls = new();

		/// <summary>自己格子往某方向是否走得通</summary>
		public bool IsOpen(int direction) => !CellWalls[direction];

		/// <summary>把自己格子的四道墙也在列表里标出来（名字与列表一致，便于模型对照）</summary>
		public IEnumerable<string> SelfWalls()
		{
			for (int i = 0; i < Directions.Length; i++)
			{
				if (CellWalls[i]) yield return $"格({PlayerCell.X},{PlayerCell.Y})的{Directions[i].Name}墙";
			}
		}
	}

	/// <summary>模型请求调用一次工具</summary>
	public sealed class ToolCall
	{
		public string Id = "";
		public string Name = "";
		public string Arguments = "";
	}

	/// <summary>模型一次回复</summary>
	public sealed class Reply
	{
		public string Content = "";
		public string Reasoning = "";
		public readonly List<ToolCall> Calls = new();
		public string Error = "";
	}

	/// <summary>暴露给模型的工具（与外部 MCP 服务里的同名工具语义一致）</summary>
	public static readonly (string Name, string Description, string Parameters)[] Tools =
	{
		("echo_state",
			"重新查看当前感知信息：所在格子、用时、以及声波当前照亮的墙。信息与每步开头给出的相同。",
			"{\"type\":\"object\",\"properties\":{}}"),

		("echo_move",
			"朝一个方向走一格。撞墙时角色不会动，看返回的格子坐标就知道有没有走成。",
			"{\"type\":\"object\",\"properties\":{\"direction\":{\"type\":\"string\",\"enum\":[\"north\",\"south\",\"east\",\"west\"],\"description\":\"方向：北/南/东/西\"}},\"required\":[\"direction\"]}"),
	};

	public const string SystemPrompt =
		"你在玩一个「声波迷宫」。规则：地图全黑，只有你移动时发出的声波会短暂照亮附近的墙壁，之后墙壁会重新变黑。\n" +
		"你看不到整张地图，只能拿到每次移动后的感知信息。目标：走到出口。出口不会主动显示，只有被声波照到才会显形。\n" +
		"重要：你自己所在格子的四面墙一定都在「已探明」列表里，没列出来的方向就是通的。\n" +
		"请每次只调用一次工具来行动，不要只输出文字。";

	/// <summary>把感知写成模型好读的一段话</summary>
	public static string Describe(Perception perception)
	{
		var text = new StringBuilder();
		text.AppendLine($"难度 {perception.Difficulty}，外形 {perception.Shape}，迷宫 {perception.GridWidth}x{perception.GridHeight}；" +
			$"你在第 ({perception.PlayerCell.X},{perception.PlayerCell.Y}) 格，用时 {perception.Elapsed:0.0} 秒，已走 {perception.Moves} 步。");

		if (perception.Won) text.AppendLine("你已经抵达出口！");

		text.Append("你所在格子的四面墙：");
		for (int i = 0; i < Directions.Length; i++)
		{
			text.Append($"{Directions[i].Name}墙{(perception.CellWalls[i] ? "有" : "无")}");
			if (i < Directions.Length - 1) text.Append('、');
		}

		text.AppendLine("。");
		text.AppendLine($"可以走的方向：{string.Join(' ', OpenDirections(perception))}");

		var walls = new List<string>(perception.SelfWalls());
		walls.AddRange(perception.KnownWalls);
		text.AppendLine($"本步声波照到的墙（{walls.Count} 段）：{(walls.Count > 0 ? string.Join('、', walls) : "无")}");
		text.Append("注意：没列出来的墙不代表没有，只代表这一步没被声波照到。");

		return text.ToString();
	}

	/// <summary>可走方向的中文名列表</summary>
	public static List<string> OpenDirections(Perception perception)
	{
		var open = new List<string>();
		for (int i = 0; i < Directions.Length; i++)
		{
			if (perception.IsOpen(i)) open.Add(Directions[i].Name);
		}

		return open;
	}

	/// <summary>把方向名（模型给的英文/中文都认）翻成索引，认不出返回 -1</summary>
	public static int DirectionIndex(string name)
	{
		string value = (name ?? "").Trim().ToLowerInvariant();

		for (int i = 0; i < Directions.Length; i++)
		{
			if (value == Directions[i].Key || value == Directions[i].Name) return i;
		}

		return value switch
		{
			"n" or "up" => 0,
			"e" or "right" => 1,
			"s" or "down" => 2,
			"w" or "left" => 3,
			_ => -1,
		};
	}

	/// <summary>把一段墙（格坐标下的中心线两端点）换算成"某格某面墙"的描述，便于模型理解</summary>
	public static string WallName(Vector2 gridA, Vector2 gridB, int gridWidth, int gridHeight)
	{
		if (Mathf.Abs(gridA.Y - gridB.Y) < 0.01f)
		{
			// 横墙：位于某格的上边界或下边界
			int x = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(gridA.X, gridB.X) + 0.5f), 0, gridWidth - 1);
			int boundary = Mathf.RoundToInt(gridA.Y);
			return boundary >= 0 && boundary < gridHeight
				? $"格({x},{boundary})的北墙"
				: $"格({x},{boundary - 1})的南墙";
		}

		int y = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(gridA.Y, gridB.Y) + 0.5f), 0, gridHeight - 1);
		int verticalBoundary = Mathf.RoundToInt(gridA.X);
		return verticalBoundary >= 0 && verticalBoundary < gridWidth
			? $"格({verticalBoundary},{y})的西墙"
			: $"格({verticalBoundary - 1},{y})的东墙";
	}
}
