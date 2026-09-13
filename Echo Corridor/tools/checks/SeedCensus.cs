using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// 一次性统计：把种子空间枚举一遍，数一数真正不同的地图有多少张（顺带查有没有撞图）。
/// 图用 64 位指纹压缩，100 万量级下指纹碰撞概率约 1e-8，可忽略。
/// 用法：Godot_console.exe --headless --path . --script res://tools/checks/SeedCensus.cs [low|medium|high] [样本数]
/// </summary>
public partial class SeedCensus : SceneTree
{
	public override void _Initialize()
	{
		string[] args = OS.GetCmdlineUserArgs();
		MazeDifficulty difficulty = args.Length > 0 ? Parse(args[0]) : MazeDifficulty.Low;
		int limit = args.Length > 1 && int.TryParse(args[1], out int count) ? count : GameSettings.MaxSeed;

		DifficultyProfile profile = GameSettings.Describe(difficulty);
		GD.Print($"[CENSUS] {profile.Name} 难度（{profile.GridWidth}x{profile.GridHeight}）枚举种子 1~{limit}");

		var seen = new Dictionary<ulong, int>();
		int duplicates = 0;
		ulong started = Time.GetTicksUsec();

		for (int seed = 1; seed <= limit; seed++)
		{
			var grid = new MazeGrid(profile.GridWidth, profile.GridHeight, MazeGrid.ShapeForSeed(seed), seed);
			ulong fingerprint = Fingerprint(grid);

			if (seen.TryGetValue(fingerprint, out int twin))
			{
				duplicates++;
				GD.Print($"[CENSUS] 撞图：seed {twin} 与 seed {seed} " +
					$"（外形 {MazeGrid.ShapeName(grid.Shape)}，起点 {grid.Start}，出口 {grid.Exit}，{grid.ActiveCount} 格）");
				continue;
			}

			seen[fingerprint] = seed;
		}

		float seconds = (Time.GetTicksUsec() - started) / 1_000_000f;

		GD.Print($"[CENSUS] 种子 {limit} 个 → 不同地图 {seen.Count} 张，撞图 {duplicates} 张");
		GD.Print($"[CENSUS] 耗时 {seconds:0.0} 秒（{seconds * 1000f / limit:0.00} ms/张）");
		GD.Print($"[CENSUS] 换种子必换图：{(duplicates == 0 ? "成立" : "不成立")}");

		Quit(0);
	}

	/// <summary>FNV-1a 64 位：把外形与每格可通行方向压成一个数</summary>
	private static ulong Fingerprint(MazeGrid grid)
	{
		ulong hash = 14695981039346656037UL;

		void Mix(ulong value)
		{
			hash ^= value;
			hash *= 1099511628211UL;
		}

		Mix((ulong)grid.Shape);
		Mix((ulong)grid.Width);
		Mix((ulong)grid.Height);

		for (int x = 0; x < grid.Width; x++)
		{
			for (int y = 0; y < grid.Height; y++)
			{
				ulong mask = 7;   // 7 = 形状外的标记，与任何真实掩码都不同
				if (grid.IsActive(x, y))
				{
					mask = 0;
					if (grid.IsOpen(x, y, MazeGrid.North)) mask |= 1;
					if (grid.IsOpen(x, y, MazeGrid.East)) mask |= 2;
					if (grid.IsOpen(x, y, MazeGrid.South)) mask |= 4;
					if (grid.IsOpen(x, y, MazeGrid.West)) mask |= 8;
				}

				Mix((ulong)x);
				Mix((ulong)y);
				Mix(mask);
			}
		}

		return hash;
	}

	private static MazeDifficulty Parse(string name) => name.Trim().ToLowerInvariant() switch
	{
		"low" => MazeDifficulty.Low,
		"high" => MazeDifficulty.High,
		_ => MazeDifficulty.Medium,
	};
}
