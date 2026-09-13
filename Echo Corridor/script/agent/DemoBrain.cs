using Godot;
using System.Collections.Generic;

/// <summary>
/// 本地演示脑：不联网，只凭"感知"做深度优先探索去找出口。
/// 它跟真模型拿到的是同一份信息（自己格子的四面墙 + 本步照到的墙），
/// 所以它既能让你零配置先看效果，也能顺带证明"只凭声波确实能摸到出口"。
/// </summary>
public sealed class DemoBrain
{
	private readonly Dictionary<Vector2I, bool[]> _known = new();
	private readonly List<Vector2I> _path = new();
	private readonly HashSet<Vector2I> _visited = new();

	public AgentProtocol.Reply Respond(AgentProtocol.Perception perception)
	{
		Vector2I cell = perception.PlayerCell;

		_known[cell] = (bool[])perception.CellWalls.Clone();
		_visited.Add(cell);

		if (_path.Count == 0 || _path[^1] != cell) _path.Add(cell);

		// 先往没走过的可通方向钻
		for (int i = 0; i < AgentProtocol.Directions.Length; i++)
		{
			if (perception.CellWalls[i]) continue;

			Vector2I next = cell + AgentProtocol.Directions[i].Delta;
			if (_visited.Contains(next)) continue;

			return Move(AgentProtocol.Directions[i].Key, $"往{AgentProtocol.Directions[i].Name}走，这条还没探过。");
		}

		// 死路：沿来路退回一格
		if (_path.Count >= 2)
		{
			Vector2I back = _path[^2];
			_path.RemoveAt(_path.Count - 1);

			foreach ((string key, string name, Vector2I delta) in AgentProtocol.Directions)
			{
				if (cell + delta == back) return Move(key, $"这格探完了，退回{name}。");
			}
		}

		return new AgentProtocol.Reply { Content = "没有可走的方向了。" };
	}

	private static AgentProtocol.Reply Move(string direction, string thought)
	{
		var reply = new AgentProtocol.Reply { Content = thought };
		reply.Calls.Add(new AgentProtocol.ToolCall
		{
			Id = $"demo-{direction}-{GD.Randi()}",
			Name = "echo_move",
			Arguments = $"{{\"direction\":\"{direction}\"}}",
		});

		return reply;
	}
}
