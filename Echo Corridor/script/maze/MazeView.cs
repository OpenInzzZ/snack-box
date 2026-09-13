using Godot;

/// <summary>
/// 迷宫渲染层：只做绘制，从 MazeGame 读取墙壁亮度、声波与玩家位置
/// 背景全黑，墙壁 / 声波 / 玩家统一用白色，亮度随衰减自然融回黑色
/// </summary>
public partial class MazeView : Node2D
{
	private MazeGame _game;
	private Vector2[] _arcPoints;

	public override void _Ready()
	{
		_game = GetParent<MazeGame>();
	}

	public override void _Process(double delta)
	{
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (_game == null || _game.CellSize <= 0f) return;

		DrawWalls();
		DrawExit();
		DrawStart();
		DrawWaves();
		DrawPlayer();
	}

	/// <summary>当前画面覆盖到的世界矩形，用来剔除屏幕外的墙（大地图时能省掉绝大部分绘制）</summary>
	private Rect2 VisibleWorldRect()
	{
		return GetViewportTransform().AffineInverse() * GetViewportRect();
	}

	private void DrawWalls()
	{
		Rect2 visible = VisibleWorldRect().Grow(_game.CellSize);

		foreach (MazeGame.Wall wall in _game.Walls)
		{
			if (wall.Intensity <= 0.01f) continue;
			if (!visible.Intersects(wall.Rect)) continue;

			DrawRect(wall.Rect, new Color(1f, 1f, 1f, wall.Intensity));
		}
	}

	private void DrawExit()
	{
		if (_game.ExitIntensity <= 0.01f) return;

		float size = _game.ExitSize;
		Rect2 rect = new(_game.ExitCenter - new Vector2(size, size) * 0.5f, new Vector2(size, size));
		DrawRect(rect, new Color(1f, 1f, 1f, _game.ExitIntensity), false,
			Mathf.Max(2f, _game.CellSize * 0.08f), true);
	}

	/// <summary>通关揭示全图时，用白色实心圆标出玩家起点</summary>
	private void DrawStart()
	{
		if (!_game.Revealed) return;

		DrawCircle(_game.StartCenter, _game.StartRadius, Colors.White);
	}

	private void DrawWaves()
	{
		float width = Mathf.Max(1.5f, _game.CellSize * 0.06f);

		foreach (MazeGame.SoundWave wave in _game.Waves)
		{
			float progress = wave.MaxRadius <= 0f ? 1f : Mathf.Clamp(wave.Radius / wave.MaxRadius, 0f, 1f);
			float alpha = (1f - progress) * 0.9f;
			if (alpha <= 0.01f) continue;

			DrawExpandingArcs(wave, alpha, width);
		}
	}

	/// <summary>
	/// 只画还在外扩的方向：它们按当前半径连成一段段圆弧。
	/// 已经撞到墙的方向停在墙面、与墙重合，不再绘制——这样就不会出现把"近处墙点"
	/// 和"远处弧点"连起来的斜线（那是阴影边界，画出来像凭空多一根线）。
	/// </summary>
	private void DrawExpandingArcs(MazeGame.SoundWave wave, float alpha, float width)
	{
		int count = wave.FrontDistance.Length;
		int runStart = -1;

		for (int k = 0; k <= count; k++)
		{
			bool expanding = k < count && wave.Radius < wave.FrontDistance[k];

			if (expanding)
			{
				if (runStart < 0) runStart = k;
				continue;
			}

			if (runStart >= 0)
			{
				DrawArcRun(wave, runStart, k - 1, alpha, width);
				runStart = -1;
			}
		}
	}

	/// <summary>把 [from, to] 这段连续方向连成一条圆弧折线</summary>
	private void DrawArcRun(MazeGame.SoundWave wave, int from, int to, float alpha, float width)
	{
		int length = to - from + 1;
		if (length < 2) return;   // 只有一个方向连不成线

		if (_arcPoints == null || _arcPoints.Length != length) _arcPoints = new Vector2[length];

		int count = wave.FrontDistance.Length;
		for (int i = 0; i < length; i++)
		{
			float angle = Mathf.Tau * (from + i) / count;
			_arcPoints[i] = wave.Center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * wave.Radius;
		}

		DrawPolyline(_arcPoints, new Color(1f, 1f, 1f, alpha), width, true);
	}

	private void DrawPlayer()
	{
		float size = _game.PlayerSize;
		DrawRect(new Rect2(_game.PlayerPosition - new Vector2(size, size) * 0.5f, new Vector2(size, size)), Colors.White);
	}
}
