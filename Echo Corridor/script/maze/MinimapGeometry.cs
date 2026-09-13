using Godot;

/// <summary>
/// 小地图专用几何：迷宫整体轮廓 + 当前亮着的墙（加粗版），只给缩略图相机看，主相机剔除。
///
/// 为什么需要它：小地图用的是"第二个相机把同一份场景缩小渲染"，而墙在世界里只有约 4px 宽，
/// 整张 63×63 的图缩进 220px 的缩略图后墙只剩约 0.4px，会变成若隐若现的发丝。
/// 所以这里把轮廓与亮墙按"小地图可辨认"的尺寸重画一遍，放在独立的 visibility_layer 上，
/// 只让缩略图相机渲染（主相机 cull_mask 排除该层），主视图完全看不到这些加粗几何。
/// </summary>
public partial class MinimapGeometry : Node2D
{
	/// <summary>小地图专用图层</summary>
	public const uint Layer = 2;

	/// <summary>主视口遮罩：只渲染普通图层（项目里所有节点默认都在第 1 层）</summary>
	public const uint MainViewportMask = 1;

	/// <summary>小地图视口遮罩：普通图层 + 本层加粗几何</summary>
	public const uint MinimapViewportMask = 1 | Layer;

	private const float OutlineCells = 0.28f;   // 轮廓粗细（格），保证缩到小地图仍有约 1px
	private const float WallCells = 0.55f;      // 亮墙粗细（格），保证缩到小地图能看清

	private MazeGame _game;

	public override void _Ready()
	{
		VisibilityLayer = Layer;
		_game = GetParent<MazeGame>();
	}

	public override void _Process(double delta)
	{
		if (_game?.Grid == null) return;

		// 墙的亮度一直在衰减，得每帧重画
		QueueRedraw();
	}

	public override void _Draw()
	{
		MazeGrid grid = _game?.Grid;
		if (grid == null) return;

		DrawShapeOutline(grid);
		DrawLitWalls();
	}

	/// <summary>迷宫整体轮廓：只描形状边界，很暗，给小地图当"整张图在哪"的参照</summary>
	private void DrawShapeOutline(MazeGrid grid)
	{
		float cell = _game.CellSize;
		float thickness = cell * OutlineCells;
		var color = new Color(1f, 1f, 1f, 0.3f);

		for (int y = 0; y < grid.Height; y++)
		{
			for (int x = 0; x < grid.Width; x++)
			{
				if (!grid.IsActive(x, y)) continue;

				Vector2 topLeft = _game.CellCenter(new Vector2I(x, y)) - new Vector2(cell, cell) * 0.5f;
				Vector2 topRight = topLeft + new Vector2(cell, 0f);
				Vector2 bottomLeft = topLeft + new Vector2(0f, cell);
				Vector2 bottomRight = topLeft + new Vector2(cell, cell);

				if (!grid.IsActive(x, y - 1)) DrawLine(topLeft, topRight, color, thickness);
				if (!grid.IsActive(x, y + 1)) DrawLine(bottomLeft, bottomRight, color, thickness);
				if (!grid.IsActive(x - 1, y)) DrawLine(topLeft, bottomLeft, color, thickness);
				if (!grid.IsActive(x + 1, y)) DrawLine(topRight, bottomRight, color, thickness);
			}
		}
	}

	/// <summary>当前亮着的墙画成加粗方块（以墙中心线对齐），亮度跟主视图一致所以一起淡出</summary>
	private void DrawLitWalls()
	{
		float thickness = _game.CellSize * WallCells;

		foreach (MazeGame.Wall wall in _game.Walls)
		{
			if (wall.Intensity <= 0.01f) continue;

			Vector2 min = new(Mathf.Min(wall.A.X, wall.B.X), Mathf.Min(wall.A.Y, wall.B.Y));
			Vector2 max = new(Mathf.Max(wall.A.X, wall.B.X), Mathf.Max(wall.A.Y, wall.B.Y));
			Vector2 span = max - min;
			Vector2 center = (min + max) * 0.5f;

			// 横向墙补厚度在 Y，纵向墙补在 X
			Vector2 size = new(
				span.X > 0f ? span.X : thickness,
				span.Y > 0f ? span.Y : thickness);

			DrawRect(new Rect2(center - size * 0.5f, size), new Color(1f, 1f, 1f, wall.Intensity));
		}
	}
}
