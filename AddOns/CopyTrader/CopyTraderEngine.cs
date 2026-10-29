#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NinjaTrader.Cbi;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
	/// <summary>
	/// Copies a leader account to follower accounts by position reconciliation:
	/// whenever the leader's position in an instrument changes, each follower is
	/// driven to (leader position * ratio) with market orders. Because it targets a
	/// position rather than replaying fills, it self-corrects after missed fills,
	/// rejects, partial fills and reconnects.
	///
	/// Works with any NinjaTrader 8 connection (Rithmic, CQG, Tradovate, Sim), since
	/// it only uses the NinjaTrader Account API.
	/// </summary>
	public sealed class CopyTraderEngine
	{
		public static readonly CopyTraderEngine Instance = new CopyTraderEngine();

		private const string OrderName           = "CopyTrader";
		private const int    MaxConsecutiveRejects = 3;
		private static readonly TimeSpan FillSettleTimeout = TimeSpan.FromSeconds(3);
		private static readonly TimeSpan StuckOrderWarning = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan LogThrottle       = TimeSpan.FromSeconds(30);

		private readonly object sync = new object();

		private Account               leader;
		private List<FollowerState>   followers = new List<FollowerState>();
		private CopyTraderSettings    settings;
		private Timer                 timer;
		private DateTime              lastResync;

		// Leader's last known signed position and instrument, keyed by Instrument.FullName.
		private readonly Dictionary<string, int>        leaderPositions   = new Dictionary<string, int>();
		private readonly Dictionary<string, Instrument> leaderInstruments = new Dictionary<string, Instrument>();
		private readonly Dictionary<string, DateTime>   lastLogged        = new Dictionary<string, DateTime>();

		private CopyTraderEngine() { }

		public event Action<string> Log;
		public event Action         RunningChanged;

		public bool IsRunning { get; private set; }

		#region Start / stop

		/// <summary>Starts copying. Returns null on success or an error message.</summary>
		public string Start(CopyTraderSettings config)
		{
			lock (sync)
			{
				if (IsRunning)
					return "Copier is already running.";

				Account leaderAccount = FindAccount(config.LeaderAccount);
				if (leaderAccount == null)
					return "Leader account '" + config.LeaderAccount + "' was not found. Is its connection up?";

				var newFollowers = new List<FollowerState>();
				var seen = new HashSet<string>(StringComparer.Ordinal);
				foreach (CopyTraderFollower f in config.Followers.Where(f => f.Enabled))
				{
					if (string.IsNullOrWhiteSpace(f.AccountName))
						return "A follower row has no account selected.";
					if (f.AccountName == leaderAccount.Name)
						return "The leader account cannot also be a follower.";
					if (!seen.Add(f.AccountName))
						return "Follower '" + f.AccountName + "' is listed more than once.";
					if (f.Ratio <= 0)
						return "Ratio for '" + f.AccountName + "' must be greater than 0.";
					if (f.MaxQuantity <= 0)
						return "Max qty for '" + f.AccountName + "' must be greater than 0.";

					Account account = FindAccount(f.AccountName);
					if (account == null)
						return "Follower account '" + f.AccountName + "' was not found. Is its connection up?";

					Dictionary<string, string> map;
					try { map = f.ParseInstrumentMap(); }
					catch (FormatException ex) { return ex.Message; }

					newFollowers.Add(new FollowerState { Config = f.Clone(), Account = account, Map = map });
				}

				if (newFollowers.Count == 0)
					return "Add at least one enabled follower.";

				settings  = config.Clone();
				leader    = leaderAccount;
				followers = newFollowers;
				leaderPositions.Clear();
				leaderInstruments.Clear();
				lastLogged.Clear();

				// Seed with whatever the leader already holds.
				foreach (Position p in SnapshotPositions(leader))
				{
					leaderInstruments[p.Instrument.FullName] = p.Instrument;
					leaderPositions[p.Instrument.FullName]   = Signed(p.MarketPosition, p.Quantity);
				}

				leader.PositionUpdate += OnLeaderPositionUpdate;
				foreach (FollowerState f in followers)
				{
					f.Account.OrderUpdate    += OnFollowerOrderUpdate;
					f.Account.PositionUpdate += OnFollowerPositionUpdate;
				}

				IsRunning  = true;
				lastResync = DateTime.UtcNow;
				timer      = new Timer(OnTimer, null, 500, 500);

				Write(string.Format(CultureInfo.InvariantCulture, "Started. Leader {0} -> {1}",
					leader.Name, string.Join(", ", followers.Select(f => string.Format(CultureInfo.InvariantCulture,
						"{0} (x{1}{2})", f.Account.Name, f.Config.Ratio, f.Config.Reverse ? ", reversed" : "")))));

				ReconcileAllLocked();
			}

			RaiseRunningChanged();
			return null;
		}

		public void Stop()
		{
			lock (sync)
			{
				if (!IsRunning)
					return;

				IsRunning = false;

				if (timer != null)
				{
					timer.Dispose();
					timer = null;
				}

				leader.PositionUpdate -= OnLeaderPositionUpdate;
				foreach (FollowerState f in followers)
				{
					f.Account.OrderUpdate    -= OnFollowerOrderUpdate;
					f.Account.PositionUpdate -= OnFollowerPositionUpdate;
				}

				int inFlight = followers.Sum(f => f.Pending.Count);
				if (inFlight > 0)
					Write(inFlight + " copy order(s) were still in flight when stopped; check follower positions.");

				followers = new List<FollowerState>();
				leader    = null;
				Write("Stopped.");
			}

			RaiseRunningChanged();
		}

		/// <summary>Stops the copier and flattens every listed follower account (all instruments).</summary>
		public void FlattenFollowers(IEnumerable<CopyTraderFollower> configured)
		{
			Stop();

			foreach (CopyTraderFollower f in configured.Where(f => f.Enabled))
			{
				Account account = FindAccount(f.AccountName);
				if (account == null)
				{
					Write("Flatten: account '" + f.AccountName + "' not found.");
					continue;
				}

				List<Instrument> instruments = SnapshotPositions(account).Select(p => p.Instrument).ToList();

				if (instruments.Count == 0)
				{
					Write("Flatten: " + account.Name + " is already flat.");
					continue;
				}

				try
				{
					account.Flatten(instruments);
					Write("Flatten: sent for " + account.Name + " (" + string.Join(", ", instruments.Select(i => i.FullName)) + ").");
				}
				catch (Exception ex)
				{
					Write("Flatten failed for " + account.Name + ": " + ex.Message);
				}
			}
		}

		/// <summary>Immediately re-checks every copied instrument on every follower.</summary>
		public void SyncNow()
		{
			lock (sync)
			{
				if (!IsRunning)
					return;

				foreach (FollowerState f in followers.Where(f => f.Halted))
				{
					f.Halted             = false;
					f.ConsecutiveRejects = 0;
					Write(f.Account.Name + ": un-halted by manual sync.");
				}
				ReconcileAllLocked();
			}
		}

		#endregion

		#region Event handlers

		private void OnLeaderPositionUpdate(object sender, PositionEventArgs e)
		{
			lock (sync)
			{
				if (!IsRunning)
					return;

				string key = e.Position.Instrument.FullName;
				int    pos = Signed(e.MarketPosition, e.Quantity);

				int previous;
				leaderPositions.TryGetValue(key, out previous);
				leaderPositions[key]   = pos;
				leaderInstruments[key] = e.Position.Instrument;

				if (previous != pos)
					Write(string.Format(CultureInfo.InvariantCulture, "Leader {0} {1}: {2} -> {3}", leader.Name, key, previous, pos));

				foreach (FollowerState f in followers)
					ReconcileLocked(f, key);
			}
		}

		private void OnFollowerOrderUpdate(object sender, OrderEventArgs e)
		{
			lock (sync)
			{
				if (!IsRunning)
					return;

				FollowerState f = followers.FirstOrDefault(x => x.Account == sender as Account);
				if (f == null)
					return;

				KeyValuePair<string, PendingOrder> entry = f.Pending.FirstOrDefault(kv => kv.Value.Order == e.Order);
				if (entry.Value == null)
					return;

				PendingOrder pending = entry.Value;
				switch (e.OrderState)
				{
					case OrderState.Filled:
						f.ConsecutiveRejects = 0;
						MarkDoneLocked(f, entry.Key, pending);
						break;

					case OrderState.Cancelled:
						if (e.Filled > 0)
							MarkDoneLocked(f, entry.Key, pending);
						else
						{
							f.Pending.Remove(entry.Key);
							Write(f.Account.Name + ": copy order for " + entry.Key + " was cancelled.");
							ReconcileLocked(f, pending.LeaderKey);
						}
						break;

					case OrderState.Rejected:
						f.Pending.Remove(entry.Key);
						f.ConsecutiveRejects++;
						Write(string.Format(CultureInfo.InvariantCulture, "{0}: order REJECTED for {1} ({2} {3}). {4}",
							f.Account.Name, entry.Key, e.Error, e.Comment, RejectSuffix(f)));
						if (f.ConsecutiveRejects >= MaxConsecutiveRejects)
						{
							f.Halted = true;
							Write(f.Account.Name + ": HALTED after " + MaxConsecutiveRejects + " consecutive rejects. Fix the cause, then press Sync now.");
						}
						// No immediate retry; the periodic resync or next leader change retries.
						break;
				}
			}
		}

		private void OnFollowerPositionUpdate(object sender, PositionEventArgs e)
		{
			lock (sync)
			{
				if (!IsRunning)
					return;

				FollowerState f = followers.FirstOrDefault(x => x.Account == sender as Account);
				if (f == null)
					return;

				string key = e.Position.Instrument.FullName;
				PendingOrder pending;
				if (!f.Pending.TryGetValue(key, out pending))
					return;

				if (Signed(e.MarketPosition, e.Quantity) != pending.ExpectedPosition)
					return;

				pending.PositionReached = true;
				if (pending.Done)
				{
					f.Pending.Remove(key);
					ReconcileLocked(f, pending.LeaderKey);
				}
			}
		}

		private void OnTimer(object state)
		{
			try
			{
				lock (sync)
				{
					if (!IsRunning)
						return;

					DateTime now = DateTime.UtcNow;

					foreach (FollowerState f in followers)
					{
						foreach (KeyValuePair<string, PendingOrder> kv in f.Pending.ToList())
						{
							PendingOrder p = kv.Value;
							if (p.Done && now - p.DoneTime > FillSettleTimeout)
							{
								// Filled, but no matching position update arrived; trust the account's current position.
								f.Pending.Remove(kv.Key);
								ReconcileLocked(f, p.LeaderKey);
							}
							else if (!p.Done && !p.WarnedStuck && now - p.Submitted > StuckOrderWarning)
							{
								p.WarnedStuck = true;
								Write(string.Format(CultureInfo.InvariantCulture, "{0}: copy order for {1} still working after {2}s (state {3}).",
									f.Account.Name, kv.Key, (int)StuckOrderWarning.TotalSeconds, p.Order.OrderState));
							}
						}
					}

					if (settings.ResyncIntervalSeconds > 0 && now - lastResync >= TimeSpan.FromSeconds(settings.ResyncIntervalSeconds))
					{
						lastResync = now;
						ReconcileAllLocked();
					}
				}
			}
			catch (Exception ex)
			{
				// Never let an exception escape a timer thread.
				Write("Timer error: " + ex.Message);
			}
		}

		#endregion

		#region Reconciliation

		private void ReconcileAllLocked()
		{
			foreach (string key in leaderPositions.Keys.ToList())
				foreach (FollowerState f in followers)
					ReconcileLocked(f, key);
		}

		/// <summary>Drives one follower's position in the instrument mapped from leaderKey toward its target.</summary>
		private void ReconcileLocked(FollowerState f, string leaderKey)
		{
			if (!IsRunning || f.Halted)
				return;

			Instrument leaderInstrument;
			if (!leaderInstruments.TryGetValue(leaderKey, out leaderInstrument))
				return;

			Instrument instrument = MapInstrument(f, leaderInstrument);
			if (instrument == null)
				return;

			string key = instrument.FullName;
			if (f.Pending.ContainsKey(key))
				return; // Re-run when the in-flight order settles.

			if (!IsConnected(f.Account))
			{
				WriteThrottled(f.Account.Name + "|conn", f.Account.Name + ": not connected; skipping until it reconnects.");
				return;
			}

			int target  = TargetPosition(f, leaderPositions[leaderKey]);
			int current = GetPosition(f.Account, instrument);
			if (target == current)
				return;

			// Never cross through zero in one order: close first, open on the next pass.
			OrderAction action;
			int quantity;
			if (current > 0 && target < current)
			{
				action   = OrderAction.Sell;
				quantity = Math.Min(current - target, current);
			}
			else if (current < 0 && target > current)
			{
				action   = OrderAction.BuyToCover;
				quantity = Math.Min(target - current, -current);
			}
			else if (target > current)
			{
				action   = OrderAction.Buy;
				quantity = target - current;
			}
			else
			{
				action   = OrderAction.SellShort;
				quantity = current - target;
			}

			int expected = current + (action == OrderAction.Buy || action == OrderAction.BuyToCover ? quantity : -quantity);

			Order order;
			try
			{
				order = f.Account.CreateOrder(instrument, action, OrderType.Market, OrderEntry.Automated, TimeInForce.Day,
					quantity, 0, 0, string.Empty, OrderName, NinjaTrader.Core.Globals.MaxDate, null);
			}
			catch (Exception ex)
			{
				Write(f.Account.Name + ": could not create order for " + key + ": " + ex.Message);
				return;
			}

			// Register before submitting: order events can fire during Submit.
			f.Pending[key] = new PendingOrder
			{
				Order            = order,
				LeaderKey        = leaderKey,
				ExpectedPosition = expected,
				Submitted        = DateTime.UtcNow
			};

			Write(string.Format(CultureInfo.InvariantCulture, "{0}: {1} {2} {3} (position {4} -> target {5})",
				f.Account.Name, action, quantity, key, current, target));

			try
			{
				f.Account.Submit(new[] { order });
			}
			catch (Exception ex)
			{
				f.Pending.Remove(key);
				Write(f.Account.Name + ": submit failed for " + key + ": " + ex.Message);
			}
		}

		private void MarkDoneLocked(FollowerState f, string key, PendingOrder pending)
		{
			pending.Done     = true;
			pending.DoneTime = DateTime.UtcNow;

			if (pending.PositionReached || GetPosition(f.Account, pending.Order.Instrument) == pending.ExpectedPosition)
			{
				f.Pending.Remove(key);
				ReconcileLocked(f, pending.LeaderKey);
			}
		}

		private static int TargetPosition(FollowerState f, int leaderPosition)
		{
			// Truncate toward zero so a fractional ratio never rounds a follower up into more risk.
			int target = (int)Math.Truncate(leaderPosition * f.Config.Ratio + (leaderPosition >= 0 ? 1e-9 : -1e-9));
			if (f.Config.Reverse)
				target = -target;
			return Math.Max(-f.Config.MaxQuantity, Math.Min(f.Config.MaxQuantity, target));
		}

		private Instrument MapInstrument(FollowerState f, Instrument leaderInstrument)
		{
			Instrument cached;
			if (f.InstrumentCache.TryGetValue(leaderInstrument.FullName, out cached))
				return cached;

			Instrument result = leaderInstrument;
			string targetMaster;
			if (f.Map.TryGetValue(leaderInstrument.MasterInstrument.Name, out targetMaster))
			{
				string name = leaderInstrument.MasterInstrument.InstrumentType == InstrumentType.Future
					? targetMaster + " " + leaderInstrument.Expiry.ToString("MM-yy", CultureInfo.InvariantCulture)
					: targetMaster;

				result = Instrument.GetInstrument(name);
				if (result == null)
				{
					WriteThrottled(f.Account.Name + "|map|" + name,
						f.Account.Name + ": mapped instrument '" + name + "' not found. Check the instrument map and Instruments window.");
					return null;
				}
			}

			f.InstrumentCache[leaderInstrument.FullName] = result;
			return result;
		}

		#endregion

		#region Helpers

		private static Account FindAccount(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;
			lock (Account.All)
				return Account.All.FirstOrDefault(a => a.Name == name);
		}

		private static bool IsConnected(Account account)
		{
			// Sim accounts may not carry a connection object; let NinjaTrader decide for those.
			return account.Connection == null || account.Connection.Status == ConnectionStatus.Connected;
		}

		private static int GetPosition(Account account, Instrument instrument)
		{
			Position p = SnapshotPositions(account).FirstOrDefault(x => x.Instrument.FullName == instrument.FullName);
			return p == null ? 0 : Signed(p.MarketPosition, p.Quantity);
		}

		// Copies the collection without taking its lock, so we never hold our lock while
		// waiting on one NinjaTrader may hold while raising events (deadlock risk).
		private static Position[] SnapshotPositions(Account account)
		{
			for (int attempt = 0; ; attempt++)
			{
				try { return account.Positions.ToArray(); }
				catch (InvalidOperationException)
				{
					if (attempt >= 4)
						throw;
				}
			}
		}

		private static int Signed(MarketPosition side, int quantity)
		{
			return side == MarketPosition.Long ? quantity : side == MarketPosition.Short ? -quantity : 0;
		}

		private static string RejectSuffix(FollowerState f)
		{
			return string.Format(CultureInfo.InvariantCulture, "[{0}/{1} rejects]", f.ConsecutiveRejects, MaxConsecutiveRejects);
		}

		private void WriteThrottled(string key, string message)
		{
			DateTime last;
			DateTime now = DateTime.UtcNow;
			if (lastLogged.TryGetValue(key, out last) && now - last < LogThrottle)
				return;
			lastLogged[key] = now;
			Write(message);
		}

		private void Write(string message)
		{
			string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message;
			try { NinjaTrader.Code.Output.Process("CopyTrader " + line, PrintTo.OutputTab1); }
			catch { }

			Action<string> handler = Log;
			if (handler != null)
				handler(line);
		}

		private void RaiseRunningChanged()
		{
			Action handler = RunningChanged;
			if (handler != null)
				handler();
		}

		#endregion

		private sealed class FollowerState
		{
			public FollowerState()
			{
				Pending         = new Dictionary<string, PendingOrder>();
				InstrumentCache = new Dictionary<string, Instrument>();
			}

			public CopyTraderFollower             Config;
			public Account                        Account;
			public Dictionary<string, string>     Map;
			public Dictionary<string, PendingOrder> Pending;          // keyed by follower Instrument.FullName
			public Dictionary<string, Instrument> InstrumentCache;  // leader FullName -> follower instrument
			public int                            ConsecutiveRejects;
			public bool                           Halted;
		}

		private sealed class PendingOrder
		{
			public Order    Order;
			public string   LeaderKey;
			public int      ExpectedPosition;
			public DateTime Submitted;
			public bool     Done;
			public DateTime DoneTime;
			public bool     PositionReached;
			public bool     WarnedStuck;
		}
	}
}
