﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NLog;
using SkiaSharp;

namespace LibDmd.Output.Virtual
{
	class SegGenerator
	{

		private readonly SKColor _foregroundColor = SKColors.OrangeRed;
		private readonly SKColor _segmentBackgroundColor = new SKColor(255, 255, 255, 0x1d);
		private readonly SKColor _backgroundColor = SKColors.Black;

		private readonly Assembly _assembly = Assembly.GetExecutingAssembly();
		protected static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		private int _call = 0;
		private readonly Stopwatch _stopwatch = new Stopwatch();

		private readonly Dictionary<int, SkiaSharp.Extended.Svg.SKSvg> _segments = new Dictionary<int, SkiaSharp.Extended.Svg.SKSvg>();
		private readonly Dictionary<int, SKSurface> _segmentsRasterized = new Dictionary<int, SKSurface>();

		private readonly SkiaSharp.Extended.Svg.SKSvg _fullSvg = new SkiaSharp.Extended.Svg.SKSvg();
		private SKSurface _fullSvgRasterized;
		private AlphaNumericFrame _frame;

		private float _svgWidth;
		private float _svgHeight;
		private float _svgScale;
		private SKMatrix _svgMatrix;
		private SKImageInfo _svgInfo;

		private const int SkewAngle = -15;
		private const int NumSegments = 20;
		private const int NumLines = 2;
		private const int Padding = 20;

		public SegGenerator()
		{
			// load svgs from packages resources
			const string prefix = "LibDmd.Output.Virtual.alphanum.";
			var segmentFileNames = new[]
			{
				$"{prefix}00-top.svg",
				$"{prefix}01-top-right.svg",
				$"{prefix}02-bottom-right.svg",
				$"{prefix}03-bottom.svg",
				$"{prefix}04-bottom-left.svg",
				$"{prefix}05-top-left.svg",
				$"{prefix}06-middle-left.svg",
				$"{prefix}07-comma.svg",
				$"{prefix}08-diag-top-left.svg",
				$"{prefix}09-center-top.svg",
				$"{prefix}10-diag-top-right.svg",
				$"{prefix}11-middle-right.svg",
				$"{prefix}12-diag-bottom-right.svg",
				$"{prefix}13-center-bottom.svg",
				$"{prefix}14-diag-bottom-left.svg",
				$"{prefix}15-dot.svg",
			};
			Logger.Info("Loading segment SVGs...");
			for (var i = 0; i < segmentFileNames.Length; i++) {
				var svg = new SkiaSharp.Extended.Svg.SKSvg();
				svg.Load(_assembly.GetManifestResourceStream(segmentFileNames[i]));
				_segments.Add(i, svg);
			}
			_fullSvg.Load(_assembly.GetManifestResourceStream($"{prefix}full.svg"));
		}

		public WriteableBitmap CreateImage(int width, int height)
		{
			SetupDimensions(width, height);
			Logger.Info("Width = {0}, Height = {1}, SkewedWidth = {2}", _svgWidth, _svgHeight, _svgInfo.Width);
			using (var svgPaint = new SKPaint()) {
				svgPaint.ColorFilter = SKColorFilter.CreateBlendMode(_foregroundColor, SKBlendMode.SrcIn);
				foreach (var i in _segments.Keys) {
					if (_segmentsRasterized.ContainsKey(i)) {
						_segmentsRasterized[i].Dispose();
					}
					
					_segmentsRasterized[i] = RasterizeSegment(_segments[i].Picture, svgPaint);
				}

				svgPaint.ColorFilter = SKColorFilter.CreateBlendMode(_segmentBackgroundColor, SKBlendMode.SrcIn);
				_fullSvgRasterized?.Dispose();
				_fullSvgRasterized = RasterizeSegment(_fullSvg.Picture, svgPaint);
			}
			return new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, BitmapPalettes.Halftone256Transparent);
		}

		public void UpdateFrame(AlphaNumericFrame frame)
		{
			_frame = frame;
		}

		private void DrawSegment(int num, SKCanvas canvas, SKPoint position)
		{

			var seg = _frame.SegmentData[num];
			for (var j = 0; j < 16; j++) {
				if (((seg >> j) & 0x1) != 0) {
					canvas.DrawSurface(_segmentsRasterized[j], position);
				}
			}
		}

		public void DrawBackground(SKCanvas canvas)
		{
			float transX = Padding;
			float transY = Padding;
			for (var j = 0; j < NumLines; j++) {
				for (var i = 0; i < NumSegments; i++) {
					canvas.DrawSurface(_fullSvgRasterized, new SKPoint(transX, transY));
					transX += _svgWidth;
				}
				transX = Padding;
				transY += _svgHeight + 10;
			}
		}

		public void DrawImage(WriteableBitmap writeableBitmap)
		{
			if (_frame == null || _segmentsRasterized.Count == 0) {
				return;
			}

			int width = (int)writeableBitmap.Width,
				height = (int)writeableBitmap.Height;

			writeableBitmap.Lock();

			using (var surface = SKSurface.Create(width, height, SKColorType.Bgra8888, SKAlphaType.Premul,
				writeableBitmap.BackBuffer, width * 4)) {

				var canvas = surface.Canvas;
				var paint = new SKPaint { Color = SKColors.White, TextSize = 10 };

				float transX = Padding;
				float transY = Padding;

				canvas.Clear(_backgroundColor);
				DrawBackground(canvas);

				for (var j = 0; j < NumLines; j++) {
					for (var i = 0; i < NumSegments; i++) {
						DrawSegment(i + 20 * j, canvas, new SKPoint(transX, transY));
						transX += _svgWidth;
					}
					transX = Padding;
					transY += _svgHeight + 10;
				}

				if (_call == 0) {
					_stopwatch.Start();
				}

				var fps = _call / (_stopwatch.Elapsed.TotalSeconds != 0 ? _stopwatch.Elapsed.TotalSeconds : 1);
				canvas.DrawText($"FPS: {fps:0}", 0, 10, paint);
				canvas.DrawText($"Frames: {this._call++}", 50, 10, paint);
			}

			writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
			writeableBitmap.Unlock();
		}

		private void SetupDimensions(int width, int height)
		{
			var svgSize = _segments[0].Picture.CullRect;
			var skewedFactor = SkewedWidth(svgSize.Width, svgSize.Height) / svgSize.Width;
			_svgWidth = (width - (2f * Padding)) / (NumSegments - 1 + skewedFactor);
			_svgScale = _svgWidth / svgSize.Width;
			_svgHeight = svgSize.Height * _svgScale;
			_svgMatrix = SKMatrix.MakeScale(_svgScale, _svgScale);
			var skewedWidth = SkewedWidth(_svgWidth, _svgHeight);
			_svgInfo = new SKImageInfo((int)skewedWidth, (int)_svgHeight);
		}

		private SKSurface RasterizeSegment(SKPicture segment, SKPaint paint)
		{
			var surface = SKSurface.Create(_svgInfo);
			surface.Canvas.Translate(_svgInfo.Width - _svgWidth, 0);
			Skew(surface.Canvas, SkewAngle, 0);
			surface.Canvas.DrawPicture(segment, ref _svgMatrix, paint);
			return surface;
		}

		private static void Skew(SKCanvas canvas, double xDegrees, double yDegrees)
		{
			canvas.Skew((float)Math.Tan(Math.PI * xDegrees / 180), (float)Math.Tan(Math.PI * yDegrees / 180));
		}

		private static float SkewedWidth(float width, float height)
		{
			var skew = (float)Math.Tan(Math.PI * SkewAngle / 180);
			return width + Math.Abs(skew * height);
		}
	}
}