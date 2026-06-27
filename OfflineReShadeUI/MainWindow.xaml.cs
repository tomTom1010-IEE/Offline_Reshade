using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace OfflineReShade.UI
{
	public partial class MainWindow : Window
	{
		private readonly string _repoRoot;
		private readonly string _prototypePath;
		private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
		private readonly SemaphoreSlim _rpcLock = new SemaphoreSlim(1, 1);
		private Process _previewProcess;
		private NamedPipeClientStream _controlPipe;
		private StreamReader _pipeReader;
		private StreamWriter _pipeWriter;
		private int _rpcId;
		private bool _buildingControls;
		private readonly object _uniformUpdateLock = new object();
		private readonly Dictionary<string, object> _pendingUniformValues = new Dictionary<string, object>();
		private readonly Dictionary<string, CancellationTokenSource> _activeUniformUpdates = new Dictionary<string, CancellationTokenSource>();

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
			PreviewPanel.MouseDown += (_, __) => PreviewPanel.Focus();
			RenderDisconnectedControls();
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

		private async void StartPreviewClick(object sender, RoutedEventArgs e)
		{
			try
			{
				StopPreviewProcess();

				if (!File.Exists(_prototypePath))
					throw new FileNotFoundException("OfflineReShadePrototype.exe was not found.", _prototypePath);

				var panelHandle = PreviewPanel.Handle;
				if (panelHandle == IntPtr.Zero)
					throw new InvalidOperationException("Preview panel handle is not available yet.");

				var pipeName = "OfflineReShade-" + Guid.NewGuid().ToString("N");
				LogBox.Clear();
				RenderConnectingControls();

				var startInfo = new ProcessStartInfo
				{
					FileName = _prototypePath,
					Arguments = BuildPreviewArguments(panelHandle, pipeName),
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
					ControlStatusText.Text = "Disconnected";
				}));

				_previewProcess.Start();
				_previewProcess.BeginOutputReadLine();
				_previewProcess.BeginErrorReadLine();

				StartPreviewButton.IsEnabled = false;
				StopPreviewButton.IsEnabled = true;
				StatusText.Text = "Preview running";
				PreviewInfoText.Text = "Full-res ReShade runtime - preview is scaled";

				await ConnectControlPipeAsync(pipeName);
				await RefreshControlStateAsync();
			}
			catch (Exception ex)
			{
				AppendLog(ex.Message);
				StatusText.Text = "Failed";
				ControlStatusText.Text = "Control failed";
				StartPreviewButton.IsEnabled = true;
				StopPreviewButton.IsEnabled = false;
			}
		}

		private void StopPreviewClick(object sender, RoutedEventArgs e)
		{
			StopPreviewProcess();
			StatusText.Text = "Preview stopped";
		}

		private async void ReShadeScreenshotClick(object sender, RoutedEventArgs e)
		{
			try
			{
				await SendRpcAsync("save_screenshot", new Dictionary<string, object>());
				AppendLog("Screenshot requested.");
			}
			catch (Exception ex)
			{
				AppendLog(ex.Message);
			}
		}

		private async Task ConnectControlPipeAsync(string pipeName)
		{
			CloseControlPipe();
			ControlStatusText.Text = "Connecting";
			_controlPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			await Task.Run(() => _controlPipe.Connect(5000));
			_pipeReader = new StreamReader(_controlPipe, new UTF8Encoding(false));
			_pipeWriter = new StreamWriter(_controlPipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
			ControlStatusText.Text = "Connected";
		}

		private async Task RefreshControlStateAsync()
		{
			Dictionary<string, object> state = null;
			for (var attempt = 0; attempt < 20; ++attempt)
			{
				state = await SendRpcAsync("list_state", new Dictionary<string, object>());
				if (AsArray(state.ContainsKey("techniques") ? state["techniques"] : null).Length != 0 || AsArray(state.ContainsKey("uniforms") ? state["uniforms"] : null).Length != 0)
					break;
				await Task.Delay(250);
			}
			BuildControls(state ?? new Dictionary<string, object>());
		}

		private async Task<Dictionary<string, object>> SendRpcAsync(string method, Dictionary<string, object> parameters)
		{
			if (_pipeWriter == null || _pipeReader == null)
				throw new InvalidOperationException("Control pipe is not connected.");

			await _rpcLock.WaitAsync();
			try
			{
				var request = new Dictionary<string, object>
				{
					{ "id", ++_rpcId },
					{ "method", method },
					{ "params", parameters ?? new Dictionary<string, object>() }
				};
				await _pipeWriter.WriteLineAsync(_json.Serialize(request));
				var line = await _pipeReader.ReadLineAsync();
				if (line == null)
					throw new IOException("Control pipe closed.");
				var response = AsDict(_json.DeserializeObject(line));
				if (!response.ContainsKey("ok") || !(bool)response["ok"])
				{
					var error = response.ContainsKey("error") ? AsDict(response["error"]) : new Dictionary<string, object>();
					throw new InvalidOperationException(error.ContainsKey("message") ? Convert.ToString(error["message"]) : "ReShade control command failed.");
				}
				return response.ContainsKey("result") ? AsDict(response["result"]) : new Dictionary<string, object>();
			}
			finally
			{
				_rpcLock.Release();
			}
		}

		private void BuildControls(Dictionary<string, object> state)
		{
			_buildingControls = true;
			ControlsPanel.Children.Clear();
			try
			{
				var runtime = state.ContainsKey("runtime") ? AsDict(state["runtime"]) : new Dictionary<string, object>();
				var effectsEnabled = !runtime.ContainsKey("effectsEnabled") || Convert.ToBoolean(runtime["effectsEnabled"]);

				var commandPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
				var effectsBox = new CheckBox { Content = "Effects Enabled", IsChecked = effectsEnabled, Margin = new Thickness(0, 0, 12, 8), VerticalAlignment = VerticalAlignment.Center };
				effectsBox.Checked += async (_, __) => { if (!_buildingControls) await SendRpcSafeAsync("set_effects_state", "enabled", true); };
				effectsBox.Unchecked += async (_, __) => { if (!_buildingControls) await SendRpcSafeAsync("set_effects_state", "enabled", false); };
				commandPanel.Children.Add(effectsBox);
				commandPanel.Children.Add(MakeCommandButton("Reload", async () => { await SendRpcAsync("reload_effects", new Dictionary<string, object>()); await Task.Delay(500); await RefreshControlStateAsync(); }));
				commandPanel.Children.Add(MakeCommandButton("Save Preset", async () => await SendRpcAsync("save_preset", new Dictionary<string, object>())));
				ControlsPanel.Children.Add(commandPanel);

				var enabledEffectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				var enabledEffectAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				var enabledEffectOrder = new List<string>();
				var techniquesPanel = new StackPanel();
				foreach (var item in AsArray(state.ContainsKey("techniques") ? state["techniques"] : null))
				{
					var technique = AsDict(item);
					var id = Convert.ToString(technique["id"]);
					var effectName = Convert.ToString(technique["effectName"]);
					var isEnabled = Convert.ToBoolean(technique["enabled"]);
					if (isEnabled && enabledEffectNames.Add(effectName))
					{
						enabledEffectOrder.Add(effectName);
						enabledEffectAliases[NormalizeEffectName(effectName)] = effectName;
					}
					var label = Convert.ToString(technique["name"]) + " [" + effectName + "]";
					var checkBox = new CheckBox { Content = label, IsChecked = isEnabled, Tag = id, Margin = new Thickness(0, 0, 0, 4) };
					checkBox.Checked += async (_, __) =>
					{
						if (_buildingControls) return;
						await SetTechniqueStateAsync((string)checkBox.Tag, true);
						await RefreshControlStateAsync();
					};
					checkBox.Unchecked += async (_, __) =>
					{
						if (_buildingControls) return;
						await SetTechniqueStateAsync((string)checkBox.Tag, false);
						await RefreshControlStateAsync();
					};
					techniquesPanel.Children.Add(checkBox);
				}
				ControlsPanel.Children.Add(new Expander { Header = "Techniques", IsExpanded = true, Content = techniquesPanel, Margin = new Thickness(0, 0, 0, 12) });

				var uniformsByEffect = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
				foreach (var item in AsArray(state.ContainsKey("uniforms") ? state["uniforms"] : null))
				{
					var uniform = AsDict(item);
					var effectName = uniform.ContainsKey("effectName") ? Convert.ToString(uniform["effectName"]) : "Unknown Effect";
					var panelEffectName = ResolveEnabledEffectName(effectName, enabledEffectNames, enabledEffectAliases);
					if (panelEffectName == null)
						continue;
					if (!uniformsByEffect.TryGetValue(panelEffectName, out var list))
					{
						list = new List<Dictionary<string, object>>();
						uniformsByEffect.Add(panelEffectName, list);
					}
					list.Add(uniform);
				}

				var preprocessorByEffect = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
				foreach (var item in AsArray(state.ContainsKey("preprocessorDefinitions") ? state["preprocessorDefinitions"] : null))
				{
					var definition = AsDict(item);
					var effectName = definition.ContainsKey("effectName") ? Convert.ToString(definition["effectName"]) : "Unknown Effect";
					var panelEffectName = ResolveEnabledEffectName(effectName, enabledEffectNames, enabledEffectAliases);
					if (panelEffectName == null)
						continue;
					if (!preprocessorByEffect.TryGetValue(panelEffectName, out var list))
					{
						list = new List<Dictionary<string, object>>();
						preprocessorByEffect.Add(panelEffectName, list);
					}
					list.Add(definition);
				}

				var effectsPanel = new StackPanel();
				foreach (var effectName in enabledEffectOrder)
				{
					uniformsByEffect.TryGetValue(effectName, out var uniforms);
					preprocessorByEffect.TryGetValue(effectName, out var definitions);
					if ((uniforms == null || uniforms.Count == 0) && (definitions == null || definitions.Count == 0))
						continue;
					effectsPanel.Children.Add(new Expander
					{
						Header = effectName,
						IsExpanded = false,
						Content = MakeEffectUniformPanel(uniforms ?? new List<Dictionary<string, object>>(), definitions ?? new List<Dictionary<string, object>>()),
						Margin = new Thickness(0, 0, 0, 8)
					});
				}
				ControlsPanel.Children.Add(new Expander { Header = "Enabled Effect Controls", IsExpanded = true, Content = effectsPanel });
				ControlStatusText.Text = "Ready";
			}
			finally
			{
				_buildingControls = false;
			}
		}
		
		private static string ResolveEnabledEffectName(string effectName, HashSet<string> enabledEffectNames, Dictionary<string, string> enabledEffectAliases)
		{
			if (enabledEffectNames.Contains(effectName))
				return effectName;
			return enabledEffectAliases.TryGetValue(NormalizeEffectName(effectName), out var enabledEffectName) ? enabledEffectName : null;
		}

		private static string NormalizeEffectName(string effectName)
		{
			if (string.IsNullOrEmpty(effectName))
				return string.Empty;
			var builder = new StringBuilder(effectName.Length);
			foreach (var ch in effectName)
			{
				if (char.IsLetterOrDigit(ch))
					builder.Append(char.ToLowerInvariant(ch));
			}
			return builder.ToString();
		}
		private Button MakeCommandButton(string label, Func<Task> action)
		{
			var button = new Button { Content = label, MinWidth = 92, Height = 28, Margin = new Thickness(0, 0, 8, 8) };
			button.Click += async (_, __) =>
			{
				try { await action(); }
				catch (Exception ex) { AppendLog(ex.Message); }
			};
			return button;
		}

		private FrameworkElement MakeEffectUniformPanel(List<Dictionary<string, object>> uniforms, List<Dictionary<string, object>> preprocessorDefinitions)
		{
			var panel = new StackPanel();
			if (preprocessorDefinitions.Count != 0)
			{
				var preprocessorPanel = new StackPanel();
				foreach (var definition in preprocessorDefinitions)
					preprocessorPanel.Children.Add(MakePreprocessorControl(definition));
				panel.Children.Add(new Expander { Header = "Preprocessor Definitions", IsExpanded = false, Content = preprocessorPanel, Margin = new Thickness(0, 0, 0, 8) });
			}

			var byCategory = new Dictionary<string, StackPanel>();
			var order = new List<string>();
			foreach (var uniform in uniforms)
			{
				var category = uniform.ContainsKey("category") ? Convert.ToString(uniform["category"]) : "General";
				if (string.IsNullOrWhiteSpace(category))
					category = "General";
				if (!byCategory.TryGetValue(category, out var categoryPanel))
				{
					categoryPanel = new StackPanel();
					byCategory.Add(category, categoryPanel);
					order.Add(category);
				}
				categoryPanel.Children.Add(MakeUniformControl(uniform));
			}

			foreach (var category in order)
			{
				if (category == "General" && order.Count == 1 && preprocessorDefinitions.Count == 0)
				{
					panel.Children.Add(byCategory[category]);
				}
				else
				{
					panel.Children.Add(new Expander { Header = category, IsExpanded = true, Content = byCategory[category], Margin = new Thickness(0, 0, 0, 8) });
				}
			}
			return panel;
		}

		private FrameworkElement MakePreprocessorControl(Dictionary<string, object> definition)
		{
			var effectName = definition.ContainsKey("effectName") ? Convert.ToString(definition["effectName"]) : string.Empty;
			var name = definition.ContainsKey("name") ? Convert.ToString(definition["name"]) : string.Empty;
			var value = definition.ContainsKey("value") ? Convert.ToString(definition["value"]) : string.Empty;
			var defaultValue = definition.ContainsKey("defaultValue") ? Convert.ToString(definition["defaultValue"]) : string.Empty;
			var group = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
			group.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
			var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
			var button = new Button { Content = "Apply", Width = 72, Height = 24, Margin = new Thickness(8, 0, 0, 0) };
			DockPanel.SetDock(button, Dock.Right);
			row.Children.Add(button);
			var text = new TextBox { Text = value, MinWidth = 120 };
			row.Children.Add(text);
			button.Click += async (_, __) =>
			{
				try
				{
					await SetPreprocessorDefinitionAsync(effectName, name, text.Text);
					await Task.Delay(500);
					await RefreshControlStateAsync();
				}
				catch (Exception ex)
				{
					AppendLog(ex.Message);
				}
			};
			group.Children.Add(row);
			if (!string.IsNullOrEmpty(defaultValue))
				group.Children.Add(new TextBlock { Text = "Default: " + defaultValue, Foreground = System.Windows.Media.Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
			return group;
		}

		private FrameworkElement MakeUniformControl(Dictionary<string, object> uniform)
		{
			var id = Convert.ToString(uniform["id"]);
			var name = uniform.ContainsKey("label") ? Convert.ToString(uniform["label"]) : Convert.ToString(uniform["name"]);
			var type = Convert.ToString(uniform["type"]);
			var uiType = uniform.ContainsKey("uiType") ? Convert.ToString(uniform["uiType"]) : string.Empty;
			var values = AsArray(uniform.ContainsKey("value") ? uniform["value"] : null);
			var items = AsArray(uniform.ContainsKey("items") ? uniform["items"] : null);
			var group = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
			group.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

			if ((uiType == "combo" || uiType == "list" || uiType == "radio") && items.Length != 0)
			{
				var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 0), MinWidth = 160 };
				foreach (var item in items)
					combo.Items.Add(Convert.ToString(item));
				combo.SelectedIndex = Math.Max(0, Math.Min(combo.Items.Count - 1, values.Length == 0 ? 0 : Convert.ToInt32(values[0])));
				combo.SelectionChanged += async (_, __) =>
				{
					if (_buildingControls || combo.SelectedIndex < 0) return;
					await SetUniformAsync(id, combo.SelectedIndex);
				};
				group.Children.Add(combo);
				return group;
			}

			if (type == "bool")
			{
				var checkBox = new CheckBox { Content = Convert.ToString(uniform["effectName"]), IsChecked = values.Length != 0 && Convert.ToBoolean(values[0]), Margin = new Thickness(0, 4, 0, 0) };
				checkBox.Checked += async (_, __) => { if (!_buildingControls) await SetUniformAsync(id, true); };
				checkBox.Unchecked += async (_, __) => { if (!_buildingControls) await SetUniformAsync(id, false); };
				group.Children.Add(checkBox);
				return group;
			}

			var numericValues = new List<double>();
			foreach (var value in values)
				numericValues.Add(Convert.ToDouble(value));
			if (numericValues.Count == 0)
				numericValues.Add(0.0);

			var componentBoxes = new List<TextBox>();
			for (var i = 0; i < numericValues.Count; ++i)
			{
				var componentIndex = i;
				var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
				var text = new TextBox { Width = 72, Text = numericValues[i].ToString("0.######") };
				componentBoxes.Add(text);
				DockPanel.SetDock(text, Dock.Right);
				row.Children.Add(text);

				var minimum = uniform.ContainsKey("min") ? Convert.ToDouble(uniform["min"]) : numericValues[i] - Math.Max(1.0, Math.Abs(numericValues[i]) * 2.0);
				var maximum = uniform.ContainsKey("max") ? Convert.ToDouble(uniform["max"]) : numericValues[i] + Math.Max(1.0, Math.Abs(numericValues[i]) * 2.0);
				if (Math.Abs(maximum - minimum) < 0.000001)
					maximum = minimum + 1.0;
				var slider = new Slider
				{
					Minimum = minimum,
					Maximum = maximum,
					Value = Math.Max(minimum, Math.Min(maximum, numericValues[i])),
					TickFrequency = uniform.ContainsKey("step") ? Math.Max(0.000001, Convert.ToDouble(uniform["step"])) : (type == "float" ? 0.0 : 1.0),
					IsSnapToTickEnabled = type != "float",
					Margin = new Thickness(0, 0, 8, 0)
				};
				slider.ValueChanged += (_, __) =>
				{
					if (_buildingControls) return;
					text.Text = slider.Value.ToString("0.######");
					QueueUniformUpdate(id, BuildNumericUniformValue(componentBoxes, componentIndex, slider.Value, numericValues.Count));
				};
				row.Children.Add(slider);

				text.LostFocus += async (_, __) => { if (TryBuildNumericUniformValue(componentBoxes, out var next)) await SetUniformAsync(id, next); };
				text.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && TryBuildNumericUniformValue(componentBoxes, out var next)) await SetUniformAsync(id, next); };
				group.Children.Add(row);
			}

			return group;
		}

		private object BuildNumericUniformValue(List<TextBox> boxes, int changedIndex, double changedValue, int componentCount)
		{
			var values = new double[componentCount];
			for (var i = 0; i < componentCount; ++i)
			{
				if (i == changedIndex)
					values[i] = changedValue;
				else if (!double.TryParse(boxes[i].Text, out values[i]))
					values[i] = 0.0;
			}
			return componentCount == 1 ? (object)values[0] : values;
		}

		private static bool TryBuildNumericUniformValue(List<TextBox> boxes, out object value)
		{
			var values = new double[boxes.Count];
			for (var i = 0; i < boxes.Count; ++i)
			{
				if (!double.TryParse(boxes[i].Text, out values[i]))
				{
					value = null;
					return false;
				}
			}
			value = values.Length == 1 ? (object)values[0] : values;
			return true;
		}
		private async Task SetTechniqueStateAsync(string id, bool enabled)
		{
			await SendRpcAsync("set_technique_state", new Dictionary<string, object> { { "id", id }, { "enabled", enabled } });
		}

		private void QueueUniformUpdate(string id, object value)
		{
			CancellationTokenSource source = null;
			lock (_uniformUpdateLock)
			{
				_pendingUniformValues[id] = value;
				if (!_activeUniformUpdates.ContainsKey(id))
				{
					source = new CancellationTokenSource();
					_activeUniformUpdates.Add(id, source);
				}
			}

			if (source != null)
				_ = SendUniformUpdateLoopAsync(id, source);
		}

		private async Task SendUniformUpdateLoopAsync(string id, CancellationTokenSource source)
		{
			try
			{
				while (!source.IsCancellationRequested)
				{
					object value;
					lock (_uniformUpdateLock)
					{
						if (!_pendingUniformValues.TryGetValue(id, out value))
						{
							_activeUniformUpdates.Remove(id);
							return;
						}
						_pendingUniformValues.Remove(id);
					}

					await Task.Delay(16, source.Token);
					await SetUniformAsync(id, value);
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				AppendLog(ex.Message);
			}
			finally
			{
				lock (_uniformUpdateLock)
				{
					if (_activeUniformUpdates.TryGetValue(id, out var existing) && ReferenceEquals(existing, source))
						_activeUniformUpdates.Remove(id);
				}
				source.Dispose();
			}
		}
		private async Task SetUniformAsync(string id, object value)
		{
			await SendRpcAsync("set_uniform", new Dictionary<string, object> { { "id", id }, { "value", value } });
		}

		private async Task SetPreprocessorDefinitionAsync(string effectName, string name, string value)
		{
			await SendRpcAsync("set_preprocessor_definition", new Dictionary<string, object> { { "effectName", effectName }, { "name", name }, { "value", value } });
		}

		private async Task SendRpcSafeAsync(string method, string key, object value)
		{
			try { await SendRpcAsync(method, new Dictionary<string, object> { { key, value } }); }
			catch (Exception ex) { AppendLog(ex.Message); }
		}

		private void RenderDisconnectedControls()
		{
			ControlsPanel.Children.Clear();
			ControlsPanel.Children.Add(new TextBlock { Text = "Start preview to load ReShade controls.", Foreground = System.Windows.Media.Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
			ControlStatusText.Text = "Disconnected";
		}

		private void RenderConnectingControls()
		{
			ControlsPanel.Children.Clear();
			ControlsPanel.Children.Add(new TextBlock { Text = "Connecting to ReShade runtime...", Foreground = System.Windows.Media.Brushes.DimGray });
			ControlStatusText.Text = "Connecting";
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
					if (!string.IsNullOrWhiteSpace(output)) log.AppendLine(output.TrimEnd());
					if (!string.IsNullOrWhiteSpace(error)) log.AppendLine(error.TrimEnd());
					log.AppendLine("ExitCode=" + process.ExitCode.ToString());
					if (process.ExitCode != 0) throw new InvalidOperationException(log.ToString());
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

		private string BuildPreviewArguments(IntPtr panelHandle, string pipeName)
		{
			var args = BuildCommonArguments();
			AddArg(args, "--parent-hwnd", panelHandle.ToInt64().ToString());
			AddArg(args, "--control-pipe", pipeName);
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

		private void BrowseColorClick(object sender, RoutedEventArgs e) => BrowseFile(ColorPathBox, "PNG files|*.png|All files|*.*");
		private void BrowseDepthClick(object sender, RoutedEventArgs e) => BrowseFile(DepthPathBox, "PNG files|*.png|All files|*.*");
		private void BrowsePresetClick(object sender, RoutedEventArgs e) => BrowseFile(PresetPathBox, "INI files|*.ini|All files|*.*");

		private void BrowseOutputClick(object sender, RoutedEventArgs e)
		{
			var dialog = new SaveFileDialog { Filter = "PNG files|*.png|All files|*.*", FileName = Path.GetFileName(OutputPathBox.Text) };
			if (!string.IsNullOrWhiteSpace(OutputPathBox.Text)) dialog.InitialDirectory = Path.GetDirectoryName(OutputPathBox.Text);
			if (dialog.ShowDialog(this) == true) OutputPathBox.Text = dialog.FileName;
		}

		private void BrowseEffectClick(object sender, RoutedEventArgs e)
		{
			using (var dialog = new Forms.FolderBrowserDialog())
			{
				dialog.SelectedPath = Directory.Exists(EffectDirBox.Text) ? EffectDirBox.Text : _repoRoot;
				if (dialog.ShowDialog() == Forms.DialogResult.OK) EffectDirBox.Text = dialog.SelectedPath;
			}
		}

		private void OpenOutputClick(object sender, RoutedEventArgs e)
		{
			if (File.Exists(OutputPathBox.Text)) Process.Start(new ProcessStartInfo(OutputPathBox.Text) { UseShellExecute = true });
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
			CloseControlPipe();
			var process = _previewProcess;
			_previewProcess = null;
			if (process != null)
			{
				try
				{
					if (!process.HasExited)
					{
						process.Kill();
						process.WaitForExit(2000);
					}
				}
				catch (InvalidOperationException) { }
				finally { process.Dispose(); }
			}
			StartPreviewButton.IsEnabled = true;
			StopPreviewButton.IsEnabled = false;
			RenderDisconnectedControls();
		}

		private void CloseControlPipe()
		{
			_pipeWriter?.Dispose();
			_pipeReader?.Dispose();
			_controlPipe?.Dispose();
			_pipeWriter = null;
			_pipeReader = null;
			_controlPipe = null;
		}

		private void AppendLogFromProcess(string line)
		{
			if (string.IsNullOrWhiteSpace(line)) return;
			Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
		}

		private void AppendLog(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return;
			if (LogBox.Text.Length != 0) LogBox.AppendText(Environment.NewLine);
			LogBox.AppendText(text.TrimEnd());
			LogBox.ScrollToEnd();
		}

		protected override void OnClosed(EventArgs e)
		{
			StopPreviewProcess();
			base.OnClosed(e);
		}

		private static Dictionary<string, object> AsDict(object value) => value as Dictionary<string, object> ?? new Dictionary<string, object>();
		private static object[] AsArray(object value) => value as object[] ?? new object[0];

		private static void BrowseFile(System.Windows.Controls.TextBox target, string filter)
		{
			var dialog = new OpenFileDialog { Filter = filter };
			if (!string.IsNullOrWhiteSpace(target.Text)) dialog.InitialDirectory = File.Exists(target.Text) ? Path.GetDirectoryName(target.Text) : target.Text;
			if (dialog.ShowDialog() == true) target.Text = dialog.FileName;
		}

		private static void AddSwitch(StringBuilder builder, string name)
		{
			if (builder.Length != 0) builder.Append(' ');
			builder.Append(name);
		}

		private static void AddArg(StringBuilder builder, string name, string value)
		{
			if (string.IsNullOrWhiteSpace(value)) return;
			if (builder.Length != 0) builder.Append(' ');
			builder.Append(name).Append(' ').Append('"').Append(value.Replace("\"", "\\\"")).Append('"');
		}

		private static string FindRepoRoot()
		{
			var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
			while (dir != null)
			{
				if (File.Exists(Path.Combine(dir.FullName, "ReShade.sln"))) return dir.FullName;
				dir = dir.Parent;
			}
			return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
		}

		private static string FindPrototypePath(string repoRoot)
		{
			var release = Path.Combine(repoRoot, "bin", "x64", "Release", "OfflineReShadePrototype.exe");
			if (File.Exists(release)) return release;
			return Path.Combine(repoRoot, "bin", "x64", "Debug", "OfflineReShadePrototype.exe");
		}
	}
}