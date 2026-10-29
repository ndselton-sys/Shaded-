#region Using declarations
using System;
using System.Windows;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
	/// <summary>Adds "Copy Trader" to the Control Center's New menu.</summary>
	public class CopyTraderAddOn : AddOnBase
	{
		private NTMenuItem menuItem;
		private NTMenuItem newMenu;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name        = "CopyTrader";
				Description = "Copies trades from a leader account to follower accounts (Rithmic or any NinjaTrader connection).";
			}
			else if (State == State.Terminated && menuItem != null)
			{
				// Only the live instance (the one that added the menu) stops the copier, so
				// a recompile or shutdown never leaves an orphaned engine running old code.
				// NinjaTrader also terminates short-lived instances, which must not stop it.
				CopyTraderEngine.Instance.Stop();
			}
		}

		protected override void OnWindowCreated(Window window)
		{
			var controlCenter = window as ControlCenter;
			if (controlCenter == null)
				return;

			newMenu = controlCenter.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
			if (newMenu == null)
				return;

			menuItem = new NTMenuItem
			{
				Header = "Copy Trader",
				Style  = Application.Current.TryFindResource("MainMenuItem") as Style
			};
			menuItem.Click += OnMenuItemClick;
			newMenu.Items.Add(menuItem);
		}

		protected override void OnWindowDestroyed(Window window)
		{
			if (menuItem == null || !(window is ControlCenter))
				return;

			if (newMenu != null && newMenu.Items.Contains(menuItem))
				newMenu.Items.Remove(menuItem);

			menuItem.Click -= OnMenuItemClick;
			menuItem = null;
		}

		private void OnMenuItemClick(object sender, RoutedEventArgs e)
		{
			Core.Globals.RandomDispatcher.BeginInvoke(new Action(() => new CopyTraderWindow().Show()));
		}
	}
}
