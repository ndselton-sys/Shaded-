#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
	/// <summary>One follower account and how it copies the leader.</summary>
	public class CopyTraderFollower
	{
		public CopyTraderFollower()
		{
			Enabled       = true;
			AccountName   = string.Empty;
			Ratio         = 1.0;
			MaxQuantity   = 10;
			InstrumentMap = string.Empty;
		}

		public bool   Enabled       { get; set; }
		public string AccountName   { get; set; }

		/// <summary>Follower size = leader size * Ratio, truncated toward zero.</summary>
		public double Ratio         { get; set; }

		/// <summary>Trade the opposite direction of the leader.</summary>
		public bool   Reverse       { get; set; }

		/// <summary>Hard cap on the follower's absolute position per instrument.</summary>
		public int    MaxQuantity   { get; set; }

		/// <summary>Master-instrument substitutions, e.g. "NQ=MNQ; ES=MES".</summary>
		public string InstrumentMap { get; set; }

		public CopyTraderFollower Clone()
		{
			return (CopyTraderFollower)MemberwiseClone();
		}

		/// <summary>Parses InstrumentMap into leader master name -> follower master name.</summary>
		public Dictionary<string, string> ParseInstrumentMap()
		{
			var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrWhiteSpace(InstrumentMap))
				return map;

			foreach (string pair in InstrumentMap.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = pair.Split('=');
				if (parts.Length != 2)
					throw new FormatException(string.Format(CultureInfo.InvariantCulture,
						"Bad instrument mapping '{0}' for {1}. Use the form NQ=MNQ.", pair.Trim(), AccountName));

				string from = parts[0].Trim();
				string to   = parts[1].Trim();
				if (from.Length == 0 || to.Length == 0)
					throw new FormatException(string.Format(CultureInfo.InvariantCulture,
						"Bad instrument mapping '{0}' for {1}. Use the form NQ=MNQ.", pair.Trim(), AccountName));

				map[from] = to;
			}
			return map;
		}
	}

	/// <summary>Copier configuration, persisted as XML in the NinjaTrader user folder.</summary>
	public class CopyTraderSettings
	{
		public CopyTraderSettings()
		{
			LeaderAccount         = string.Empty;
			Followers             = new ObservableCollection<CopyTraderFollower>();
			ResyncIntervalSeconds = 5;
			ApexRules             = true;
		}

		public string LeaderAccount { get; set; }

		public ObservableCollection<CopyTraderFollower> Followers { get; set; }

		/// <summary>How often every copied instrument is re-checked and corrected. 0 disables it.</summary>
		public int ResyncIntervalSeconds { get; set; }

		/// <summary>
		/// Enforce prop-firm (Apex) restrictions: no reversed followers, since opposite
		/// positions across your accounts count as prohibited hedging.
		/// </summary>
		public bool ApexRules { get; set; }

		[XmlIgnore]
		public static string FilePath
		{
			get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "CopyTrader.xml"); }
		}

		public static CopyTraderSettings Load()
		{
			try
			{
				if (File.Exists(FilePath))
				{
					var serializer = new XmlSerializer(typeof(CopyTraderSettings));
					using (var stream = File.OpenRead(FilePath))
					{
						var settings = (CopyTraderSettings)serializer.Deserialize(stream);
						if (settings.Followers == null)
							settings.Followers = new ObservableCollection<CopyTraderFollower>();
						return settings;
					}
				}
			}
			catch (Exception ex)
			{
				NinjaTrader.Code.Output.Process("CopyTrader: could not load settings, using defaults. " + ex.Message, PrintTo.OutputTab1);
			}
			return new CopyTraderSettings();
		}

		public void Save()
		{
			var serializer = new XmlSerializer(typeof(CopyTraderSettings));
			using (var stream = File.Create(FilePath))
				serializer.Serialize(stream, this);
		}

		public CopyTraderSettings Clone()
		{
			return new CopyTraderSettings
			{
				LeaderAccount         = LeaderAccount,
				ResyncIntervalSeconds = ResyncIntervalSeconds,
				ApexRules             = ApexRules,
				Followers             = new ObservableCollection<CopyTraderFollower>(Followers.Select(f => f.Clone()))
			};
		}
	}
}
