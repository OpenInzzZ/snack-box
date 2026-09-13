using Godot;
using System;

/// <summary>
/// 暂停面板：整棵树暂停时它仍然工作（ProcessMode = Always），负责"继续 / 返回菜单"。
/// </summary>
public partial class PauseMenu : CanvasLayer
{
	public event Action ResumeRequested;
	public event Action MenuRequested;

	public override void _Ready()
	{
		Visible = false;
		GetNode<Button>("Panel/ResumeButton").Pressed += () => ResumeRequested?.Invoke();
		GetNode<Button>("Panel/MenuButton").Pressed += () => MenuRequested?.Invoke();
	}

	public void Open() => Visible = true;

	public void Close() => Visible = false;

	/// <summary>暂停时再按 Esc / 空格继续</summary>
	public override void _UnhandledInput(InputEvent @event)
	{
		if (!Visible) return;
		if (!@event.IsActionPressed("ui_cancel")) return;

		ResumeRequested?.Invoke();
		GetViewport().SetInputAsHandled();
	}
}
