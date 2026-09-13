using Godot;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// 模型客户端：把对话与工具按两种接口格式发出去，再把回复解析成统一结构。
///
/// 支持的格式：
///   Chat Completions（/chat/completions）：工具写成 {"type":"function","function":{...}}，回复在 choices[0].message
///   Responses（/responses）            ：工具是扁平的 {"type":"function","name":...}，回复在 output[] 里
/// 地址填 mock:// 开头时走本地演示脑，不联网。
///
/// 对话历史保存成与格式无关的 turn 列表，每次请求按当前格式重新拼装，
/// 这样切换格式不需要迁移历史，也不会被某一种格式的结构细节绑死。
/// </summary>
public partial class LlmClient : Node
{
	/// <summary>与格式无关的一轮对话</summary>
	private sealed class Turn
	{
		public string Role = "";        // user / assistant / tool
		public string Text = "";
		public string CallId = "";
		public string CallName = "";
		public string CallArguments = "";
	}

	private readonly List<Turn> _turns = new();
	private HttpRequest _http;
	private DemoBrain _demo;

	public override void _Ready()
	{
		_http = new HttpRequest { Timeout = 180 };
		AddChild(_http);
	}

	/// <summary>开一局：清空历史与演示脑</summary>
	public void Reset()
	{
		_turns.Clear();
		_demo = new DemoBrain();
	}

	public void AddUser(string text) => _turns.Add(new Turn { Role = "user", Text = text });

	/// <summary>发送当前历史并取回一次回复；失败时 Reply.Error 非空</summary>
	public async Task<AgentProtocol.Reply> Send(AgentProtocol.Perception perception)
	{
		AgentProtocol.Reply reply = AgentStore.IsDemo ? _demo.Respond(perception) : await SendToApi();

		if (reply.Calls.Count > 0) RecordAssistant(reply, reply.Calls[0]);
		else _turns.Add(new Turn { Role = "assistant", Text = reply.Content });

		return reply;
	}

	public void AddToolResult(string callId, string output) =>
		_turns.Add(new Turn { Role = "tool", CallId = callId, Text = output });

	private void RecordAssistant(AgentProtocol.Reply reply, AgentProtocol.ToolCall call) =>
		_turns.Add(new Turn
		{
			Role = "assistant",
			Text = reply.Content,
			CallId = call.Id,
			CallName = call.Name,
			CallArguments = call.Arguments,
		});

	private async Task<AgentProtocol.Reply> SendToApi()
	{
		string body = AgentStore.EffectiveFormat == AgentStore.ApiFormat.Responses
			? BuildResponsesRequest()
			: BuildChatRequest();

		var headers = new List<string> { "Content-Type: application/json" };
		if (AgentStore.Current.ApiKey.Length > 0) headers.Add($"Authorization: Bearer {AgentStore.Current.ApiKey}");

		Error error = _http.Request(AgentStore.RequestUrl, headers.ToArray(), HttpClient.Method.Post, body);
		if (error != Error.Ok) return Failed($"请求发不出去：{error}");

		Variant[] result = await ToSignal(_http, HttpRequest.SignalName.RequestCompleted);
		long code = result[1].AsInt64();
		string text = Encoding.UTF8.GetString(result[3].AsByteArray());

		if (code != 200)
		{
			// 把服务端返回的错误信息尽量透出来，方便玩家自己排查
			Variant parsedError = Json.ParseString(text);
			string detail = parsedError.VariantType == Variant.Type.Dictionary
				&& parsedError.AsGodotDictionary().TryGetValue("error", out Variant node)
				&& node.VariantType == Variant.Type.Dictionary
				&& node.AsGodotDictionary().TryGetValue("message", out Variant message)
					? message.AsString()
					: text.Trim();

			return Failed($"HTTP {code}：{Trim(detail, 300)}");
		}

		return AgentStore.EffectiveFormat == AgentStore.ApiFormat.Responses
			? ParseResponses(text)
			: ParseChat(text);
	}

	private static AgentProtocol.Reply Failed(string message) => new() { Error = message };

	// ---------- Chat Completions ----------

	private string BuildChatRequest()
	{
		var messages = new Godot.Collections.Array
		{
			new Godot.Collections.Dictionary { { "role", "system" }, { "content", AgentProtocol.SystemPrompt } },
		};

		foreach (Turn turn in _turns)
		{
			switch (turn.Role)
			{
				case "user":
					messages.Add(new Godot.Collections.Dictionary { { "role", "user" }, { "content", turn.Text } });
					break;

				case "assistant":
					var assistant = new Godot.Collections.Dictionary { { "role", "assistant" }, { "content", turn.Text } };
					if (turn.CallId.Length > 0)
					{
						assistant["tool_calls"] = new Godot.Collections.Array
						{
							new Godot.Collections.Dictionary
							{
								{ "id", turn.CallId },
								{ "type", "function" },
								{ "function", new Godot.Collections.Dictionary
									{
										{ "name", turn.CallName },
										{ "arguments", turn.CallArguments },
									} },
							},
						};
					}

					messages.Add(assistant);
					break;

				default:
					messages.Add(new Godot.Collections.Dictionary
					{
						{ "role", "tool" },
						{ "tool_call_id", turn.CallId },
						{ "content", turn.Text },
					});
					break;
			}
		}

		var tools = new Godot.Collections.Array();
		foreach ((string name, string description, string parameters) in AgentProtocol.Tools)
		{
			tools.Add(new Godot.Collections.Dictionary
			{
				{ "type", "function" },
				{ "function", new Godot.Collections.Dictionary
					{
						{ "name", name },
						{ "description", description },
						{ "parameters", Json.ParseString(parameters) },
					} },
			});
		}

		return Json.Stringify(new Godot.Collections.Dictionary
		{
			{ "model", AgentStore.Current.Model },
			{ "messages", messages },
			{ "tools", tools },
			{ "tool_choice", "auto" },
			{ "temperature", 0.2 },
		});
	}

	private static AgentProtocol.Reply ParseChat(string text)
	{
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary) return Failed("回复不是 JSON 对象");

		Godot.Collections.Dictionary root = parsed.AsGodotDictionary();
		if (!root.TryGetValue("choices", out Variant choices) || choices.AsGodotArray().Count == 0)
			return Failed($"回复里没有 choices：{Trim(text, 300)}");

		Variant first = choices.AsGodotArray()[0];
		if (first.VariantType != Variant.Type.Dictionary
			|| !first.AsGodotDictionary().TryGetValue("message", out Variant messageNode)
			|| messageNode.VariantType != Variant.Type.Dictionary)
		{
			return Failed("choices[0] 里没有 message");
		}

		Godot.Collections.Dictionary message = messageNode.AsGodotDictionary();
		var reply = new AgentProtocol.Reply
		{
			Content = message.TryGetValue("content", out Variant content) ? content.AsString() : "",
			Reasoning = FirstNonEmpty(message, "reasoning_content", "reasoning"),
		};

		if (message.TryGetValue("tool_calls", out Variant calls) && calls.VariantType == Variant.Type.Array)
		{
			foreach (Variant item in calls.AsGodotArray())
			{
				if (item.VariantType != Variant.Type.Dictionary) continue;

				Godot.Collections.Dictionary call = item.AsGodotDictionary();
				Godot.Collections.Dictionary function = call.TryGetValue("function", out Variant node) && node.VariantType == Variant.Type.Dictionary
					? node.AsGodotDictionary()
					: new Godot.Collections.Dictionary();

				reply.Calls.Add(new AgentProtocol.ToolCall
				{
					Id = call.TryGetValue("id", out Variant id) ? id.AsString() : "",
					Name = function.TryGetValue("name", out Variant name) ? name.AsString() : "",
					Arguments = function.TryGetValue("arguments", out Variant arguments) ? arguments.AsString() : "",
				});
			}
		}

		return reply;
	}

	// ---------- Responses ----------

	private string BuildResponsesRequest()
	{
		var input = new Godot.Collections.Array();

		foreach (Turn turn in _turns)
		{
			switch (turn.Role)
			{
				case "user":
					input.Add(new Godot.Collections.Dictionary { { "role", "user" }, { "content", turn.Text } });
					break;

				case "assistant":
					if (turn.Text.Length > 0)
						input.Add(new Godot.Collections.Dictionary { { "role", "assistant" }, { "content", turn.Text } });

					if (turn.CallId.Length > 0)
					{
						input.Add(new Godot.Collections.Dictionary
						{
							{ "type", "function_call" },
							{ "call_id", turn.CallId },
							{ "name", turn.CallName },
							{ "arguments", turn.CallArguments },
						});
					}

					break;

				default:
					input.Add(new Godot.Collections.Dictionary
					{
						{ "type", "function_call_output" },
						{ "call_id", turn.CallId },
						{ "output", turn.Text },
					});
					break;
			}
		}

		// Responses 的工具是扁平结构，不像 Chat 那样嵌在 function 里
		var tools = new Godot.Collections.Array();
		foreach ((string name, string description, string parameters) in AgentProtocol.Tools)
		{
			tools.Add(new Godot.Collections.Dictionary
			{
				{ "type", "function" },
				{ "name", name },
				{ "description", description },
				{ "parameters", Json.ParseString(parameters) },
			});
		}

		return Json.Stringify(new Godot.Collections.Dictionary
		{
			{ "model", AgentStore.Current.Model },
			{ "instructions", AgentProtocol.SystemPrompt },
			{ "input", input },
			{ "tools", tools },
			{ "tool_choice", "auto" },
			{ "temperature", 0.2 },
		});
	}

	private static AgentProtocol.Reply ParseResponses(string text)
	{
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary) return Failed("回复不是 JSON 对象");

		Godot.Collections.Dictionary root = parsed.AsGodotDictionary();
		if (!root.TryGetValue("output", out Variant output) || output.VariantType != Variant.Type.Array)
			return Failed($"回复里没有 output：{Trim(text, 300)}");

		var reply = new AgentProtocol.Reply();
		var messageText = new StringBuilder();

		foreach (Variant item in output.AsGodotArray())
		{
			if (item.VariantType != Variant.Type.Dictionary) continue;

			Godot.Collections.Dictionary node = item.AsGodotDictionary();
			string type = node.TryGetValue("type", out Variant kind) ? kind.AsString() : "";

			switch (type)
			{
				case "function_call":
					reply.Calls.Add(new AgentProtocol.ToolCall
					{
						Id = node.TryGetValue("call_id", out Variant callId)
							? callId.AsString()
							: node.TryGetValue("id", out Variant id) ? id.AsString() : "",
						Name = node.TryGetValue("name", out Variant name) ? name.AsString() : "",
						Arguments = node.TryGetValue("arguments", out Variant arguments) ? arguments.AsString() : "",
					});
					break;

				case "message":
					messageText.Append(CollectText(node, "output_text"));
					break;

				case "reasoning":
					reply.Reasoning = CollectText(node, "summary");
					break;
			}
		}

		reply.Content = messageText.Length > 0
			? messageText.ToString()
			: root.TryGetValue("output_text", out Variant flat) ? flat.AsString() : "";

		return reply;
	}

	/// <summary>把 message.content[] / reasoning.summary[] 里的文本拼起来</summary>
	private static string CollectText(Godot.Collections.Dictionary node, string type)
	{
		if (!node.TryGetValue("content", out Variant content) || content.VariantType != Variant.Type.Array)
			content = node.TryGetValue("summary", out Variant summary) ? summary : default;

		if (content.VariantType != Variant.Type.Array) return "";

		var text = new StringBuilder();
		foreach (Variant part in content.AsGodotArray())
		{
			if (part.VariantType != Variant.Type.Dictionary) continue;

			Godot.Collections.Dictionary partNode = part.AsGodotDictionary();
			string partType = partNode.TryGetValue("type", out Variant kind) ? kind.AsString() : "";
			if (type == "output_text" && partType != "output_text") continue;

			if (partNode.TryGetValue("text", out Variant value)) text.Append(value.AsString());
		}

		return text.ToString();
	}

	private static string FirstNonEmpty(Godot.Collections.Dictionary node, params string[] keys)
	{
		foreach (string key in keys)
		{
			if (node.TryGetValue(key, out Variant value) && value.AsString().Length > 0) return value.AsString();
		}

		return "";
	}

	private static string Trim(string text, int limit) =>
		text.Length <= limit ? text : text[..limit] + "…";
}
