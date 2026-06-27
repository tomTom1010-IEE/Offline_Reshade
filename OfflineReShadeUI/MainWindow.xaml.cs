using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace OfflineReShade.UI
{
	public partial class MainWindow : Window
	{
		private readonly string _repoRoot;
		private readonly string _prototypePath;
		private Process _previewProcess;
		private IntPtr _previewChildHandle;

		public MainWindow()
		{
			InitializeComponent();

			_repoRoot = FindRepoRoot();
			_prototypePath = FindPrototypePath(_repoRoot);

			var exportDir = @"D:\Program Files\KoikatuSunshine\UserData\cap\OfflineReShade";
			ColorPathBox.Text = Path.Combine(exportDir, "coloroutput.png");
			DepthPathBox.Text = Path.Combine(exportDir, "depthoutput.png");
			OutputPathBox.Text = Path.Combine(exportDir, "reshadeoutput.png");
			EffectDirBox.Text = Path.Combine(_repoRoot, "bin", "x64", "Release", "OfflinePrototype", "Effects");
			PreviewPanel.TabStop = true;
			OverlayPanel.TabStop = true;
			PreviewPanel.MouseDown += (_, __) => FocusPreviewChildWindow();
			OverlayPanel.MouseDown += (_, e) => ForwardOverlayMouse(e, MouseDownMessage(e.Button));
			OverlayPanel.MouseUp += (_, e) => ForwardOverlayMouse(e, MouseUpMessage(e.Button));
			OverlayPanel.MouseMove += (_, e) => ForwardOverlayMouse(e, WM_MOUSEMOVE);
			OverlayPanel.MouseWheel += ForwardOverlayMouseWheel;
			OverlayPanel.KeyDown += ForwardOverlayKeyDown;
			OverlayPanel.KeyUp += ForwardOverlayKeyUp;
			PreviewKeyDown += ForwardPreviewKeyDown;
			PreviewKeyUp += ForwardPreviewKeyUp;

			RefreshOutputInfo();
		}

		private async void SavePngClick(object sender, RoutedEventArgs e)
		{
			SaveButton.IsEnabled = false;
			StatusText.Text = "Saving";
			LogBox.Clear();

			try
			{
				var result = await RunPrototypeAsync(BuildExportArguments());
				AppendLog(result);
				RefreshOutputInfo();
				StatusText.Text = "Saved";
			}
			catch (Exception ex)
			{
				AppendLog(ex.Message);
				StatusText.Text = "Failed";
			}
			finally
			{
				SaveButton.IsEnabled = true;
			}
		}

		private void StartPreviewClick(object sender, RoutedEventArgs e)
		{
			try
			{
				StopPreviewProcess();

				if (!File.Exists(_prototypePath))
					throw new FileNotFoundException("OfflineReShadePrototype.exe was not found.", _prototypePath);

				var panelHandle = PreviewPanel.Handle;
				var overlayHandle = OverlayPanel.Handle;
				if (panelHandle == IntPtr.Zero || overlayHandle == IntPtr.Zero)
					throw new InvalidOperationException("Preview or overlay panel handle is not available yet.");

				LogBox.Clear();
				var startInfo = new ProcessStartInfo
				{
					FileName = _prototypePath,
					Arguments = BuildPreviewArguments(panelHandle, overlayHandle),
					WorkingDirectory = Path.GetDirectoryName(_prototypePath),
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};

				_previewProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
				_previewProcess.OutputDataReceived += (_, args) => AppendLogFromProcess(args.Data);
				_previewProcess.ErrorDataReceived += (_, args) => AppendLogFromProcess(args.Data);
				_previewProcess.Exited += (_, __) => Dispatcher.BeginInvoke(new Action(() =>
				{
					StartPreviewButton.IsEnabled = true;
					StopPreviewButton.IsEnabled = false;
					StatusText.Text = "Preview stopped";
				}));

				_previewProcess.Start();
				_previewProcess.BeginOutputReadLine();
				_previewProcess.BeginErrorReadLine();

				Task.Delay(300).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(FocusPreviewChildWindow)));

				StartPreviewButton.IsEnabled = false;
				StopPreviewButton.IsEnabled = true;
				StatusText.Text = "Preview running";
				PreviewInfoText.Text = "Full-res ReShade runtime - preview is scaled, press Home for overlay";
			}
			catch (Exception ex)
			{
				AppendLog(ex.Message);
				StatusText.Text = "Failed";
				StartPreviewButton.IsEnabled = true;
				StopPreviewButton.IsEnabled = false;
			}
		}

		private void StopPreviewClick(object sender, RoutedEventArgs e)
		{
			StopPreviewProcess();
			StatusText.Text = "Preview stopped";
		}

		private Task<string> RunPrototypeAsync(string arguments)
		{
			return Task.Run(() =>
			{
				if (!File.Exists(_prototypePath))
					throw new FileNotFoundException("OfflineReShadePrototype.exe was not found.", _prototypePath);

				var startInfo = new ProcessStartInfo
				{
					FileName = _prototypePath,
					Arguments = arguments,
					WorkingDirectory = Path.GetDirectoryName(_prototypePath),
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};

				using (var process = Process.Start(startInfo))
				{
					var output = process.StandardOutput.ReadToEnd();
					var error = process.StandardError.ReadToEnd();
					process.WaitForExit();

					var log = new StringBuilder();
					if (!string.IsNullOrWhiteSpace(output))
						log.AppendLine(output.TrimEnd());
					if (!string.IsNullOrWhiteSpace(error))
						log.AppendLine(error.TrimEnd());
					log.AppendLine("ExitCode=" + process.ExitCode.ToString());

					if (process.ExitCode != 0)
						throw new InvalidOperationException(log.ToString());

					return log.ToString();
				}
			});
		}

		private string BuildExportArguments()
		{
			var args = BuildCommonArguments();
			AddArg(args, "--output", OutputPathBox.Text);
			AddArg(args, "--width", WidthBox.Text);
			AddArg(args, "--height", HeightBox.Text);
			return args.ToString();
		}

		private string BuildPreviewArguments(IntPtr panelHandle, IntPtr overlayHandle)
		{
			var args = BuildCommonArguments();
			AddArg(args, "--parent-hwnd", panelHandle.ToInt64().ToString());
			AddArg(args, "--overlay-hwnd", overlayHandle.ToInt64().ToString());
			AddSwitch(args, "--interactive");
			AddArg(args, "--output", OutputPathBox.Text);

			AddArg(args, "--width", WidthBox.Text);
			AddArg(args, "--height", HeightBox.Text);
			return args.ToString();
		}

		private StringBuilder BuildCommonArguments()
		{
			var args = new StringBuilder();
			AddArg(args, "--color", ColorPathBox.Text);
			AddArg(args, "--depth", DepthPathBox.Text);
			AddArg(args, "--effect-dir", EffectDirBox.Text);
			AddArg(args, "--preset", PresetPathBox.Text);
			return args;
		}
		private void ReShadeScreenshotClick(object sender, RoutedEventArgs e)
		{
			SendKeyToPreview(0x2C);
		}

		private void ForwardPreviewKeyDown(object sender, KeyEventArgs e)
		{
			ForwardPreviewKey(e, true);
		}

		private void ForwardPreviewKeyUp(object sender, KeyEventArgs e)
		{
			ForwardPreviewKey(e, false);
		}
		private void ForwardOverlayKeyDown(object sender, Forms.KeyEventArgs e)
		{
			ForwardOverlayKey(e, true);
		}

		private void ForwardOverlayKeyUp(object sender, Forms.KeyEventArgs e)
		{
			ForwardOverlayKey(e, false);
		}

		private void ForwardOverlayKey(Forms.KeyEventArgs e, bool keyDown)
		{
			if (_previewProcess == null || _previewProcess.HasExited)
				return;

			PostKeyMessageToPreview(e.KeyValue, keyDown);
			e.Handled = true;
		}

		private void ForwardPreviewKey(KeyEventArgs e, bool keyDown)
		{
			if (_previewProcess == null || _previewProcess.HasExited || IsTextBoxFocused())
				return;

			var key = e.Key == Key.System ? e.SystemKey : e.Key;
			if (key == Key.ImeProcessed)
				key = e.ImeProcessedKey;

			var vk = KeyInterop.VirtualKeyFromKey(key);
			if (vk == 0)
				return;

			PostKeyMessageToPreview(vk, keyDown);
			e.Handled = true;
		}

		private void SendKeyToPreview(int vk)
		{
			if (_previewProcess == null || _previewProcess.HasExited)
				return;

			FocusPreviewChildWindow();
			PostKeyMessageToPreview(vk, true);
			PostKeyMessageToPreview(vk, false);
		}

		private void PostKeyMessageToPreview(int vk, bool keyDown)
		{
			if (!TryGetPreviewChildWindow(out var hwnd))
				return;

			var scanCode = MapVirtualKey((uint)vk, 0);
			var lparam = 1 | ((int)scanCode << 16);
			if (!keyDown)
				lparam |= unchecked((int)0xC0000000);

			PostMessage(hwnd, keyDown ? WM_KEYDOWN : WM_KEYUP, new IntPtr(vk), new IntPtr(lparam));
		}
		private void ForwardOverlayMouse(Forms.MouseEventArgs e, int message)
		{
			if (_previewProcess == null || _previewProcess.HasExited)
				return;
			if (!TryGetPreviewChildWindow(out var hwnd))
				return;

			OverlayPanel.Focus();
			PostMessage(hwnd, message, new IntPtr(MouseKeyState()), MakeMouseLParam(e.X, e.Y));
		}

		private void ForwardOverlayMouseWheel(object sender, Forms.MouseEventArgs e)
		{
			if (_previewProcess == null || _previewProcess.HasExited)
				return;
			if (!TryGetPreviewChildWindow(out var hwnd))
				return;

			OverlayPanel.Focus();
			var wparam = (e.Delta << 16) | (MouseKeyState() & 0xffff);
			PostMessage(hwnd, WM_MOUSEWHEEL, new IntPtr(wparam), MakeMouseLParam(e.X, e.Y));
		}

		private static int MouseDownMessage(Forms.MouseButtons button)
		{
			if (button == Forms.MouseButtons.Left)
				return WM_LBUTTONDOWN;
			if (button == Forms.MouseButtons.Right)
				return WM_RBUTTONDOWN;
			if (button == Forms.MouseButtons.Middle)
				return WM_MBUTTONDOWN;
			return WM_MOUSEMOVE;
		}

		private static int MouseUpMessage(Forms.MouseButtons button)
		{
			if (button == Forms.MouseButtons.Left)
				return WM_LBUTTONUP;
			if (button == Forms.MouseButtons.Right)
				return WM_RBUTTONUP;
			if (button == Forms.MouseButtons.Middle)
				return WM_MBUTTONUP;
			return WM_MOUSEMOVE;
		}

		private static int MouseKeyState()
		{
			var state = 0;
			if ((Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0)
				state |= MK_LBUTTON;
			if ((Forms.Control.MouseButtons & Forms.MouseButtons.Right) != 0)
				state |= MK_RBUTTON;
			if ((Forms.Control.MouseButtons & Forms.MouseButtons.Middle) != 0)
				state |= MK_MBUTTON;
			if ((Forms.Control.ModifierKeys & Forms.Keys.Shift) != 0)
				state |= MK_SHIFT;
			if ((Forms.Control.ModifierKeys & Forms.Keys.Control) != 0)
				state |= MK_CONTROL;
			return state;
		}

		private static IntPtr MakeMouseLParam(int x, int y)
		{
			return new IntPtr((x & 0xffff) | ((y & 0xffff) << 16));
		}

		private void FocusPreviewChildWindow()
		{
			PreviewPanel.Focus();
			if (TryGetPreviewChildWindow(out var hwnd))
				SetFocus(hwnd);
		}

		private bool TryGetPreviewChildWindow(out IntPtr hwnd)
		{
			if (_previewChildHandle != IntPtr.Zero && IsWindow(_previewChildHandle))
			{
				hwnd = _previewChildHandle;
				return true;
			}

			_previewChildHandle = IntPtr.Zero;
			EnumChildWindows(PreviewPanel.Handle, (child, _) =>
			{
				_previewChildHandle = child;
				return false;
			}, IntPtr.Zero);

			hwnd = _previewChildHandle;
			return hwnd != IntPtr.Zero && IsWindow(hwnd);
		}

		private static bool IsTextBoxFocused()
		{
			return Keyboard.FocusedElement is System.Windows.Controls.TextBox;
		}

		private void BrowseColorClick(object sender, RoutedEventArgs e) => BrowseFile(ColorPathBox, "PNG files|*.png|All files|*.*");
		private void BrowseDepthClick(object sender, RoutedEventArgs e) => BrowseFile(DepthPathBox, "PNG files|*.png|All files|*.*");
		private void BrowsePresetClick(object sender, RoutedEventArgs e) => BrowseFile(PresetPathBox, "INI files|*.ini|All files|*.*");

		private void BrowseOutputClick(object sender, RoutedEventArgs e)
		{
			var dialog = new SaveFileDialog { Filter = "PNG files|*.png|All files|*.*", FileName = Path.GetFileName(OutputPathBox.Text) };
			if (!string.IsNullOrWhiteSpace(OutputPathBox.Text))
				dialog.InitialDirectory = Path.GetDirectoryName(OutputPathBox.Text);
			if (dialog.ShowDialog(this) == true)
				OutputPathBox.Text = dialog.FileName;
		}

		private void BrowseEffectClick(object sender, RoutedEventArgs e)
		{
			using (var dialog = new Forms.FolderBrowserDialog())
			{
				dialog.SelectedPath = Directory.Exists(EffectDirBox.Text) ? EffectDirBox.Text : _repoRoot;
				if (dialog.ShowDialog() == Forms.DialogResult.OK)
					EffectDirBox.Text = dialog.SelectedPath;
			}
		}

		private void OpenOutputClick(object sender, RoutedEventArgs e)
		{
			if (File.Exists(OutputPathBox.Text))
				Process.Start(new ProcessStartInfo(OutputPathBox.Text) { UseShellExecute = true });
		}

		private void RefreshOutputClick(object sender, RoutedEventArgs e) => RefreshOutputInfo();

		private void RefreshOutputInfo()
		{
			var path = OutputPathBox.Text;
			if (!File.Exists(path))
			{
				PreviewInfoText.Text = "Embedded ReShade Preview";
				return;
			}

			using (var stream = File.OpenRead(path))
			{
				var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
				var frame = decoder.Frames[0];
				PreviewInfoText.Text = Path.GetFileName(path) + "  " + frame.PixelWidth + "x" + frame.PixelHeight;
			}
		}

		private void StopPreviewProcess()
		{
			var process = _previewProcess;
			_previewProcess = null;
			if (process == null)
				return;

			try
			{
				if (!process.HasExited)
				{
					process.Kill();
					process.WaitForExit(2000);
				}
			}
			catch (InvalidOperationException)
			{
			}
			finally
			{
				process.Dispose();
				StartPreviewButton.IsEnabled = true;
				StopPreviewButton.IsEnabled = false;
			}
		}

		private void AppendLogFromProcess(string line)
		{
			if (string.IsNullOrWhiteSpace(line))
				return;
			Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
		}

		private void AppendLog(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return;
			if (LogBox.Text.Length != 0)
				LogBox.AppendText(Environment.NewLine);
			LogBox.AppendText(text.TrimEnd());
			LogBox.ScrollToEnd();
		}

		protected override void OnClosed(EventArgs e)
		{
			StopPreviewProcess();
			base.OnClosed(e);
		}
		private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

		private const int WM_KEYDOWN = 0x0100;
		private const int WM_KEYUP = 0x0101;
		private const int WM_MOUSEMOVE = 0x0200;
		private const int WM_LBUTTONDOWN = 0x0201;
		private const int WM_LBUTTONUP = 0x0202;
		private const int WM_RBUTTONDOWN = 0x0204;
		private const int WM_RBUTTONUP = 0x0205;
		private const int WM_MBUTTONDOWN = 0x0207;
		private const int WM_MBUTTONUP = 0x0208;
		private const int WM_MOUSEWHEEL = 0x020A;
		private const int MK_LBUTTON = 0x0001;
		private const int MK_RBUTTON = 0x0002;
		private const int MK_SHIFT = 0x0004;
		private const int MK_CONTROL = 0x0008;
		private const int MK_MBUTTON = 0x0010;

		[DllImport("user32.dll")]
		private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc callback, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern IntPtr SetFocus(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern uint MapVirtualKey(uint code, uint mapType);

		private static void BrowseFile(System.Windows.Controls.TextBox target, string filter)
		{
			var dialog = new OpenFileDialog { Filter = filter };
			if (!string.IsNullOrWhiteSpace(target.Text))
				dialog.InitialDirectory = File.Exists(target.Text) ? Path.GetDirectoryName(target.Text) : target.Text;
			if (dialog.ShowDialog() == true)
				target.Text = dialog.FileName;
		}

		private static void AddSwitch(StringBuilder builder, string name)
		{
			if (builder.Length != 0)
				builder.Append(' ');
			builder.Append(name);
		}

		private static void AddArg(StringBuilder builder, string name, string value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return;
			if (builder.Length != 0)
				builder.Append(' ');
			builder.Append(name).Append(' ').Append('"').Append(value.Replace("\"", "\\\"")).Append('"');
		}

		private static int ParsePositiveInt(string value)
		{
			int parsed;
			return int.TryParse(value, out parsed) && parsed > 0 ? parsed : 0;
		}

		private static string FindRepoRoot()
		{
			var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
			while (dir != null)
			{
				if (File.Exists(Path.Combine(dir.FullName, "ReShade.sln")))
					return dir.FullName;
				dir = dir.Parent;
			}
			return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
		}

		private static string FindPrototypePath(string repoRoot)
		{
			var release = Path.Combine(repoRoot, "bin", "x64", "Release", "OfflineReShadePrototype.exe");
			if (File.Exists(release))
				return release;
			return Path.Combine(repoRoot, "bin", "x64", "Debug", "OfflineReShadePrototype.exe");
		}
	}
}









