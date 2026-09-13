using Godot;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// 通关用时排行榜：按难度各存一份，加密放在 user:// 下（每行一个秒数），只保留最快的前 MaxEntries 名。
/// 难度会影响迷宫尺寸与声波范围，成绩不能跨难度比较，所以必须分开记录。
/// 底层读写按路径参数化，自检时可指向临时文件而不碰真实成绩。
/// </summary>
/// <remarks>
/// 用的是 Godot 自带的 AES 加密文件（OpenEncryptedWithPass）。口令硬编码在程序里、
/// 会随可执行文件一起分发，所以这是防手改的混淆，不是真正的安全——单机本地榜做不了
/// 更强的保证，真想防作弊只能把成绩交给服务器校验。
/// </remarks>
public static class Leaderboard
{
	public const int MaxEntries = 8;

	private const string Passphrase = "echo-corridor/sonar-maze:v1";

	/// <summary>每个难度一份榜单文件</summary>
	public static string PathFor(MazeDifficulty difficulty) => $"user://leaderboard_{(int)difficulty}.dat";

	/// <summary>读取某个难度的成绩，按用时从快到慢排序</summary>
	public static List<float> Load(MazeDifficulty difficulty) => LoadFrom(PathFor(difficulty));

	/// <summary>记入一次成绩并落盘，返回名次（从 1 开始）；没进榜则返回 0 且不写文件</summary>
	public static int Record(float seconds, MazeDifficulty difficulty) => Record(seconds, PathFor(difficulty));

	/// <summary>读取指定文件里的成绩，按用时从快到慢排序</summary>
	public static List<float> LoadFrom(string path)
	{
		var times = new List<float>();
		if (!FileAccess.FileExists(path)) return times;

		using FileAccess file = FileAccess.OpenEncryptedWithPass(path, FileAccess.ModeFlags.Read, Passphrase);
		if (file == null)
		{
			// 口令不符或文件被改坏时按空榜处理，不让一条坏数据导致进不了游戏
			GD.PushWarning($"排行榜读取失败（按空榜处理）：{FileAccess.GetOpenError()}");
			return times;
		}

		while (!file.EofReached())
		{
			string line = file.GetLine().Trim();
			if (float.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds) && seconds > 0f)
				times.Add(seconds);
		}

		times.Sort();
		return times;
	}

	/// <summary>记入一次成绩到指定文件，返回名次（从 1 开始）；没进榜则返回 0 且不写文件</summary>
	public static int Record(float seconds, string path)
	{
		List<float> times = LoadFrom(path);
		times.Add(seconds);
		times.Sort();

		int rank = times.IndexOf(seconds) + 1;
		if (rank > MaxEntries) return 0;

		if (times.Count > MaxEntries) times.RemoveRange(MaxEntries, times.Count - MaxEntries);
		return Save(times, path) ? rank : 0;
	}

	/// <summary>新上榜成绩的显示颜色（绿色）</summary>
	private const string NewEntryColor = "#5cd75c";

	/// <summary>开始界面用的纯文本榜单</summary>
	public static string Format(MazeDifficulty difficulty) => FormatText(difficulty, 0, bbcode: false);

	/// <summary>通关结算用的榜单（BBCode）：本次成绩那一行标绿并加 *新* 标记</summary>
	public static string FormatResult(MazeDifficulty difficulty, int rank) => FormatText(difficulty, rank, bbcode: true);

	private static string FormatText(MazeDifficulty difficulty, int rank, bool bbcode)
	{
		var lines = new List<string>
		{
			$"排行榜 · {GameSettings.DisplayName(difficulty)}（最快 {MaxEntries} 名）",
		};

		List<float> times = Load(difficulty);
		if (times.Count == 0) lines.Add("暂无成绩");

		for (int i = 0; i < times.Count; i++)
		{
			string row = $"{i + 1}. {times[i]:0.00} 秒";
			if (i + 1 != rank)
			{
				lines.Add(row);
				continue;
			}

			// 只影响这次结算的显示，不会写进成绩文件
			lines.Add(bbcode ? $"[color={NewEntryColor}]{row}　*新*[/color]" : row);
		}

		return string.Join("\n", lines);
	}

	private static bool Save(List<float> times, string path)
	{
		using FileAccess file = FileAccess.OpenEncryptedWithPass(path, FileAccess.ModeFlags.Write, Passphrase);
		if (file == null)
		{
			GD.PushWarning($"排行榜写入失败：{FileAccess.GetOpenError()}");
			return false;
		}

		foreach (float seconds in times) file.StoreLine(seconds.ToString(CultureInfo.InvariantCulture));
		return true;
	}
}
