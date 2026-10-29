#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
	public class CopyTraderWindow : NTWindow
	{
		private const int MaxLogLines = 500;

		private readonly CopyTraderSettings           settings;
		private readonly ObservableCollection<string> accountNames = new ObservableCollection<string>();
		private readonly ObservableCollection<string> logLines     = new ObservableCollection<string>();

		private ComboBox  leaderBox;
		private TextBox   resyncBox;
		private CheckBox  apexBox;
		private DataGrid  grid;
		private Button    startStopButton;
		private Button    addButton;
		private Button    removeButton;
		private TextBlock statusText;
		private ListBox   logList;

		public CopyTraderWindow()
		{
			Caption = "Copy Trader";
			Width   = 900;
			Height  = 560;

			settings = CopyTraderSettings.Load();
			RefreshAccounts();

			Content = BuildContent();

			CopyTraderEngine.Instance.Log            += OnEngineLog;
			CopyTraderEngine.Instance.RunningChanged += OnRunningChanged;
			Account.AccountStatusUpdate              += OnAccountStatusUpdate;

			Closing += OnWindowClosing;
			Closed  += OnWindowClosed;

			UpdateRunningState();
		}

		#region Layout

		private UIElement BuildContent()
		{
			var root = new Grid { Margin = new Thickness(10) };
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			// Leader row
			var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
			top.Children.Add(Label("Leader account:"));
			leaderBox = new ComboBox { Width = 200, ItemsSource = accountNames, SelectedItem = settings.LeaderAccount, Margin = new Thickness(0, 0, 20, 0) };
			leaderBox.SelectionChanged += (s, e) => { settings.LeaderAccount = leaderBox.SelectedItem as string ?? string.Empty; };
			top.Children.Add(leaderBox);
			top.Children.Add(Label("Resync every (s, 0 = off):"));
			resyncBox = new TextBox { Width = 50, Text = settings.ResyncIntervalSeconds.ToString(), VerticalContentAlignment = VerticalAlignment.Center };
			top.Children.Add(resyncBox);
			apexBox = new CheckBox
			{
				Content           = "Apex rules (no reverse / hedging)",
				IsChecked         = settings.ApexRules,
				VerticalAlignment = VerticalAlignment.Center,
				Margin            = new Thickness(20, 0, 0, 0)
			};
			apexBox.Click += (s, e) => { settings.ApexRules = apexBox.IsChecked == true; };
			top.Children.Add(apexBox);
			Grid.SetRow(top, 0);
			root.Children.Add(top);

			// Followers grid
			grid = new DataGrid
			{
				ItemsSource         = settings.Followers,
				AutoGenerateColumns = false,
				CanUserAddRows      = false,
				CanUserDeleteRows   = false,
				SelectionMode       = DataGridSelectionMode.Single,
				HeadersVisibility   = DataGridHeadersVisibility.Column
			};
			grid.Columns.Add(new DataGridCheckBoxColumn { Header = "On", Binding = TwoWay("Enabled"), Width = 40 });
			grid.Columns.Add(new DataGridComboBoxColumn
			{
				Header              = "Follower account",
				ItemsSource         = accountNames,
				SelectedItemBinding = TwoWay("AccountName"),
				Width               = 200
			});
			grid.Columns.Add(new DataGridTextColumn     { Header = "Ratio",   Binding = TwoWay("Ratio"),       Width = 70 });
			grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Reverse", Binding = TwoWay("Reverse"),     Width = 60 });
			grid.Columns.Add(new DataGridTextColumn     { Header = "Max qty", Binding = TwoWay("MaxQuantity"), Width = 70 });
			grid.Columns.Add(new DataGridTextColumn
			{
				Header  = "Instrument map (e.g. NQ=MNQ; ES=MES)",
				Binding = TwoWay("InstrumentMap"),
				Width   = new DataGridLength(1, DataGridLengthUnitType.Star)
			});
			Grid.SetRow(grid, 1);
			root.Children.Add(grid);

			// Buttons
			var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
			addButton    = MakeButton("Add follower", (s, e) => settings.Followers.Add(new CopyTraderFollower()));
			removeButton = MakeButton("Remove", (s, e) =>
			{
				var selected = grid.SelectedItem as CopyTraderFollower;
				if (selected != null)
					settings.Followers.Remove(selected);
			});
			startStopButton = MakeButton("Start", OnStartStop);
			startStopButton.FontWeight = FontWeights.Bold;

			buttons.Children.Add(addButton);
			buttons.Children.Add(removeButton);
			buttons.Children.Add(startStopButton);
			buttons.Children.Add(MakeButton("Sync now", (s, e) => CopyTraderEngine.Instance.SyncNow()));
			buttons.Children.Add(MakeButton("Stop + flatten followers", OnFlattenFollowers));
			buttons.Children.Add(MakeButton("Refresh accounts", (s, e) => RefreshAccounts()));
			statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), FontWeight = FontWeights.Bold };
			buttons.Children.Add(statusText);
			Grid.SetRow(buttons, 2);
			root.Children.Add(buttons);

			// Log
			logList = new ListBox { ItemsSource = logLines, FontFamily = new FontFamily("Consolas") };
			Grid.SetRow(logList, 3);
			root.Children.Add(logList);

			return root;
		}

		private static TextBlock Label(string text)
		{
			return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
		}

		private static Button MakeButton(string text, RoutedEventHandler onClick)
		{
			var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
			button.Click += onClick;
			return button;
		}

		private static Binding TwoWay(string path)
		{
			return new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
		}

		#endregion

		#region Actions

		private void OnStartStop(object sender, RoutedEventArgs e)
		{
			if (CopyTraderEngine.Instance.IsRunning)
			{
				CopyTraderEngine.Instance.Stop();
				return;
			}

			grid.CommitEdit(DataGridEditingUnit.Row, true);

			int resync;
			if (!int.TryParse(resyncBox.Text, out resync) || resync < 0)
			{
				ShowError("Resync interval must be a whole number of seconds (0 to turn it off).");
				return;
			}
			settings.ResyncIntervalSeconds = resync;

			if (!ConfirmExistingLeaderPosition())
				return;

			string error = CopyTraderEngine.Instance.Start(settings);
			if (error != null)
			{
				ShowError(error);
				return;
			}

			try { settings.Save(); }
			catch (Exception ex) { AppendLog("Could not save settings: " + ex.Message); }
		}

		/// <summary>Warns that followers will be brought in line with any position the leader already holds.</summary>
		private bool ConfirmExistingLeaderPosition()
		{
			Account leader;
			lock (Account.All)
				leader = Account.All.FirstOrDefault(a => a.Name == settings.LeaderAccount);
			if (leader == null)
				return true; // Start() reports the error.

			List<string> open = leader.Positions.ToArray()
				.Where(p => p.MarketPosition != MarketPosition.Flat)
				.Select(p => p.MarketPosition + " " + p.Quantity + " " + p.Instrument.FullName)
				.ToList();
			if (open.Count == 0)
				return true;

			return MessageBox.Show(
				"The leader already holds:\n\n  " + string.Join("\n  ", open) +
				"\n\nFollowers will immediately be sent market orders to match. Continue?",
				"Copy Trader", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
		}

		private void OnFlattenFollowers(object sender, RoutedEventArgs e)
		{
			if (MessageBox.Show(
					"Stop copying and flatten ALL positions (and cancel working orders) on every enabled follower account?",
					"Copy Trader", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
				return;

			CopyTraderEngine.Instance.FlattenFollowers(settings.Followers.ToList());
		}

		private void RefreshAccounts()
		{
			List<string> names;
			lock (Account.All)
				names = Account.All.Select(a => a.Name).OrderBy(n => n).ToList();

			// Keep configured names selectable even if their connection is down right now.
			foreach (string configured in new[] { settings.LeaderAccount }.Concat(settings.Followers.Select(f => f.AccountName)))
				if (!string.IsNullOrEmpty(configured) && !names.Contains(configured))
					names.Add(configured);

			foreach (string name in names.Where(n => !accountNames.Contains(n)).ToList())
				accountNames.Add(name);
		}

		private void ShowError(string message)
		{
			AppendLog("Error: " + message);
			MessageBox.Show(message, "Copy Trader", MessageBoxButton.OK, MessageBoxImage.Error);
		}

		#endregion

		#region Engine / window events

		private void OnEngineLog(string line)
		{
			// Engine raises this while holding its lock: never block here.
			Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
		}

		private void OnRunningChanged()
		{
			Dispatcher.BeginInvoke(new Action(UpdateRunningState));
		}

		private void OnAccountStatusUpdate(object sender, AccountStatusEventArgs e)
		{
			Dispatcher.BeginInvoke(new Action(RefreshAccounts));
		}

		private void AppendLog(string line)
		{
			logLines.Add(line);
			while (logLines.Count > MaxLogLines)
				logLines.RemoveAt(0);
			logList.ScrollIntoView(line);
		}

		private void UpdateRunningState()
		{
			bool running = CopyTraderEngine.Instance.IsRunning;

			startStopButton.Content = running ? "Stop" : "Start";
			statusText.Text         = running ? "RUNNING" : "Stopped";
			statusText.Foreground   = running ? Brushes.LimeGreen : Brushes.Gray;

			// Settings are snapshotted at start; lock editing so the screen matches what is live.
			leaderBox.IsEnabled    = !running;
			resyncBox.IsEnabled    = !running;
			apexBox.IsEnabled      = !running;
			grid.IsReadOnly        = running;
			addButton.IsEnabled    = !running;
			removeButton.IsEnabled = !running;
		}

		private void OnWindowClosing(object sender, CancelEventArgs e)
		{
			if (!CopyTraderEngine.Instance.IsRunning)
				return;

			MessageBoxResult answer = MessageBox.Show(
				"The copier is running.\n\nYes = stop copying and close\nNo = keep copying in the background and close\nCancel = keep this window open",
				"Copy Trader", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

			if (answer == MessageBoxResult.Cancel)
				e.Cancel = true;
			else if (answer == MessageBoxResult.Yes)
				CopyTraderEngine.Instance.Stop();
		}

		private void OnWindowClosed(object sender, EventArgs e)
		{
			CopyTraderEngine.Instance.Log            -= OnEngineLog;
			CopyTraderEngine.Instance.RunningChanged -= OnRunningChanged;
			Account.AccountStatusUpdate              -= OnAccountStatusUpdate;

			try { settings.Save(); }
			catch { }
		}

		#endregion
	}
}
