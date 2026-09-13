using Godot;
using System;

/// <summary>
/// Agent 模式的左侧思考面板：把模型每一步说的话、调的工具、拿到的结果按顺序展示出来。
/// 面板只在 Agent 模式下出现（由迷宫场景按需实例化）。
/// </summary>
public partial class AgentPanel : CanvasLayer
{
	private const int MaxEntryLength = 500;

	private RichTextLabel _log;
	private Button _stopButton;

	/// <summary>玩家点了"停止"</summary>
	public event Action StopRequested;

	/// <summary>已经展示过多少步（自检用）</summary>
	public int StepCount { get; private set; }

	public override void _Ready()
	{
		_log = GetNode<RichTextLabel>("Log");
		_stopButton = GetNode<Button>("StopButton");

		_stopButton.Pressed += () =>
		{
			_stopButton.Disabled = true;
			_stopButton.Text = "正在停止…";
			StopRequested?.Invoke();
		};
	}

	public void Begin()
	{
		Visible = true;
		_log.Clear();
		_stopButton.Disabled = false;
		_stopButton.Text = "停止";
		Append("[color=#8ab4f8]开始：模型只拿到声波感知，看不到整张地图。[/color]");
	}

	/// <summary>模型一次回复（含它的思考文本）</summary>
	public void Assistant(int step, AgentProtocol.Reply reply)
	{
		StepCount = step;
		Append($"[color=#8ab4f8]▸ 第 {step} 步[/color]");

		if (reply.Reasoning.Length > 0) Append($"[color=#b8a6e0]思考：{Escape(reply.Reasoning)}[/color]");
		if (reply.Content.Length > 0) Append($"[color=#e8eaed]{Escape(reply.Content)}[/color]");
	}

	public void Tool(AgentProtocol.ToolCall call, string result) =>
		Append($"[color=#ffd479]调用 {Escape(call.Name)} {Escape(call.Arguments)}[/color]\n" +
			$"[color=#9aa0a6]{Escape(result)}[/color]");

	public void Error(int step, string message) =>
		Append($"[color=#ff8080]✕ 第 {step} 步失败：{Escape(message)}[/color]");

	public void Finish(bool won, string summary)
	{
		GetNode<Label>("Title").Text = "AI 已结束（你可以继续操作）";

		Append(won
			? $"[color=#5cd75c]✔ {Escape(summary)}[/color]"
			: $"[color=#ff8080]■ {Escape(summary)}[/color]");

		_stopButton.Disabled = true;
		_stopButton.Text = "已结束";
	}

	private void Append(string bbcode)
	{
		_log.AppendText(bbcode + "\n");
		_log.ScrollToLine(_log.GetLineCount());
	}

	/// <summary>模型输出里可能有方括号，转义掉免得被当成 BBCode 标签</summary>
	private static string Escape(string text)
	{
		string value = text.Replace("[", "[lb]").Replace("\r", "");
		return value.Length <= MaxEntryLength ? value : value[..MaxEntryLength] + "…";
	}
}
