using Godot;
using System.Collections.Generic;

/// <summary>一套模型配置</summary>
public sealed class AgentProfile
{
	public string Name = "";
	public string BaseUrl = "";
	public string Model = "";
	public string ApiKey = "";

	public AgentProfile Copy() => new() { Name = Name, BaseUrl = BaseUrl, Model = Model, ApiKey = ApiKey };
}

/// <summary>
/// Agent 模式的模型库与当前选择，加密存在本地。
/// 玩家可以保存多套模型（不同服务商 / 不同 key）随时切换。
///
/// 口令硬编码在程序里，所以这是"防手翻"级别的混淆，不是真正的安全；
/// 存的是玩家自己的 key，风险由玩家自行判断。
/// </summary>
public static class AgentStore
{
	/// <summary>内置演示模型：不联网，用本地脚本化走位，方便零配置先看效果</summary>
	public const string DemoBaseUrl = "mock://demo";

	private const string SavePath = "user://agent.dat";
	private const string Passphrase = "echo-corridor/agent:v1";

	/// <summary>接口格式</summary>
	public enum ApiFormat
	{
		/// <summary>按地址自动判断：路径里有 /responses 用 Responses，否则用 Chat Completions</summary>
		Auto = 0,
		ChatCompletions = 1,
		Responses = 2,
	}

	public static readonly (string Label, ApiFormat Format)[] Formats =
	{
		("自动识别", ApiFormat.Auto),
		("Chat Completions", ApiFormat.ChatCompletions),
		("Responses", ApiFormat.Responses),
	};

	private static readonly List<AgentProfile> Profiles = new();

	/// <summary>当前选中的模型下标</summary>
	public static int Selected { get; private set; }

	public static ApiFormat Format { get; set; } = ApiFormat.Auto;

	/// <summary>每步之间的停顿（秒）。默认 0：靠走一格本身的时间分隔，不再额外等待</summary>
	public static float StepDelay { get; set; }

	public static int Count => Profiles.Count;

	public static IReadOnlyList<AgentProfile> All => Profiles;

	public static AgentProfile Current =>
		Profiles.Count > 0 ? Profiles[Mathf.Clamp(Selected, 0, Profiles.Count - 1)] : BuiltInDemo();

	/// <summary>当前选的是不是内置演示（不联网、不花钱）</summary>
	public static bool IsDemo => Current.BaseUrl.StartsWith("mock://");

	/// <summary>是否可以开跑（演示模式不要求 key）</summary>
	public static bool IsReady => Current.BaseUrl.Length > 0 && Current.Model.Length > 0 && (IsDemo || Current.ApiKey.Length > 0);

	/// <summary>实际使用的接口格式：自动时按地址路径判断</summary>
	public static ApiFormat EffectiveFormat =>
		Format != ApiFormat.Auto
			? Format
			: Current.BaseUrl.Contains("/responses", System.StringComparison.OrdinalIgnoreCase)
				? ApiFormat.Responses
				: ApiFormat.ChatCompletions;

	/// <summary>请求地址：玩家填的是 base（如 https://api.openai.com/v1），这里补上具体路径</summary>
	public static string RequestUrl =>
		Current.BaseUrl.TrimEnd('/') + (EffectiveFormat == ApiFormat.Responses ? "/responses" : "/chat/completions");

	public static string FormatLabel(ApiFormat format) => Formats[(int)format].Label;

	public static AgentProfile At(int index) => Profiles[Mathf.Clamp(index, 0, Profiles.Count - 1)];

	public static void Select(int index) => Selected = Mathf.Clamp(index, 0, Mathf.Max(0, Profiles.Count - 1));

	public static int Add(AgentProfile profile)
	{
		Profiles.Add(profile);
		return Profiles.Count - 1;
	}

	public static void Update(int index, AgentProfile profile)
	{
		if (index < 0 || index >= Profiles.Count) return;

		Profiles[index] = profile;
	}

	/// <summary>删除一套模型；删空了会补回内置演示，保证列表里始终有东西</summary>
	public static void Remove(int index)
	{
		if (index < 0 || index >= Profiles.Count) return;

		Profiles.RemoveAt(index);

		if (Profiles.Count == 0) Profiles.Add(BuiltInDemo());

		Select(Mathf.Min(Selected, Profiles.Count - 1));
	}

	public static AgentProfile BuiltInDemo() => new()
	{
		Name = "演示（内置）",
		BaseUrl = DemoBaseUrl,
		Model = "demo",
		ApiKey = "",
	};

	public static void Load()
	{
		Profiles.Clear();
		Selected = 0;
		Format = ApiFormat.Auto;
		StepDelay = 0f;

		if (!FileAccess.FileExists(SavePath))
		{
			Profiles.Add(BuiltInDemo());
			return;
		}

		using FileAccess file = FileAccess.OpenEncryptedWithPass(SavePath, FileAccess.ModeFlags.Read, Passphrase);
		if (file == null)
		{
			GD.PushWarning($"Agent 模型库读取失败（按默认处理）：{FileAccess.GetOpenError()}");
			Profiles.Add(BuiltInDemo());
			return;
		}

		Variant parsed = Json.ParseString(file.GetAsText());
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			Profiles.Add(BuiltInDemo());
			return;
		}

		Godot.Collections.Dictionary data = parsed.AsGodotDictionary();
		Format = (ApiFormat)Mathf.Clamp(
			data.TryGetValue("format", out Variant format) ? format.AsInt32() : 0, 0, Formats.Length - 1);

		if (data.TryGetValue("profiles", out Variant list) && list.VariantType == Variant.Type.Array)
		{
			foreach (Variant item in list.AsGodotArray())
			{
				if (item.VariantType != Variant.Type.Dictionary) continue;

				Godot.Collections.Dictionary node = item.AsGodotDictionary();
				Profiles.Add(new AgentProfile
				{
					Name = Read(node, "name", "未命名"),
					BaseUrl = Read(node, "base_url", ""),
					Model = Read(node, "model", ""),
					ApiKey = Read(node, "api_key", ""),
				});
			}
		}

		if (Profiles.Count == 0) Profiles.Add(BuiltInDemo());

		Selected = Mathf.Clamp(data.TryGetValue("selected", out Variant chosen) ? chosen.AsInt32() : 0, 0, Profiles.Count - 1);
	}

	public static void Save()
	{
		var list = new Godot.Collections.Array();
		foreach (AgentProfile profile in Profiles)
		{
			list.Add(new Godot.Collections.Dictionary
			{
				{ "name", profile.Name },
				{ "base_url", profile.BaseUrl },
				{ "model", profile.Model },
				{ "api_key", profile.ApiKey },
			});
		}

		var data = new Godot.Collections.Dictionary
		{
			{ "selected", Selected },
			{ "format", (int)Format },
			{ "profiles", list },
		};

		using FileAccess file = FileAccess.OpenEncryptedWithPass(SavePath, FileAccess.ModeFlags.Write, Passphrase);
		if (file == null)
		{
			GD.PushWarning($"Agent 模型库写入失败：{FileAccess.GetOpenError()}");
			return;
		}

		file.StoreString(Json.Stringify(data));
	}

	private static string Read(Godot.Collections.Dictionary data, string key, string fallback) =>
		data.TryGetValue(key, out Variant value) ? value.AsString() : fallback;
}
