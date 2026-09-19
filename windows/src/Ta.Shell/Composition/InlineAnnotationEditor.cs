using Ta.Core.Agent;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Shell.Contracts;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Ta.Shell.Composition;

/// <summary>
/// 交互式标注编辑器。对应 Mac 版 <c>InlineAnnotationController</c>
/// （AIScreenshotApp/Editor/InlineAnnotationController.swift:129-210）：
/// 框选结果弹到屏幕上原位标注，工具条提供颜色/线宽/撤销重做，
/// 动作按钮（复制/保存/钉图）通过 <c>actionHandler</c> 回交编排层。
///
/// 渲染不另起炉灶：编辑产物 = 一串 <see cref="AnnotationOperation"/>，
/// 最终位图由 <see cref="Ta.Annotation.TaAgentAnnotationRenderer"/> 产出 ——
/// 与 Agent 桥 <c>transform.image</c> 配方渲染是同一条像素管线（Mac 亦然）。
///
/// 支持工具：矩形、椭圆、箭头、画笔、荧光笔、文字、编号、马赛克、马赛克笔、
/// 模糊、放大镜、橡皮、裁剪。快捷键：Ctrl+Z 撤销、Ctrl+Y 重做、
/// Enter 复制、Esc 关闭（返回 null 表示取消）。
/// </summary>
public sealed class InlineAnnotationEditor : IAnnotationEditor
{
    /// <summary>渲染器与 Agent 桥共用（同一条像素管线）。</summary>
    private static readonly Ta.Annotation.TaAgentAnnotationRenderer Renderer = new();

    /// <inheritdoc />
    public Task<AnnotationEditAction?> OpenAsync(
        RgbaBitmap image,
        CaptureSelection selection,
        Func<AnnotationEditAction, RgbaBitmap, bool> actionHandler,
        CancellationToken cancellationToken = default)
    {
        _ = selection; // 位置信息仅用于日志语义；编辑窗居中显示。
        _ = cancellationToken;

        using var form = new EditorForm(image, actionHandler);
        var dialogResult = form.ShowDialog();
        _ = dialogResult; // 结果以动作表达；Esc/关闭 = null（取消）。
        return Task.FromResult(form.CommittedAction);
    }

    /// <summary>RgbaBitmap（RGBA 字节序）→ GDI+ 位图（BGRA 字节序）。</summary>
    internal static Drawing.Bitmap ToBitmap(RgbaBitmap bitmap)
    {
        var result = new Drawing.Bitmap(bitmap.Width, bitmap.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        var target = result.LockBits(
            new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            Drawing.Imaging.ImageLockMode.WriteOnly,
            Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var source = bitmap.Pixels;
            var stride = target.Stride;
            unsafe
            {
                var basePointer = (byte*)target.Scan0;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var sourceIndex = y * bitmap.Stride;
                    var targetIndex = y * stride;
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        // RGBA 字节序 → BGRA 字节序。
                        basePointer[targetIndex] = source[sourceIndex + 2];
                        basePointer[targetIndex + 1] = source[sourceIndex + 1];
                        basePointer[targetIndex + 2] = source[sourceIndex];
                        basePointer[targetIndex + 3] = source[sourceIndex + 3];
                        sourceIndex += 4;
                        targetIndex += 4;
                    }
                }
            }
        }
        finally
        {
            result.UnlockBits(target);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 编辑窗体
    // ═══════════════════════════════════════════════════════════════════

    private sealed class EditorForm : WinForms.Form
    {
        private static readonly (string Label, AnnotationColor Color)[] Palette =
        {
            ("红", new AnnotationColor(1, 59.0 / 255, 48.0 / 255)),
            ("橙", new AnnotationColor(1, 149.0 / 255, 0)),
            ("黄", new AnnotationColor(1, 204.0 / 255, 0)),
            ("绿", new AnnotationColor(52.0 / 255, 199.0 / 255, 89.0 / 255)),
            ("蓝", new AnnotationColor(0, 122.0 / 255, 1)),
            ("紫", new AnnotationColor(175.0 / 255, 82.0 / 255, 222.0 / 255)),
            ("黑", new AnnotationColor(0.1, 0.1, 0.12)),
            ("白", new AnnotationColor(1, 1, 1)),
        };

        private static readonly (string Label, double Width)[] Widths =
        {
            ("细", 3), ("中", 6), ("粗", 10), ("加粗", 16),
        };

        private readonly RgbaBitmap _source;
        private readonly Func<AnnotationEditAction, RgbaBitmap, bool> _actionHandler;

        private readonly List<AnnotationOperation> _operations = new();
        private readonly List<AnnotationOperation> _redo = new();
        private AnnotationRect? _cropRect;
        private int _numberNext = 1;

        private AnnotationColor _color = Palette[0].Color;
        private double _lineWidth = 6;
        private EditorTool _tool = EditorTool.Rectangle;

        // 拖拽草稿（图像像素坐标）。
        private AnnotationPoint? _dragStart;
        private AnnotationPoint? _dragCurrent;
        private List<AnnotationPoint> _dragStroke = new();
        private readonly HashSet<(int, int)> _mosaicCells = new();

        private readonly WinForms.Panel _canvasPanel;
        private readonly WinForms.PictureBox _canvas;
        private readonly WinForms.Panel _toolbar;
        private readonly List<WinForms.Button> _toolButtons = new();
        private readonly List<WinForms.Button> _colorButtons = new();
        private readonly List<WinForms.Button> _widthButtons = new();
        private readonly WinForms.Button _undoButton;
        private readonly WinForms.Button _redoButton;
        private Drawing.Bitmap? _displayImage;

        /// <summary>提交的动作；null 表示用户取消。</summary>
        public AnnotationEditAction? CommittedAction { get; private set; }

        public EditorForm(RgbaBitmap image, Func<AnnotationEditAction, RgbaBitmap, bool> actionHandler)
        {
            _source = image;
            _actionHandler = actionHandler;

            Text = "标注 · 拓";
            StartPosition = WinForms.FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Drawing.Color.FromArgb(24, 24, 28);
            Font = new Drawing.Font("Microsoft YaHei UI", 9f);

            // 窗口尺寸：图像 1:1（上限 85% 屏幕区域，超出出滚动条）。
            var screen = Screen.FromPoint(Cursor.Position).Bounds;
            var chromeWidth = 24;
            var chromeHeight = 96;
            var clientWidth = Math.Min(image.Width, (int)(screen.Width * 0.85)) + chromeWidth;
            var clientHeight = Math.Min(image.Height, (int)(screen.Height * 0.80)) + chromeHeight;
            ClientSize = new Drawing.Size(clientWidth, clientHeight);
            MinimumSize = new Drawing.Size(720, 420);

            // 工具条。
            _toolbar = new WinForms.Panel
            {
                Dock = WinForms.DockStyle.Top,
                Height = 64,
                BackColor = Drawing.Color.FromArgb(36, 36, 40),
            };

            var tools = new[]
            {
                (EditorTool.Rectangle, "矩形"), (EditorTool.Ellipse, "椭圆"),
                (EditorTool.Arrow, "箭头"), (EditorTool.Pen, "画笔"),
                (EditorTool.Highlighter, "荧光笔"), (EditorTool.Text, "文字"),
                (EditorTool.Number, "编号"), (EditorTool.Mosaic, "马赛克"),
                (EditorTool.MosaicBrush, "涂抹"), (EditorTool.Blur, "模糊"),
                (EditorTool.Magnify, "放大镜"), (EditorTool.Eraser, "橡皮"),
                (EditorTool.Crop, "裁剪"),
            };

            var x = 8;
            foreach (var (tool, label) in tools)
            {
                var captured = tool;
                var button = MakeToolbarButton(label, 34);
                button.Click += (_, _) => SelectTool(captured);
                _toolbar.Controls.Add(button);
                button.Location = new Drawing.Point(x, 4);
                x += 38;
                _toolButtons.Add(button);
            }

            x += 6;
            foreach (var (label, color) in Palette)
            {
                var captured = color;
                var button = new WinForms.Button
                {
                    Size = new Drawing.Size(26, 26),
                    Location = new Drawing.Point(x, 8),
                    FlatStyle = WinForms.FlatStyle.Flat,
                    BackColor = Drawing.Color.FromArgb(
                        (int)(color.Red * 255), (int)(color.Green * 255), (int)(color.Blue * 255)),
                    Tag = label,
                };
                button.FlatAppearance.BorderSize = 1;
                button.Click += (_, _) => SelectColor(captured);
                _toolbar.Controls.Add(button);
                x += 30;
                _colorButtons.Add(button);
            }

            x += 6;
            foreach (var (label, width) in Widths)
            {
                var captured = width;
                var button = MakeToolbarButton(label, 34);
                button.Click += (_, _) => SelectWidth(captured);
                _toolbar.Controls.Add(button);
                button.Location = new Drawing.Point(x, 8);
                x += 38;
                _widthButtons.Add(button);
            }

            _undoButton = MakeToolbarButton("撤销", 48);
            _undoButton.Location = new Drawing.Point(x, 8);
            _undoButton.Click += (_, _) => Undo();
            _toolbar.Controls.Add(_undoButton);
            x += 52;

            _redoButton = MakeToolbarButton("重做", 48);
            _redoButton.Location = new Drawing.Point(x, 8);
            _redoButton.Click += (_, _) => Redo();
            _toolbar.Controls.Add(_redoButton);
            x += 56;

            foreach (var (action, label, highlight) in new[]
                     {
                         (AnnotationEditAction.Copy, "复制", true),
                         (AnnotationEditAction.Save, "保存", false),
                         (AnnotationEditAction.Pin, "钉图", false),
                     })
            {
                var captured = action;
                var button = new WinForms.Button
                {
                    Text = label,
                    Size = new Drawing.Size(56, 26),
                    Location = new Drawing.Point(x, 8),
                    FlatStyle = WinForms.FlatStyle.Flat,
                    BackColor = highlight ? Drawing.Color.FromArgb(69, 130, 230) : Drawing.Color.FromArgb(56, 56, 62),
                    ForeColor = Drawing.Color.White,
                };
                button.Click += (_, _) => Commit(captured);
                _toolbar.Controls.Add(button);
                x += 60;
            }

            // 画布。
            _canvasPanel = new WinForms.Panel
            {
                Dock = WinForms.DockStyle.Fill,
                AutoScroll = true,
                BackColor = Drawing.Color.FromArgb(12, 12, 16),
            };

            _canvas = new WinForms.PictureBox
            {
                SizeMode = WinForms.PictureBoxSizeMode.AutoSize,
                Location = new Drawing.Point(0, 0),
                BackColor = Drawing.Color.FromArgb(12, 12, 16),
            };
            _canvas.MouseDown += OnCanvasMouseDown;
            _canvas.MouseMove += OnCanvasMouseMove;
            _canvas.MouseUp += OnCanvasMouseUp;
            _canvas.Paint += OnCanvasPaint;
            _canvasPanel.Controls.Add(_canvas);

            Controls.Add(_canvasPanel);
            Controls.Add(_toolbar);

            KeyDown += OnFormKeyDown;
            Shown += (_, _) => RenderToDisplay();
            SelectTool(EditorTool.Rectangle);
        }

        private WinForms.Button MakeToolbarButton(string label, int width) => new()
        {
            Text = label,
            Size = new Drawing.Size(width, 26),
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = Drawing.Color.FromArgb(56, 56, 62),
            ForeColor = Drawing.Color.White,
            Tag = label,
        };

        // ── 工具/颜色/线宽选择 ───────────────────────────────────────

        private void SelectTool(EditorTool tool)
        {
            _tool = tool;
            foreach (var button in _toolButtons)
            {
                var selected = button.Text switch
                {
                    "矩形" => tool == EditorTool.Rectangle,
                    "椭圆" => tool == EditorTool.Ellipse,
                    "箭头" => tool == EditorTool.Arrow,
                    "画笔" => tool == EditorTool.Pen,
                    "荧光笔" => tool == EditorTool.Highlighter,
                    "文字" => tool == EditorTool.Text,
                    "编号" => tool == EditorTool.Number,
                    "马赛克" => tool == EditorTool.Mosaic,
                    "涂抹" => tool == EditorTool.MosaicBrush,
                    "模糊" => tool == EditorTool.Blur,
                    "放大镜" => tool == EditorTool.Magnify,
                    "橡皮" => tool == EditorTool.Eraser,
                    "裁剪" => tool == EditorTool.Crop,
                    _ => false,
                };
                button.BackColor = selected
                    ? Drawing.Color.FromArgb(69, 130, 230)
                    : Drawing.Color.FromArgb(56, 56, 62);
            }
        }

        private void SelectColor(AnnotationColor color)
        {
            _color = color;
            for (var index = 0; index < _colorButtons.Count; index++)
            {
                _colorButtons[index].FlatAppearance.BorderSize =
                    Palette[index].Color.Equals(color) ? 3 : 1;
            }

            _colorButtons[0].Invalidate();
        }

        private void SelectWidth(double width)
        {
            _lineWidth = width;
            for (var index = 0; index < _widthButtons.Count; index++)
            {
                _widthButtons[index].BackColor = Widths[index].Width.Equals(width)
                    ? Drawing.Color.FromArgb(69, 130, 230)
                    : Drawing.Color.FromArgb(56, 56, 62);
            }
        }

        // ── 鼠标交互 ─────────────────────────────────────────────────

        private AnnotationPoint ImagePointOf(WinForms.MouseEventArgs e)
        {
            // PictureBox 是 AutoSize（1:1），滚动偏移由 AutoScrollPosition 补偿
            //（AutoScrollPosition 返回负值，取反即滚动量）。
            var offsetX = -_canvasPanel.AutoScrollPosition.X;
            var offsetY = -_canvasPanel.AutoScrollPosition.Y;
            return new AnnotationPoint(e.X + offsetX, e.Y + offsetY);
        }

        private void OnCanvasMouseDown(object? sender, WinForms.MouseEventArgs e)
        {
            if (e.Button != WinForms.MouseButtons.Left || _displayImage is null)
            {
                return;
            }

            var point = ImagePointOf(e);

            if (_tool == EditorTool.Eraser)
            {
                EraseAt(point);
                return;
            }

            if (_tool == EditorTool.Text)
            {
                BeginTextInput(point);
                return;
            }

            if (_tool == EditorTool.Number)
            {
                AppendOperation(AnnotationOperation.CreateNumber(new AnnotationNumberOperation(
                    NewId(), point, _numberNext++, _color, Diameter: Math.Max(28, _lineWidth * 4))));
                return;
            }

            _dragStart = point;
            _dragCurrent = point;
            _dragStroke = new List<AnnotationPoint> { point };
            _mosaicCells.Clear();
        }

        private void OnCanvasMouseMove(object? sender, WinForms.MouseEventArgs e)
        {
            if (_dragStart is null || e.Button != WinForms.MouseButtons.Left)
            {
                return;
            }

            var point = ImagePointOf(e);
            _dragCurrent = point;

            if (_tool is EditorTool.Pen or EditorTool.Highlighter or EditorTool.MosaicBrush)
            {
                _dragStroke.Add(point);

                if (_tool == EditorTool.MosaicBrush)
                {
                    var cell = 16;
                    _mosaicCells.Add(((int)Math.Floor(point.X / cell), (int)Math.Floor(point.Y / cell)));
                }
            }

            _canvas.Invalidate();
        }

        private void OnCanvasMouseUp(object? sender, WinForms.MouseEventArgs e)
        {
            if (_dragStart is null)
            {
                return;
            }

            var start = _dragStart.Value;
            var end = ImagePointOf(e);
            _dragStart = null;
            _dragCurrent = null;

            var rect = NormalizeRect(start, end);
            var tiny = Math.Abs(end.X - start.X) < 3 && Math.Abs(end.Y - start.Y) < 3;

            switch (_tool)
            {
                case EditorTool.Rectangle when !tiny:
                    AppendOperation(AnnotationOperation.CreateRectangle(new AnnotationRectOperation(
                        NewId(), rect, _color, _lineWidth, Dashed: false)));
                    break;

                case EditorTool.Ellipse when !tiny:
                    AppendOperation(AnnotationOperation.CreateEllipse(new AnnotationRectOperation(
                        NewId(), rect, _color, _lineWidth, Dashed: false)));
                    break;

                case EditorTool.Arrow when !tiny:
                    AppendOperation(AnnotationOperation.CreateArrow(new AnnotationArrowOperation(
                        NewId(), start, end, _color, _lineWidth, Dashed: false)));
                    break;

                case EditorTool.Pen when _dragStroke.Count > 1:
                    AppendOperation(AnnotationOperation.CreatePen(new AnnotationStrokeOperation(
                        NewId(), _dragStroke.ToArray(), _color, _lineWidth, Dashed: false)));
                    break;

                case EditorTool.Highlighter when _dragStroke.Count > 1:
                    // 荧光笔半透明：叠加当前色的 35% alpha（Mac 的 AnnotationColor.Highlighter 同式）。
                    AppendOperation(AnnotationOperation.CreateHighlighter(new AnnotationStrokeOperation(
                        NewId(), _dragStroke.ToArray(),
                        new AnnotationColor(_color.Red, _color.Green, _color.Blue, 0.35),
                        Math.Max(12, _lineWidth * 2.5), Dashed: false)));
                    break;

                case EditorTool.Mosaic when !tiny:
                    AppendOperation(AnnotationOperation.CreateMosaic(new AnnotationMosaicOperation(
                        NewId(), AnnotationMosaicMode.Rect, rect, null, 0, Scale: 14)));
                    break;

                case EditorTool.MosaicBrush when _dragStroke.Count > 0:
                    AppendOperation(AnnotationOperation.CreateMosaic(new AnnotationMosaicOperation(
                        NewId(), AnnotationMosaicMode.Brush, null,
                        _dragStroke.ToArray(), LineWidth: Math.Max(16, _lineWidth * 2), Scale: 14)));
                    break;

                case EditorTool.Blur when !tiny:
                    AppendOperation(AnnotationOperation.CreateBlur(new AnnotationBlurOperation(
                        NewId(), rect, Radius: 10)));
                    break;

                case EditorTool.Magnify when !tiny:
                    AppendOperation(AnnotationOperation.CreateMagnify(new AnnotationMagnifyOperation(
                        NewId(), rect, Factor: 2)));
                    break;

                case EditorTool.Crop when !tiny:
                    _cropRect = rect;
                    AppendOperation(null); // 裁剪改变显示尺寸，重渲染。
                    break;
            }

            _dragStroke = new List<AnnotationPoint>();
            _canvas.Invalidate();
        }

        private void OnCanvasPaint(object? sender, WinForms.PaintEventArgs e)
        {
            if (_dragStart is null || _dragCurrent is null || _displayImage is null)
            {
                return;
            }

            var start = _dragStart.Value;
            var end = _dragCurrent.Value;
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb((int)(_color.Red * 255), (int)(_color.Green * 255), (int)(_color.Blue * 255)), (float)_lineWidth);

            var rect = new Drawing.Rectangle(
                (int)Math.Min(start.X, end.X), (int)Math.Min(start.Y, end.Y),
                (int)Math.Abs(end.X - start.X), (int)Math.Abs(end.Y - start.Y));

            switch (_tool)
            {
                case EditorTool.Rectangle:
                case EditorTool.Crop:
                    e.Graphics.DrawRectangle(pen, rect);
                    if (_tool == EditorTool.Crop)
                    {
                        using var shade = new Drawing.SolidBrush(Drawing.Color.FromArgb(90, 0, 0, 0));
                        var region = new Drawing.Region(_canvas.ClientRectangle);
                        region.Exclude(rect);
                        e.Graphics.FillRegion(shade, region);
                    }

                    break;
                case EditorTool.Ellipse:
                    e.Graphics.DrawEllipse(pen, rect);
                    break;
                case EditorTool.Arrow:
                    e.Graphics.DrawLine(pen, (int)start.X, (int)start.Y, (int)end.X, (int)end.Y);
                    DrawArrowHead(e.Graphics, pen, start, end);
                    break;
                case EditorTool.Pen or EditorTool.Highlighter or EditorTool.MosaicBrush
                    when _dragStroke.Count > 1:
                    pen.Width = _tool == EditorTool.Highlighter || _tool == EditorTool.MosaicBrush
                        ? (float)Math.Max(12, _lineWidth * 2)
                        : (float)_lineWidth;
                    pen.StartCap = pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                    e.Graphics.DrawLines(pen, _dragStroke
                        .Select(p => new Drawing.PointF((float)p.X, (float)p.Y))
                        .ToArray());
                    break;
            }
        }

        private static void DrawArrowHead(Drawing.Graphics graphics, Drawing.Pen pen, AnnotationPoint start, AnnotationPoint end)
        {
            var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            var length = Math.Max(10, pen.Width * 2.4);
            var left = (end.X - length * Math.Cos(angle - Math.PI / 6), end.Y - length * Math.Sin(angle - Math.PI / 6));
            var right = (end.X - length * Math.Cos(angle + Math.PI / 6), end.Y - length * Math.Sin(angle + Math.PI / 6));
            graphics.DrawLines(pen, new[]
            {
                new Drawing.PointF((float)left.Item1, (float)left.Item2),
                new Drawing.PointF((float)end.X, (float)end.Y),
                new Drawing.PointF((float)right.Item1, (float)right.Item2),
            });
        }

        // ── 文字输入 ─────────────────────────────────────────────────

        private void BeginTextInput(AnnotationPoint point)
        {
            var input = new WinForms.TextBox
            {
                Parent = _canvasPanel,
                Location = new Drawing.Point(
                    (int)point.X - _canvasPanel.AutoScrollPosition.X,
                    (int)point.Y - _canvasPanel.AutoScrollPosition.Y),
                Font = new Drawing.Font("Microsoft YaHei UI", (float)Math.Max(14, _lineWidth * 2.4)),
                BackColor = Drawing.Color.White,
                Width = 220,
            };
            _canvasPanel.Controls.Add(input);
            input.BringToFront();
            input.Focus();

            void CommitText()
            {
                var text = input.Text.Trim();
                input.Parent?.Controls.Remove(input);
                input.Dispose();
                if (text.Length > 0)
                {
                    AppendOperation(AnnotationOperation.CreateText(new AnnotationTextOperation(
                        NewId(), point, text, _color, FontSize: Math.Max(14, _lineWidth * 2.4))));
                }
            }

            input.KeyDown += (_, args) =>
            {
                if (args.KeyCode == WinForms.Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    CommitText();
                }
                else if (args.KeyCode == WinForms.Keys.Escape)
                {
                    args.SuppressKeyPress = true;
                    input.Parent?.Controls.Remove(input);
                    input.Dispose();
                }
            };

            input.LostFocus += (_, _) =>
            {
                if (!input.IsDisposed)
                {
                    CommitText();
                }
            };
        }

        // ── 橡皮 ─────────────────────────────────────────────────────

        private void EraseAt(AnnotationPoint point)
        {
            const double hitRadius = 12;

            for (var index = _operations.Count - 1; index >= 0; index--)
            {
                if (!OperationHits(_operations[index], point, hitRadius))
                {
                    continue;
                }

                _operations.RemoveAt(index);
                _redo.Clear();
                RenderToDisplay();
                return;
            }
        }

        private static bool OperationHits(AnnotationOperation operation, AnnotationPoint point, double radius)
        {
            bool InRect(AnnotationRect rect) =>
                point.X >= rect.X - radius && point.X <= rect.X + rect.Width + radius &&
                point.Y >= rect.Y - radius && point.Y <= rect.Y + rect.Height + radius;

            return operation switch
            {
                _ when operation.RectShape is { } shape => InRect(shape.Rect),
                _ when operation.Arrow is { } arrow => DistanceToSegment(point, arrow.Start, arrow.End) <= radius + arrow.LineWidth / 2,
                _ when operation.Stroke is { } stroke => stroke.Points.Length > 0 &&
                    stroke.Points.Any(p => Distance(point, p) <= radius + stroke.LineWidth / 2),
                _ when operation.Text is { } text => InRect(new AnnotationRect(
                    text.Origin.X, text.Origin.Y, text.FontSize * text.Text.Length * 0.62, text.FontSize * 1.4)),
                _ when operation.Number is { } number => Distance(point, number.Center) <= number.Diameter / 2 + radius,
                _ when operation.Blur is { } blur => InRect(blur.Rect),
                _ when operation.Magnify is { } magnify => InRect(magnify.Rect),
                _ => false,
            };
        }

        private static double Distance(AnnotationPoint a, AnnotationPoint b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double DistanceToSegment(AnnotationPoint point, AnnotationPoint start, AnnotationPoint end)
        {
            var vx = end.X - start.X;
            var vy = end.Y - start.Y;
            var lengthSquared = vx * vx + vy * vy;
            if (lengthSquared < 1e-9)
            {
                return Distance(point, start);
            }

            var t = Math.Clamp(((point.X - start.X) * vx + (point.Y - start.Y) * vy) / lengthSquared, 0, 1);
            return Distance(point, new AnnotationPoint(start.X + t * vx, start.Y + t * vy));
        }

        // ── 撤销 / 重做 / 提交 ───────────────────────────────────────

        private void AppendOperation(AnnotationOperation? operation)
        {
            if (operation is not null)
            {
                _operations.Add(operation);
                _redo.Clear();
            }

            RenderToDisplay();
        }

        private void Undo()
        {
            if (_operations.Count == 0)
            {
                return;
            }

            _redo.Add(_operations[^1]);
            _operations.RemoveAt(_operations.Count - 1);
            RenderToDisplay();
        }

        private void Redo()
        {
            if (_redo.Count == 0)
            {
                return;
            }

            _operations.Add(_redo[^1]);
            _redo.RemoveAt(_redo.Count - 1);
            RenderToDisplay();
        }

        private void Commit(AnnotationEditAction action)
        {
            // 交给编排层（复制/保存/钉图各自走共享后端），成功才关窗。
            if (_actionHandler(action, RenderCurrent()))
            {
                CommittedAction = action;
                Close();
            }
        }

        private void OnFormKeyDown(object? sender, WinForms.KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case WinForms.Keys.Escape:
                    CommittedAction = null;
                    Close();
                    break;
                case WinForms.Keys.Enter:
                    Commit(AnnotationEditAction.Copy);
                    break;
                case WinForms.Keys.Z when e.Control && e.Shift:
                    Redo();
                    e.Handled = true;
                    break;
                case WinForms.Keys.Z when e.Control:
                    Undo();
                    e.Handled = true;
                    break;
                case WinForms.Keys.Y when e.Control:
                    Redo();
                    e.Handled = true;
                    break;
            }
        }

        // ── 渲染 ─────────────────────────────────────────────────────

        private RgbaBitmap RenderCurrent() => Renderer.Render(_source, _cropRect, _operations);

        private void RenderToDisplay()
        {
            var rendered = RenderCurrent();
            var newImage = ToBitmap(rendered);
            var old = _displayImage;
            _displayImage = newImage;
            _canvas.Image = newImage;
            old?.Dispose();
            _undoButton.Enabled = _operations.Count > 0;
            _redoButton.Enabled = _redo.Count > 0;
            _canvas.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _displayImage?.Dispose();
            }

            base.Dispose(disposing);
        }

        private static AnnotationRect NormalizeRect(AnnotationPoint a, AnnotationPoint b) => new(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(b.X - a.X),
            Math.Abs(b.Y - a.Y));

        private static string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();
    }

    /// <summary>编辑工具。对应 Mac 版 inline 工具集的子集命名。</summary>
    private enum EditorTool
    {
        Rectangle,
        Ellipse,
        Arrow,
        Pen,
        Highlighter,
        Text,
        Number,
        Mosaic,
        MosaicBrush,
        Blur,
        Magnify,
        Eraser,
        Crop,
    }
}
