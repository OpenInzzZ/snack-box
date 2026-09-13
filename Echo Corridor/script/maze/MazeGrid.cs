using Godot;
using System;
using System.Collections.Generic;

/// <summary>迷宫外形</summary>
public enum MazeShape
{
	Square = 0,
	Circle = 1,
	Ring = 2,
	Diamond = 3,
	Cross = 4,
	Triangle = 5,
}

/// <summary>
/// 迷宫数据模型：每个格子记录四个方向是否打通，附递归回溯生成与最远出口求解。
/// 圆形/圆环通过在方形网格上裁掉范围外的格子实现，格子与墙仍是网格结构，
/// 因此碰撞、遮挡、声波射线都能直接复用。
/// 纯逻辑实现，不依赖场景树，可独立测试。
/// </summary>
public class MazeGrid
{
	public const int North = 1;
	public const int East = 2;
	public const int South = 4;
	public const int West = 8;

	public static readonly int[] Directions = { North, East, South, West };

	/// <summary>每局随机外形时从这里挑</summary>
	public static readonly MazeShape[] AllShapes =
	{
		MazeShape.Square,
		MazeShape.Circle,
		MazeShape.Ring,
		MazeShape.Diamond,
		MazeShape.Cross,
		MazeShape.Triangle,
	};

	/// <summary>外形也由种子决定，这样"同一个种子 = 同一张图"</summary>
	public static MazeShape ShapeForSeed(int seed) => AllShapes[Mathf.Abs(seed) % AllShapes.Length];

	public int Width { get; }
	public int Height { get; }
	public MazeShape Shape { get; }

	/// <summary>落在形状内的格子总数（形状外的格子不参与生成）</summary>
	public int ActiveCount { get; }

	public Vector2I Start { get; }

	/// <summary>距起点最远的格子，作为迷宫出口</summary>
	public Vector2I Exit { get; }

	private readonly int[,] _open;
	private readonly bool[,] _active;

	public MazeGrid(int width, int height, MazeShape shape, int seed)
	{
		Width = width;
		Height = height;
		Shape = shape;
		_open = new int[width, height];
		_active = BuildMask(width, height, shape);

		foreach (bool active in _active)
		{
			if (active) ActiveCount++;
		}

		Start = FindFirstActive();
		Generate(seed);
		Exit = FindFarthestCell(Start);
	}

	public static string ShapeName(MazeShape shape) => shape switch
	{
		MazeShape.Circle => "圆形",
		MazeShape.Ring => "圆环",
		MazeShape.Diamond => "菱形",
		MazeShape.Cross => "十字",
		MazeShape.Triangle => "三角",
		_ => "方形",
	};

	/// <summary>该格是否在迷宫范围内（圆形/圆环会裁掉范围外的格子）</summary>
	public bool IsActive(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && _active[x, y];

	public bool IsInside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

	/// <summary>指定方向是否可通行（越界或对面不在形状内都算不可通行）</summary>
	public bool IsOpen(int x, int y, int dir)
	{
		if (!IsActive(x, y)) return false;

		Vector2I off = Offset(dir);
		return IsActive(x + off.X, y + off.Y) && (_open[x, y] & dir) != 0;
	}

	/// <summary>指定方向是否有墙（越界或对面不在形状内都算有墙）</summary>
	public bool HasWall(int x, int y, int dir) => !IsOpen(x, y, dir);

	public static Vector2I Offset(int dir) => dir switch
	{
		North => new Vector2I(0, -1),
		East => new Vector2I(1, 0),
		South => new Vector2I(0, 1),
		_ => new Vector2I(-1, 0),
	};

	public static int Opposite(int dir) => dir switch
	{
		North => South,
		East => West,
		South => North,
		_ => East,
	};

	/// <summary>按格子中心与网格中心的相对位置裁形，形状外的格子不参与迷宫生成</summary>
	private static bool[,] BuildMask(int width, int height, MazeShape shape)
	{
		var active = new bool[width, height];
		float centerX = (width - 1) * 0.5f;
		float centerY = (height - 1) * 0.5f;
		float outer = Mathf.Min(width, height) * 0.5f;

		for (int x = 0; x < width; x++)
		{
			for (int y = 0; y < height; y++)
			{
				float offsetX = x - centerX;
				float offsetY = y - centerY;
				float dx = Mathf.Abs(offsetX);
				float dy = Mathf.Abs(offsetY);
				float radius = Mathf.Sqrt(offsetX * offsetX + offsetY * offsetY);

				active[x, y] = shape switch
				{
					MazeShape.Circle => radius <= outer,
					MazeShape.Ring => radius <= outer && radius >= outer * 0.5f,
					// 菱形：曼哈顿距离等距线
					MazeShape.Diamond => dx + dy <= outer,
					// 十字：横竖两条带子的并集
					MazeShape.Cross => dx <= outer / 3f || dy <= outer / 3f,
					// 三角：顶点在上，半宽随高度线性张开
					MazeShape.Triangle => dx <= (offsetY + outer) * 0.5f,
					_ => true,
				};
			}
		}

		return active;
	}

	private Vector2I FindFirstActive()
	{
		for (int y = 0; y < Height; y++)
		{
			for (int x = 0; x < Width; x++)
			{
				if (_active[x, y]) return new Vector2I(x, y);
			}
		}

		return Vector2I.Zero;
	}

	/// <summary>递归回溯（深度优先）挖墙，生成无环的完美迷宫，只在形状内的格子间挖</summary>
	private void Generate(int seed)
	{
		var rng = new Random(seed);
		var visited = new bool[Width, Height];
		var stack = new Stack<Vector2I>();

		visited[Start.X, Start.Y] = true;
		stack.Push(Start);

		var candidates = new List<int>(4);
		while (stack.Count > 0)
		{
			Vector2I cur = stack.Peek();

			candidates.Clear();
			foreach (int dir in Directions)
			{
				Vector2I off = Offset(dir);
				int nx = cur.X + off.X;
				int ny = cur.Y + off.Y;
				if (IsActive(nx, ny) && !visited[nx, ny]) candidates.Add(dir);
			}

			if (candidates.Count == 0)
			{
				stack.Pop();
				continue;
			}

			int pick = candidates[rng.Next(candidates.Count)];
			Vector2I step = Offset(pick);
			var next = new Vector2I(cur.X + step.X, cur.Y + step.Y);

			_open[cur.X, cur.Y] |= pick;
			_open[next.X, next.Y] |= Opposite(pick);

			visited[next.X, next.Y] = true;
			stack.Push(next);
		}
	}

	/// <summary>BFS 求距起点最远的格子，保证出口需要走最长的路</summary>
	private Vector2I FindFarthestCell(Vector2I from)
	{
		var dist = new int[Width, Height];
		for (int x = 0; x < Width; x++)
		{
			for (int y = 0; y < Height; y++) dist[x, y] = -1;
		}

		var queue = new Queue<Vector2I>();
		dist[from.X, from.Y] = 0;
		queue.Enqueue(from);

		Vector2I farthest = from;
		int best = 0;

		while (queue.Count > 0)
		{
			Vector2I cur = queue.Dequeue();
			int d = dist[cur.X, cur.Y];
			if (d > best)
			{
				best = d;
				farthest = cur;
			}

			foreach (int dir in Directions)
			{
				if (!IsOpen(cur.X, cur.Y, dir)) continue;

				Vector2I off = Offset(dir);
				int nx = cur.X + off.X;
				int ny = cur.Y + off.Y;
				if (dist[nx, ny] >= 0) continue;

				dist[nx, ny] = d + 1;
				queue.Enqueue(new Vector2I(nx, ny));
			}
		}

		return farthest;
	}
}
