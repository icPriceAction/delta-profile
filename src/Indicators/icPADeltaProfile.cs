// =========================================================================================
// Tool Name:    icPA - Delta Profile
// Class:        icPADeltaProfile
// Category:     Indicator
// Version:      2.0.1
// Platform:     NinjaTrader 8.0+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/delta-profile
// YouTube:      https://youtube.com/@icPriceAction
// Support:      https://buymeacoffee.com/icpriceaction
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  Session or anchored delta profile: diverging buy/sell delta bars at every
//               price, built from its own 1-tick series with bid/ask aggressor classification,
//               plus lines at each profile's largest positive and negative delta.
//               No Tick Replay required.
//
// Changelog:
//   v2.0.1 - Initial public release. Cleaned and optimized by icPriceAction.
//   v2.0.0 - Renamed from CustomVolumeDelta to CustomDeltaProfile; brushes frozen at
//                         Configure; realtime bid/ask snapshot discarded after every trade; Auto
//                         Right Margin guarded against the chart closing; chart label shows the
//                         indicator name only; accessors documented as data-thread calls.
//   v1.9.1 - Developing profile (Right of current bar) stays pinned to the right edge
//                         of the screen when scrolling left.
//   v1.9.0 - Added Completed Position (Start / Center / End) for completed profiles.
//   v1.8.1 - Developing profile default width changed from 200 to 280 px.
//   v1.8.0 - Removed POC / Value Area; developing profile right of the current bar;
//                         Auto Right Margin; completed profile width as % of session with a pixel
//                         cap; two-line summary label.
//   v1.7.0 - Delta-first refocus: diverging delta bars, Absolute / Ratio measure,
//                         delta extreme levels, plots and accessors, optional volume context.
//   v1.6.2 - Level lines, row text, summary label, plots, public accessors and
//                         example strategy (covers v1.6.0 - v1.6.2).
//   v1.5.0 - Rendering: one profile per period, auto row sizing, gradient colouring,
//                         width scaling, panel clipping and culling.
//   v1.4.0 - Data engine: 1-tick series, bid/ask classification with tick-rule
//                         fallback, session / anchored scope, Sessions To Keep, diagnostics.
//   v1.3.0 - Level 1 up-tick/down-tick profile built on the primary series.
// =========================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.icPA
{
    public class icPADeltaProfile : Indicator
    {
        private const string VersionString = "2.0.1";
        private const string LogPrefix     = "icPADP | ";
        private const int    TickSeries    = 1;
        private const int    RealtimeDiagIntervalMinutes = 5;

        private const float  DevelopingGapPx   = 10f;   // gap between current bar and developing profile
        private const int    MarginPaddingPx   = 10;    // extra room right of the developing profile

        // Property groups
        private const string GroupGeneral    = "1. General";
        private const string GroupVisuals    = "2. Visuals";
        private const string GroupThresholds = "3. Offsets & Thresholds";

        // Plot indices
        private const int PlotDelta     = 0;
        private const int PlotVolume    = 1;
        private const int PlotDevMaxPos = 2;
        private const int PlotDevMaxNeg = 3;

        #region Data Classes

        private class VolumeRow
        {
            public double BuyVol;
            public double SellVol;
            public double TotalVol { get { return BuyVol + SellVol; } }
            public double Delta    { get { return BuyVol - SellVol; } }
        }

        private class ProfileSegment
        {
            // Nominal period bounds, in the platform time zone
            public DateTime PeriodBegin;
            public DateTime PeriodEnd;
            public DateTime FirstTickTime;
            public DateTime LastTickTime;

            // Price levels keyed by integer tick index (price / TickSize)
            public readonly Dictionary<long, VolumeRow> Rows = new Dictionary<long, VolumeRow>();
            public long MinIdx;
            public long MaxIdx;

            public double TotalBuy;
            public double TotalSell;
            public double TotalVolume { get { return TotalBuy + TotalSell; } }
            public double Delta       { get { return TotalBuy - TotalSell; } }

            // Running (cumulative) delta extremes reached during the profile
            public double CumDeltaHigh;
            public double CumDeltaLow;

            // Delta extremes (tick indices), recomputed lazily when dirty
            public bool   LevelsDirty = true;
            public bool   HasMaxPos;
            public bool   HasMaxNeg;
            public long   MaxPosIdx;
            public long   MaxNegIdx;
            public double MaxPosDelta;
            public double MaxNegDelta;

            // Incremented on every change (data thread, inside dataLock)
            public long Version;

            // Diagnostics
            public long TickCount;
            public long HistTicks;
            public long HistTicksWithBidAsk;
            public long RtTicks;
            public long RtTicksWithBidAsk;
            public long RtTicksFromSnapshot;

            // Render cache - written and read by the render thread only.
            // Bar indices are cached once the segment has completed.
            public bool BarIdxCached;
            public int  CachedStartIdx;
            public int  CachedEndIdx;

            // Aggregated rows, rebuilt only when the grouping or the data changes.
            // The VolumeRow objects in this list are owned by the render thread.
            public List<KeyValuePair<long, VolumeRow>> AggRows;
            public int    AggGrouping;
            public long   AggVersion = -1;
            public double AggMaxVol;
            public double AggMaxAbsDelta;
        }

        /// <summary>Per-frame render view of one segment (pooled, render thread only).</summary>
        private class SegmentView
        {
            public ProfileSegment Seg;
            public DateTime First;
            public DateTime Last;
            public bool     IsDeveloping;
            public bool     ProfileVisible;
            public int      StartIdx;
            public int      EndIdx;
            public float    XLeft;        // session start (line start)
            public float    XRight;       // line end / profile box right
            public float    NextXRight = float.NaN;
            public float    AxisX;
            public float    HalfLen;      // maximum bar length on each side of the axis

            // Snapshot of segment values
            public long     MinIdx, MaxIdx, MaxPosIdx, MaxNegIdx;
            public bool     HasMaxPos, HasMaxNeg;
            public double   TotalVolume, Delta, CumDeltaHigh, CumDeltaLow;

            public List<KeyValuePair<long, VolumeRow>> Rows;
            public double   MaxVol;
            public double   MaxAbsDelta;

            public void Reset()
            {
                Seg            = null;
                Rows           = null;
                IsDeveloping   = false;
                ProfileVisible = false;
                NextXRight     = float.NaN;
                MaxVol         = 0;
                MaxAbsDelta    = 0;
            }
        }

        /// <summary>
        /// Maps a timestamp to the profile period (session or anchored window)
        /// it belongs to. Caches the current period so the expensive work
        /// (session lookups, time zone conversion) only runs at boundaries.
        /// </summary>
        private class PeriodTracker
        {
            private readonly icPADeltaProfile owner;
            private readonly SessionIterator  sessionIterator;
            private bool     hasPeriod;
            private DateTime nextBegin;

            public DateTime Begin;  // period key (platform time zone)
            public DateTime End;    // end of the active window

            public PeriodTracker(icPADeltaProfile owner, Bars bars)
            {
                this.owner = owner;
                if (owner.ProfileScope == DpProfileScope.Session)
                    sessionIterator = new SessionIterator(bars);
            }

            /// <summary>Returns false if t falls outside any active window (anchored gap).</summary>
            public bool TryGetPeriod(DateTime t, out DateTime key)
            {
                if (owner.ProfileScope == DpProfileScope.Session)
                {
                    if (!hasPeriod || t > End)
                    {
                        sessionIterator.GetNextSession(t, true);
                        Begin     = sessionIterator.ActualSessionBegin;
                        End       = sessionIterator.ActualSessionEnd;
                        hasPeriod = true;
                    }
                    key = Begin;
                    return true;
                }

                // Anchored
                if (!hasPeriod || t < Begin || t >= nextBegin)
                    ComputeAnchoredWindow(t);

                key = Begin;
                return t >= Begin && t < End;
            }

            private void ComputeAnchoredWindow(DateTime t)
            {
                TimeZoneInfo platformTz = owner.platformTz;
                TimeZoneInfo anchorTz   = owner.anchorTz;

                DateTime local = ConvertTime(t, platformTz, anchorTz);
                TimeSpan start = owner.AnchorStartTime.TimeOfDay;

                DateTime day    = local.TimeOfDay >= start ? local.Date : local.Date.AddDays(-1);
                DateTime startA = day + start;

                TimeSpan duration = TimeSpan.FromDays(1);
                if (owner.UseAnchorEndTime)
                {
                    duration = owner.AnchorEndTime.TimeOfDay - start;
                    if (duration <= TimeSpan.Zero)
                        duration += TimeSpan.FromDays(1);   // window crosses midnight
                }

                // Window bounds are wall-clock times in the anchor zone,
                // converted back to the platform zone (DST handled here).
                Begin     = ConvertTime(startA,            anchorTz, platformTz);
                End       = ConvertTime(startA + duration, anchorTz, platformTz);
                nextBegin = ConvertTime(startA.AddDays(1), anchorTz, platformTz);
                hasPeriod = true;
            }
        }

        #endregion

        #region Variables

        private readonly object dataLock = new object();

        private List<ProfileSegment> segments;
        private ProfileSegment       currentSegment;
        private PeriodTracker        tickTracker;
        private PeriodTracker        barTracker;
        private Dictionary<DateTime, double> diagBarVolume;

        private TimeZoneInfo platformTz;
        private TimeZoneInfo anchorTz;

        // Tick processing state
        private int    lastTickBar;
        private double lastTickBarVolume;
        private long   lastTradeIdx;
        private bool   hasLastTrade;
        private int    lastDirection;

        // Realtime bid/ask snapshot captured with each trade
        private double rtBid;
        private double rtAsk;
        private double rtLastPrice;
        private bool   rtSnapshotValid;

        private DateTime lastRealtimeDiagPrint;

        // Auto right margin (guarded by marginLock: set on the UI thread,
        // released from State.Terminated)
        private readonly object marginLock = new object();
        private ChartControl marginChart;
        private bool         marginChanged;
        private bool         marginReleased;
        private int          originalMargin;
        private int          appliedMargin;

        // Render-thread scratch lists, reused every frame to avoid allocations
        private readonly List<SegmentView> renderViews = new List<SegmentView>();
        private readonly List<SegmentView> viewPool    = new List<SegmentView>();

        // SharpDX rendering: one reusable brush, colours set per element
        private SharpDX.Direct2D1.SolidColorBrush workBrushDx;
        private SharpDX.DirectWrite.TextFormat    textFormat;
        private SharpDX.Color4 buyC4;
        private SharpDX.Color4 sellC4;
        private SharpDX.Color4 neutralC4;
        private SharpDX.Color4 volumeContextC4;
        private SharpDX.Color4 textC4;

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Delta profile per session or anchored window: diverging buy/sell delta at every price, with lines at the largest positive and negative delta. "
                                         + "Builds its own 1-tick series, so Tick Replay is not required; bid/ask classification needs historical tick data with bid/ask from your data provider.\r\n"
                                         + "Developed by Istvan Cebrian | https://icpriceaction.com\r"
					                     + "Support this free tool: https://buymeacoffee.com/icpriceaction";
                Name                     = "icPA - Delta Profile";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                IsAutoScale              = false;   // keep volume/delta plots out of the price scale
                PaintPriceMarkers        = false;
                BarsRequiredToPlot       = 0;
                IsSuspendedWhileInactive = false;

                // 1. General
                Classification   = DpClassification.BidAsk;
                ProfileScope     = DpProfileScope.Session;
                AnchorStartTime  = DateTime.Today.Add(new TimeSpan(9, 30, 0));
                UseAnchorEndTime = false;
                AnchorEndTime    = DateTime.Today.Add(new TimeSpan(16, 0, 0));
                AnchorTimeZone   = "Eastern Standard Time";
                SessionsToKeep   = 10;
                ShowDiagnostics  = false;

                // 2. Visuals (palette tuned for a light #E6E7E8 chart background)
                Brush buyBrush  = MakeBrush(0x3C, 0x8D, 0x5A);   // muted green
                Brush sellBrush = MakeBrush(0xC0, 0x50, 0x4D);   // muted red

                DeltaMeasure         = DpDeltaMeasure.Absolute;
                WidthScaling         = DpWidthScaling.PerSession;
                CompletedPosition    = DpCompletedPosition.End;
                DevelopingPosition   = DpDevelopingPosition.RightOfCurrentBar;
                AutoRightMargin      = true;
                ShowAxis             = true;
                ShowVolumeContext    = false;
                ShowDeltaExtremes    = true;
                PositiveDeltaStroke  = new Stroke(buyBrush,  DashStyleHelper.Solid, 2, 70);
                NegativeDeltaStroke  = new Stroke(sellBrush, DashStyleHelper.Solid, 2, 70);
                LevelExtension       = DpLevelExtension.None;
                RowText              = DpRowText.None;
                SummaryPosition      = DpSummaryPosition.Top;
                TextFont             = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 10);
                TextColor            = MakeBrush(0x33, 0x33, 0x33);
                ColorMode            = DpColorMode.Gradient;
                BuyColor             = buyBrush;
                SellColor            = sellBrush;
                NeutralColor         = MakeBrush(0x8C, 0x8C, 0x8C);
                VolumeColor          = MakeBrush(0x9A, 0xA3, 0xAD);
                ProfileOpacity       = 60;
                VolumeContextOpacity = 15;

                // 3. Offsets & Thresholds
                RowSizing           = DpRowSizing.Auto;
                MinRowHeight        = 4;
                TickGrouping        = 1;
                ProfileWidthPercent = 80;
                MaxProfileWidthPx   = 300;
                DevelopingWidthPx   = 280;
                GradientSaturation  = 20;

                // Plots (transparent by default; for strategies / Market Analyzer)
                AddPlot(Brushes.Transparent, "Period Delta");
                AddPlot(Brushes.Transparent, "Period Volume");
                AddPlot(Brushes.Transparent, "Dev +Delta Extreme");
                AddPlot(Brushes.Transparent, "Dev -Delta Extreme");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);

                // Brushes are read on the render thread; freeze them so a
                // brush created on another thread can't throw cross-thread.
                BuyColor     = FreezeBrush(BuyColor);
                SellColor    = FreezeBrush(SellColor);
                NeutralColor = FreezeBrush(NeutralColor);
                VolumeColor  = FreezeBrush(VolumeColor);
                TextColor    = FreezeBrush(TextColor);
            }
            else if (State == State.DataLoaded)
            {
                segments       = new List<ProfileSegment>();
                diagBarVolume  = new Dictionary<DateTime, double>();
                currentSegment = null;

                lastTickBar       = -1;
                lastTickBarVolume = 0;
                hasLastTrade      = false;
                lastDirection     = 0;
                rtSnapshotValid   = false;
                lastRealtimeDiagPrint = DateTime.MinValue;

                ResolveTimeZones();

                tickTracker = new PeriodTracker(this, BarsArray[TickSeries]);
                barTracker  = new PeriodTracker(this, BarsArray[0]);

                CheckPrerequisites();
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null
                    && AutoRightMargin
                    && DevelopingPosition == DpDevelopingPosition.RightOfCurrentBar)
                {
                    marginChart = ChartControl;
                    marginChart.Dispatcher.InvokeAsync(EnsureRightMargin);
                }
            }
            else if (State == State.Realtime)
            {
                WarnIfNoHistoricalBidAsk();

                if (ShowDiagnostics)
                    PrintHistoricalDiagnostics();
            }
            else if (State == State.Terminated)
            {
                RestoreRightMargin();
                DisposeRenderResources();
            }
        }

        /// <summary>Chart label: just the indicator name, without the parameter list.</summary>
        public override string DisplayName
        {
            get { return Name; }
        }

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush FreezeBrush(Brush brush)
        {
            if (brush == null || brush.IsFrozen)
                return brush;
            if (!brush.CanFreeze)
                return brush;
            Brush clone = brush.Clone();
            clone.Freeze();
            return clone;
        }

        #region Prerequisites

        /// <summary>DataLoaded: one-time warnings for settings or data that degrade the profile.</summary>
        private void CheckPrerequisites()
        {
            if (Calculate != Calculate.OnEachTick)
                Print(LogPrefix + "Calculate is set to " + Calculate + ". Use 'On each tick': otherwise each realtime trade is processed only when the next one arrives, and the plots update only on bar close.");

            if (BarsArray[TickSeries] == null || BarsArray[TickSeries].Count == 0)
                Print(LogPrefix + "No historical tick data was loaded for " + Instrument.FullName + ". Profiles will build from realtime data only. Check that your data provider supplies tick history for the days loaded on the chart.");
        }

        /// <summary>Realtime transition: warn once if BidAsk found no historical quotes.</summary>
        private void WarnIfNoHistoricalBidAsk()
        {
            if (Classification != DpClassification.BidAsk || segments == null)
                return;

            long ticks = 0, withBidAsk = 0;
            lock (dataLock)
            {
                foreach (ProfileSegment s in segments)
                {
                    ticks      += s.HistTicks;
                    withBidAsk += s.HistTicksWithBidAsk;
                }
            }

            if (ticks > 0 && withBidAsk == 0)
                Print(LogPrefix + "Classification is BidAsk, but no historical trades carried bid/ask quotes from your data provider. Historical profiles used the tick rule; realtime trades use live quotes.");
        }

        #endregion

        #region Right Margin

        private int RequiredMarginPx()
        {
            return (int)Math.Ceiling(DevelopingGapPx) + DevelopingWidthPx + MarginPaddingPx;
        }

        /// <summary>UI thread. Raises the right margin only if it is too small.</summary>
        private void EnsureRightMargin()
        {
            try
            {
                lock (marginLock)
                {
                    // The indicator may have been removed before this ran.
                    if (marginReleased || marginChart == null || marginChart.Properties == null)
                        return;

                    int current  = marginChart.Properties.BarMarginRight;
                    int required = RequiredMarginPx();
                    if (current >= required)
                        return;   // the existing margin already fits the profile

                    originalMargin = current;
                    appliedMargin  = required;
                    marginChanged  = true;
                    marginChart.Properties.BarMarginRight = required;
                }
                marginChart.InvalidateVisual();
            }
            catch (Exception)
            {
                // Chart closing or not ready; the margin is a convenience only.
            }
        }

        /// <summary>Restores the margin, unless the user changed it since.</summary>
        private void RestoreRightMargin()
        {
            ChartControl chart;
            int          original;
            int          applied;

            lock (marginLock)
            {
                marginReleased = true;   // stops a pending EnsureRightMargin from applying later
                if (!marginChanged || marginChart == null)
                    return;

                chart         = marginChart;
                original      = originalMargin;
                applied       = appliedMargin;
                marginChanged = false;
            }

            try
            {
                chart.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (chart.Properties != null && chart.Properties.BarMarginRight == applied)
                        {
                            chart.Properties.BarMarginRight = original;
                            chart.InvalidateVisual();
                        }
                    }
                    catch (Exception)
                    {
                        // Chart already closed (e.g. workspace shutting down)
                    }
                });
            }
            catch (Exception)
            {
                // Dispatcher unavailable (chart closed)
            }
        }

        #endregion

        #region Data Processing

        protected override void OnBarUpdate()
        {
            if (CurrentBars[BarsInProgress] < 0)
                return;

            if (BarsInProgress == TickSeries)
            {
                ProcessTick();
            }
            else if (BarsInProgress == 0)
            {
                if (CurrentBars[0] < BarsRequiredToPlot)
                    return;

                if (ShowDiagnostics && State == State.Historical)
                    AccumulateBarVolume();

                UpdatePlots();
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // Realtime only: capture the quote at the moment of each trade.
            if (State != State.Realtime || e.MarketDataType != MarketDataType.Last)
                return;

            rtBid           = e.Bid;
            rtAsk           = e.Ask;
            rtLastPrice     = e.Price;
            rtSnapshotValid = true;
        }

        private void ProcessTick()
        {
            int    bar      = CurrentBars[TickSeries];
            double barVol   = Volumes[TickSeries][0];
            double vol      = bar != lastTickBar ? barVol : barVol - lastTickBarVolume;

            lastTickBar       = bar;
            lastTickBarVolume = barVol;

            if (vol <= 0)
                return;

            double   price = Closes[TickSeries][0];
            DateTime t     = Times[TickSeries][0];
            long     pIdx  = ToTickIndex(price);

            // Tick rule state is maintained continuously, including gaps
            // between anchored windows, so classification never restarts cold.
            int tickRuleDir = lastDirection;
            if (hasLastTrade)
            {
                if (pIdx > lastTradeIdx)      tickRuleDir = 1;
                else if (pIdx < lastTradeIdx) tickRuleDir = -1;
            }
            lastTradeIdx  = pIdx;
            hasLastTrade  = true;
            lastDirection = tickRuleDir;

            bool isRealtime   = State == State.Realtime;
            bool hasBidAsk    = false;
            bool fromSnapshot = false;
            int  direction    = tickRuleDir;

            if (Classification == DpClassification.BidAsk)
            {
                double bid, ask;
                if (TryGetBidAsk(bar, pIdx, isRealtime, out bid, out ask, out fromSnapshot))
                {
                    hasBidAsk = true;
                    long bIdx = ToTickIndex(bid);
                    long aIdx = ToTickIndex(ask);

                    if (pIdx >= aIdx)      direction = 1;    // lifted the offer
                    else if (pIdx <= bIdx) direction = -1;   // hit the bid
                    // else: traded inside the spread, keep tick-rule direction
                }
            }

            // A realtime snapshot belongs to one trade. Discard it even if it
            // was not used, so it can never be applied to a later trade.
            rtSnapshotValid = false;

            DateTime key;
            if (!tickTracker.TryGetPeriod(t, out key))
                return;   // outside the anchored window

            ProfileSegment completed = null;

            lock (dataLock)
            {
                if (currentSegment == null || currentSegment.PeriodBegin != key)
                    completed = StartNewSegment(key, tickTracker.End);

                ProfileSegment seg = currentSegment;

                VolumeRow row;
                if (!seg.Rows.TryGetValue(pIdx, out row))
                {
                    row = new VolumeRow();
                    seg.Rows[pIdx] = row;

                    if (seg.Rows.Count == 1)
                    {
                        seg.MinIdx = pIdx;
                        seg.MaxIdx = pIdx;
                    }
                    else
                    {
                        if (pIdx < seg.MinIdx) seg.MinIdx = pIdx;
                        if (pIdx > seg.MaxIdx) seg.MaxIdx = pIdx;
                    }
                }

                if (direction > 0)
                {
                    row.BuyVol   += vol;
                    seg.TotalBuy += vol;
                }
                else if (direction < 0)
                {
                    row.SellVol   += vol;
                    seg.TotalSell += vol;
                }
                else
                {
                    // Direction unknown (very first ticks only): split evenly
                    double half = vol / 2.0;
                    row.BuyVol    += half;
                    row.SellVol   += half;
                    seg.TotalBuy  += half;
                    seg.TotalSell += half;
                }

                // Cumulative delta extremes during the profile
                double cum = seg.Delta;
                if (seg.TickCount == 0)
                {
                    seg.CumDeltaHigh  = cum;
                    seg.CumDeltaLow   = cum;
                    seg.FirstTickTime = t;
                }
                else
                {
                    if (cum > seg.CumDeltaHigh) seg.CumDeltaHigh = cum;
                    if (cum < seg.CumDeltaLow)  seg.CumDeltaLow  = cum;
                }

                seg.LevelsDirty  = true;
                seg.Version++;
                seg.LastTickTime = t;
                seg.TickCount++;

                if (isRealtime)
                {
                    seg.RtTicks++;
                    if (hasBidAsk)    seg.RtTicksWithBidAsk++;
                    if (fromSnapshot) seg.RtTicksFromSnapshot++;
                }
                else
                {
                    seg.HistTicks++;
                    if (hasBidAsk) seg.HistTicksWithBidAsk++;
                }
            }

            if (ShowDiagnostics && isRealtime)
            {
                if (completed != null)
                {
                    Print(LogPrefix + "Period completed:");
                    PrintSegmentReport(completed, false);
                }

                if ((DateTime.UtcNow - lastRealtimeDiagPrint).TotalMinutes >= RealtimeDiagIntervalMinutes)
                {
                    lastRealtimeDiagPrint = DateTime.UtcNow;
                    PrintSegmentReport(currentSegment, true);
                }
            }
        }

        /// <summary>Must be called inside dataLock. Returns the segment that just completed.</summary>
        private ProfileSegment StartNewSegment(DateTime key, DateTime periodEnd)
        {
            ProfileSegment completed = currentSegment;

            currentSegment = new ProfileSegment { PeriodBegin = key, PeriodEnd = periodEnd };
            segments.Add(currentSegment);

            while (segments.Count > SessionsToKeep)
            {
                diagBarVolume.Remove(segments[0].PeriodBegin);
                segments.RemoveAt(0);
            }

            return completed;
        }

        private bool TryGetBidAsk(int bar, long pIdx, bool isRealtime, out double bid, out double ask, out bool fromSnapshot)
        {
            fromSnapshot = false;

            if (isRealtime && rtSnapshotValid && ToTickIndex(rtLastPrice) == pIdx)
            {
                bid = rtBid;
                ask = rtAsk;
                fromSnapshot = true;
                rtSnapshotValid = false;   // each snapshot is used once
            }
            else
            {
                bid = BarsArray[TickSeries].GetBid(bar);
                ask = BarsArray[TickSeries].GetAsk(bar);
            }

            if (bid <= 0 || ask <= 0 || ask < bid)
                return false;

            // Providers without stored quotes return the trade price for both
            long bIdx = ToTickIndex(bid);
            long aIdx = ToTickIndex(ask);
            if (bIdx == pIdx && aIdx == pIdx)
                return false;

            return true;
        }

        /// <summary>Must be called inside dataLock. Finds the delta extremes at full tick resolution.</summary>
        private void ComputeLevels(ProfileSegment seg)
        {
            if (!seg.LevelsDirty || seg.Rows.Count == 0)
                return;

            bool   hasPos = false, hasNeg = false;
            double bestPos = 0, bestNeg = 0;
            long   posIdx = 0, negIdx = 0;

            foreach (KeyValuePair<long, VolumeRow> kvp in seg.Rows)
            {
                double d = kvp.Value.Delta;
                if (d > bestPos) { bestPos = d; posIdx = kvp.Key; hasPos = true; }
                if (d < bestNeg) { bestNeg = d; negIdx = kvp.Key; hasNeg = true; }
            }

            seg.HasMaxPos   = hasPos;
            seg.HasMaxNeg   = hasNeg;
            seg.MaxPosIdx   = posIdx;
            seg.MaxNegIdx   = negIdx;
            seg.MaxPosDelta = bestPos;
            seg.MaxNegDelta = bestNeg;
            seg.LevelsDirty = false;
        }

        private void UpdatePlots()
        {
            // Bar timestamps mark the bar's END; step back one tick.
            DateTime key;
            if (!barTracker.TryGetPeriod(Times[0][0].AddTicks(-1), out key))
                return;   // outside the anchored window: leave a gap

            lock (dataLock)
            {
                ProfileSegment seg = currentSegment;
                if (seg == null || seg.PeriodBegin != key || seg.TickCount == 0)
                    return;

                ComputeLevels(seg);

                Values[PlotDelta][0]  = seg.Delta;
                Values[PlotVolume][0] = seg.TotalVolume;

                if (seg.HasMaxPos) Values[PlotDevMaxPos][0] = seg.MaxPosIdx * TickSize;
                if (seg.HasMaxNeg) Values[PlotDevMaxNeg][0] = seg.MaxNegIdx * TickSize;
            }
        }

        private long ToTickIndex(double price)
        {
            return (long)Math.Round(price / TickSize);
        }

        private static long FloorDiv(long a, long b)
        {
            return a >= 0 ? a / b : -((-a + b - 1) / b);
        }

        #endregion

        #region Public Accessors (strategies / other indicators)

        // Call these from OnBarUpdate (the data thread) of a hosting strategy
        // or indicator. They call Update() so the profile is current first.

        /// <summary>Returns the stored segment periodsAgo back (0 = latest). Call inside dataLock.</summary>
        private ProfileSegment GetSegment(int periodsAgo)
        {
            if (segments == null || periodsAgo < 0)
                return null;
            int i = segments.Count - 1 - periodsAgo;
            if (i < 0)
                return null;
            ProfileSegment s = segments[i];
            return s.TickCount == 0 ? null : s;
        }

        /// <summary>
        /// Delta levels for a stored profile. periodsAgo: 0 = latest
        /// (developing), 1 = previous, etc. Returns the price and delta of
        /// the largest positive and largest negative delta rows, plus the
        /// cumulative delta high/low reached. Prices are NaN when no such
        /// row exists; all values are NaN if the profile is not available.
        /// </summary>
        public bool TryGetDeltaLevels(int periodsAgo,
                                      out double maxPosPrice, out double maxPosDelta,
                                      out double maxNegPrice, out double maxNegDelta,
                                      out double cumDeltaHigh, out double cumDeltaLow)
        {
            Update();

            maxPosPrice = maxPosDelta = maxNegPrice = maxNegDelta = cumDeltaHigh = cumDeltaLow = double.NaN;
            lock (dataLock)
            {
                ProfileSegment s = GetSegment(periodsAgo);
                if (s == null)
                    return false;

                ComputeLevels(s);
                if (s.HasMaxPos) { maxPosPrice = s.MaxPosIdx * TickSize; maxPosDelta = s.MaxPosDelta; }
                if (s.HasMaxNeg) { maxNegPrice = s.MaxNegIdx * TickSize; maxNegDelta = s.MaxNegDelta; }
                cumDeltaHigh = s.CumDeltaHigh;
                cumDeltaLow  = s.CumDeltaLow;
                return true;
            }
        }

        /// <summary>Net delta of a stored profile (NaN if not available).</summary>
        public double GetPeriodDelta(int periodsAgo)
        {
            Update();
            lock (dataLock)
            {
                ProfileSegment s = GetSegment(periodsAgo);
                return s == null ? double.NaN : s.Delta;
            }
        }

        /// <summary>Total volume of a stored profile (NaN if not available).</summary>
        public double GetPeriodVolume(int periodsAgo)
        {
            Update();
            lock (dataLock)
            {
                ProfileSegment s = GetSegment(periodsAgo);
                return s == null ? double.NaN : s.TotalVolume;
            }
        }

        public double GetMaxPositiveDeltaPrice(int periodsAgo)
        {
            double pp, pd, np, nd, ch, cl;
            TryGetDeltaLevels(periodsAgo, out pp, out pd, out np, out nd, out ch, out cl);
            return pp;
        }

        public double GetMaxNegativeDeltaPrice(int periodsAgo)
        {
            double pp, pd, np, nd, ch, cl;
            TryGetDeltaLevels(periodsAgo, out pp, out pd, out np, out nd, out ch, out cl);
            return np;
        }

        #endregion

        #region Time Zones

        private void ResolveTimeZones()
        {
            platformTz = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo ?? TimeZoneInfo.Local;
            anchorTz   = platformTz;

            if (!string.IsNullOrEmpty(AnchorTimeZone)
                && AnchorTimeZone != DpTimeZoneConverter.PlatformTimeZone)
            {
                try
                {
                    anchorTz = TimeZoneInfo.FindSystemTimeZoneById(AnchorTimeZone);
                }
                catch (Exception)
                {
                    Print(string.Format(LogPrefix + "Time zone '{0}' not found. Using platform time zone '{1}'.", AnchorTimeZone, platformTz.Id));
                    anchorTz = platformTz;
                }
            }
        }

        private static DateTime ConvertTime(DateTime t, TimeZoneInfo from, TimeZoneInfo to)
        {
            if (from.Id == to.Id)
                return t;

            DateTime unspecified = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);

            // Wall-clock times inside a spring-forward gap do not exist; shift past it.
            if (from.IsInvalidTime(unspecified))
                unspecified = unspecified.AddHours(1);

            return TimeZoneInfo.ConvertTime(unspecified, from, to);
        }

        #endregion

        #region Diagnostics

        private void AccumulateBarVolume()
        {
            // Bar timestamps mark the bar's END, so step back one tick to
            // attribute a bar closing exactly on a boundary to the earlier period.
            DateTime t = Times[0][0].AddTicks(-1);

            DateTime key;
            if (!barTracker.TryGetPeriod(t, out key))
                return;

            double existing;
            diagBarVolume.TryGetValue(key, out existing);
            diagBarVolume[key] = existing + Volumes[0][0];
        }

        private void PrintHistoricalDiagnostics()
        {
            Print("=====================================================================");
            Print(string.Format(LogPrefix + "icPA - Delta Profile v{0} diagnostics | {1} | {2} | Scope: {3} | Classification: {4}",
                VersionString, Instrument.FullName, BarsArray[0].BarsPeriod, ProfileScope, Classification));
            Print(string.Format(LogPrefix + "Platform TZ: {0} | Anchor TZ: {1}", platformTz.Id, anchorTz.Id));
            if (ProfileScope == DpProfileScope.Anchored)
                Print(LogPrefix + "Anchored mode: bar volume only matches when window edges align with bar boundaries.");
            Print("---------------------------------------------------------------------");

            lock (dataLock)
            {
                for (int i = 0; i < segments.Count; i++)
                    PrintSegmentReport(segments[i], i == segments.Count - 1);
            }

            Print("=====================================================================");
        }

        /// <summary>Safe inside or outside dataLock (the lock is re-entrant).</summary>
        private void PrintSegmentReport(ProfileSegment s, bool developing)
        {
            if (s == null)
                return;

            double barVol;
            string volCheck = diagBarVolume.TryGetValue(s.PeriodBegin, out barVol)
                ? string.Format("bar vol {0:N0} | diff {1:N0}", barVol, s.TotalVolume - barVol)
                : "bar vol n/a";

            string levels = "";
            lock (dataLock)
            {
                ComputeLevels(s);
                if (s.Rows.Count > 0)
                {
                    MasterInstrument mi = Instrument.MasterInstrument;
                    levels = string.Format(" | +Dmax {0} -Dmax {1} | CD hi {2:N0} lo {3:N0}",
                        s.HasMaxPos ? mi.FormatPrice(s.MaxPosIdx * TickSize) : "n/a",
                        s.HasMaxNeg ? mi.FormatPrice(s.MaxNegIdx * TickSize) : "n/a",
                        s.CumDeltaHigh, s.CumDeltaLow);
                }
            }

            Print(string.Format(LogPrefix + "{0:yyyy-MM-dd HH:mm} -> {1:yyyy-MM-dd HH:mm}{2} | ticks {3:N0} | profile vol {4:N0} | {5} | delta {6:N0}{7} | bid/ask hist {8} rt {9} (snapshot {10})",
                s.PeriodBegin,
                s.PeriodEnd,
                developing ? " (developing)" : "",
                s.TickCount,
                s.TotalVolume,
                volCheck,
                s.Delta,
                levels,
                Pct(s.HistTicksWithBidAsk, s.HistTicks),
                Pct(s.RtTicksWithBidAsk, s.RtTicks),
                Pct(s.RtTicksFromSnapshot, s.RtTicks)));
        }

        private static string Pct(long part, long total)
        {
            return total == 0 ? "n/a" : string.Format("{0:0.0}%", 100.0 * part / total);
        }

        #endregion

        #region Render Logic (Direct2D / SharpDX)

        /// <summary>Render thread. Returns a reset view from the pool.</summary>
        private SegmentView RentView(int index)
        {
            SegmentView v;
            if (index < viewPool.Count)
            {
                v = viewPool[index];
            }
            else
            {
                v = new SegmentView();
                viewPool.Add(v);
            }
            v.Reset();
            return v;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);   // draws the (transparent by default) plots

            if (segments == null || workBrushDx == null || ChartBars == null || ChartBars.Bars == null)
                return;

            Bars bars = ChartBars.Bars;
            if (bars.Count == 0)
                return;

            // ---- 1. Snapshot segments and their levels (under lock) ----------
            List<SegmentView> views = renderViews;
            views.Clear();

            lock (dataLock)
            {
                for (int si = 0; si < segments.Count; si++)
                {
                    ProfileSegment seg = segments[si];
                    if (seg.TickCount == 0)
                        continue;

                    ComputeLevels(seg);

                    SegmentView v = RentView(views.Count);
                    v.Seg          = seg;
                    v.First        = seg.FirstTickTime;
                    v.Last         = seg.LastTickTime;
                    v.IsDeveloping = seg == currentSegment;
                    v.MinIdx       = seg.MinIdx;
                    v.MaxIdx       = seg.MaxIdx;
                    v.HasMaxPos    = seg.HasMaxPos;
                    v.HasMaxNeg    = seg.HasMaxNeg;
                    v.MaxPosIdx    = seg.MaxPosIdx;
                    v.MaxNegIdx    = seg.MaxNegIdx;
                    v.TotalVolume  = seg.TotalVolume;
                    v.Delta        = seg.Delta;
                    v.CumDeltaHigh = seg.CumDeltaHigh;
                    v.CumDeltaLow  = seg.CumDeltaLow;
                    views.Add(v);
                }
            }

            if (views.Count == 0)
                return;

            // ---- 2. Placement: bar indices, axis and bar length --------------
            int   fromIdx    = ChartBars.FromIndex;
            int   toIdx      = ChartBars.ToIndex;
            float barHalf    = (float)chartControl.Properties.BarDistance / 2f;
            float panelLeft  = ChartPanel.X;
            float panelRight = ChartPanel.X + ChartPanel.W;
            int   lastBar    = bars.Count - 1;
            bool  anyProfile = false;

            for (int vi = 0; vi < views.Count; vi++)
            {
                SegmentView v = views[vi];

                if (!v.IsDeveloping && v.Seg.BarIdxCached)
                {
                    v.StartIdx = v.Seg.CachedStartIdx;
                    v.EndIdx   = v.Seg.CachedEndIdx;
                }
                else
                {
                    v.StartIdx = Clamp(bars.GetBar(v.First), 0, lastBar);
                    v.EndIdx   = Clamp(bars.GetBar(v.Last),  0, lastBar);
                    if (!v.IsDeveloping)
                    {
                        v.Seg.CachedStartIdx = v.StartIdx;
                        v.Seg.CachedEndIdx   = v.EndIdx;
                        v.Seg.BarIdxCached   = true;
                    }
                }

                float sessionLeft  = chartControl.GetXByBarIndex(ChartBars, v.StartIdx) - barHalf;
                float sessionRight = chartControl.GetXByBarIndex(ChartBars, v.EndIdx)   + barHalf;

                bool rightOfBar = v.IsDeveloping
                    && DevelopingPosition == DpDevelopingPosition.RightOfCurrentBar;

                if (rightOfBar)
                {
                    // Preferred: fixed-width box just right of the current bar
                    float boxLeft = sessionRight + DevelopingGapPx;
                    float width   = DevelopingWidthPx;
                    v.HalfLen = width / 2f;
                    v.XLeft   = sessionLeft;
                    v.XRight  = boxLeft + width;

                    // When scrolled back so the box leaves the screen, keep the
                    // profile inside the visible part of the session (pinned to
                    // the right edge), the same way completed profiles behave.
                    float centre = boxLeft + v.HalfLen;
                    float visL   = Math.Max(sessionLeft, panelLeft);
                    float visR   = Math.Min(v.XRight,    panelRight);
                    if (visR - visL >= width)
                        centre = Math.Max(visL + v.HalfLen, Math.Min(visR - v.HalfLen, centre));
                    else if (visR > visL)
                        centre = (visL + visR) / 2f;
                    v.AxisX = centre;

                    v.ProfileVisible = visR > visL;
                }
                else
                {
                    v.XLeft  = sessionLeft;
                    v.XRight = sessionRight;

                    float span  = sessionRight - sessionLeft;
                    float width = Math.Min(span * (ProfileWidthPercent / 100f), MaxProfileWidthPx);
                    v.HalfLen   = width / 2f;

                    // Preferred position within the session
                    float centre;
                    switch (CompletedPosition)
                    {
                        case DpCompletedPosition.Start:
                            centre = sessionLeft + v.HalfLen;
                            break;
                        case DpCompletedPosition.Center:
                            centre = (sessionLeft + sessionRight) / 2f;
                            break;
                        default:
                            centre = sessionRight - v.HalfLen;
                            break;
                    }

                    // Keep the axis inside the visible part of the session, so
                    // the whole profile stays on screen when zoomed in.
                    float visL   = Math.Max(sessionLeft,  panelLeft);
                    float visR   = Math.Min(sessionRight, panelRight);
                    if (visR - visL >= width)
                        centre = Math.Max(visL + v.HalfLen, Math.Min(visR - v.HalfLen, centre));
                    else if (visR > visL)
                        centre = (visL + visR) / 2f;
                    v.AxisX = centre;

                    v.ProfileVisible = v.StartIdx <= toIdx && v.EndIdx >= fromIdx && v.HalfLen >= 0.5f;
                }

                if (v.ProfileVisible)
                    anyProfile = true;
            }

            for (int i = 0; i < views.Count - 1; i++)
                views[i].NextXRight = views[i + 1].XRight;

            // ---- 3. Row size and aggregation for visible profiles ------------
            // Aggregated rows are cached per segment and rebuilt only when the
            // grouping changes (zoom) or the segment received new trades.
            int g = ResolveGrouping(chartScale);

            if (anyProfile)
            {
                lock (dataLock)
                {
                    for (int vi = 0; vi < views.Count; vi++)
                    {
                        SegmentView v = views[vi];
                        if (!v.ProfileVisible)
                            continue;

                        ProfileSegment s = v.Seg;
                        if (s.AggRows == null || s.AggGrouping != g || s.AggVersion != s.Version)
                        {
                            double maxVol, maxAbsDelta;
                            s.AggRows        = AggregateRows(s.Rows, g, out maxVol, out maxAbsDelta);
                            s.AggGrouping    = g;
                            s.AggVersion     = s.Version;
                            s.AggMaxVol      = maxVol;
                            s.AggMaxAbsDelta = maxAbsDelta;
                        }

                        v.Rows        = s.AggRows;
                        v.MaxVol      = s.AggMaxVol;
                        v.MaxAbsDelta = s.AggMaxAbsDelta;
                    }
                }
            }

            // ---- 4. Scaling maxima -------------------------------------------
            double sharedMaxVol = 0, sharedMaxAbsDelta = 0;
            for (int vi = 0; vi < views.Count; vi++)
            {
                SegmentView v = views[vi];
                if (!v.ProfileVisible)
                    continue;
                if (v.MaxVol      > sharedMaxVol)      sharedMaxVol      = v.MaxVol;
                if (v.MaxAbsDelta > sharedMaxAbsDelta) sharedMaxAbsDelta = v.MaxAbsDelta;
            }

            // ---- 5. Draw, clipped to the panel -------------------------------
            RenderTarget.PushAxisAlignedClip(
                new SharpDX.RectangleF(ChartPanel.X, ChartPanel.Y, ChartPanel.W, ChartPanel.H),
                SharpDX.Direct2D1.AntialiasMode.Aliased);
            try
            {
                bool shared = WidthScaling == DpWidthScaling.SharedVisible;

                // Profiles
                for (int vi = 0; vi < views.Count; vi++)
                {
                    SegmentView v = views[vi];
                    if (!v.ProfileVisible || v.Rows == null)
                        continue;
                    DrawProfileRows(v, chartScale, g,
                        shared ? sharedMaxVol      : v.MaxVol,
                        shared ? sharedMaxAbsDelta : v.MaxAbsDelta);
                    DrawAxis(v, chartScale);
                }

                // Level lines (drawn for any segment whose line reaches the screen)
                for (int vi = 0; vi < views.Count; vi++)
                    DrawLevelLines(views[vi], chartScale, panelLeft, panelRight);

                // Text on top
                if (textFormat != null)
                {
                    for (int vi = 0; vi < views.Count; vi++)
                    {
                        SegmentView v = views[vi];
                        if (!v.ProfileVisible || v.Rows == null)
                            continue;
                        DrawRowText(v, chartScale, g);
                        DrawSummary(v, chartScale);
                    }
                }
            }
            finally
            {
                RenderTarget.PopAxisAlignedClip();
            }
        }

        private void DrawProfileRows(SegmentView v, ChartScale chartScale, int g, double maxVol, double maxAbsDelta)
        {
            float halfLen = v.HalfLen;
            float axisX   = v.AxisX;
            bool  ratio   = DeltaMeasure == DpDeltaMeasure.Ratio;

            double visMin = chartScale.MinValue;
            double visMax = chartScale.MaxValue;

            List<KeyValuePair<long, VolumeRow>> rows = v.Rows;
            for (int ri = 0; ri < rows.Count; ri++)
            {
                KeyValuePair<long, VolumeRow> kvp = rows[ri];

                float yTop, h;
                if (!RowGeometry(kvp.Key, g, chartScale, visMin, visMax, out yTop, out h))
                    continue;

                if (h >= 3)
                {
                    float pad = h * 0.1f;
                    yTop += pad / 2f;
                    h    -= pad;
                }

                VolumeRow row = kvp.Value;

                // Volume context: faint silhouette centred on the axis
                if (ShowVolumeContext && maxVol > 0)
                {
                    float half = (float)(row.TotalVol / maxVol * halfLen);
                    if (half > 0)
                    {
                        workBrushDx.Color = volumeContextC4;
                        RenderTarget.FillRectangle(new SharpDX.RectangleF(axisX - half, yTop, half * 2f, h), workBrushDx);
                    }
                }

                // Delta bar: positive to the right, negative to the left
                if (row.Delta == 0)
                    continue;

                double metric = ratio
                    ? (row.TotalVol > 0 ? Math.Abs(row.Delta) / row.TotalVol : 0)
                    : (maxAbsDelta > 0  ? Math.Abs(row.Delta) / maxAbsDelta  : 0);

                float w = (float)(metric * halfLen);
                if (w <= 0)
                    continue;

                workBrushDx.Color = DeltaColor(row.Delta, row.TotalVol);
                SharpDX.RectangleF rect = row.Delta > 0
                    ? new SharpDX.RectangleF(axisX, yTop, w, h)
                    : new SharpDX.RectangleF(axisX - w, yTop, w, h);
                RenderTarget.FillRectangle(rect, workBrushDx);
            }
        }

        private void DrawAxis(SegmentView v, ChartScale chartScale)
        {
            if (!ShowAxis)
                return;

            float yTop = chartScale.GetYByValue((v.MaxIdx + 0.5) * TickSize);
            float yBot = chartScale.GetYByValue((v.MinIdx - 0.5) * TickSize);

            workBrushDx.Color = neutralC4;
            RenderTarget.DrawLine(new SharpDX.Vector2(v.AxisX, yTop), new SharpDX.Vector2(v.AxisX, yBot), workBrushDx, 1f);
        }

        private void DrawLevelLines(SegmentView v, ChartScale chartScale, float panelLeft, float panelRight)
        {
            if (!ShowDeltaExtremes)
                return;

            float xStart = v.XLeft;
            float xEnd   = v.XRight;

            if (!v.IsDeveloping)
            {
                switch (LevelExtension)
                {
                    case DpLevelExtension.ToRightEdge:
                        xEnd = panelRight;
                        break;
                    case DpLevelExtension.ThroughNextSession:
                        if (!float.IsNaN(v.NextXRight)) xEnd = v.NextXRight;
                        break;
                }
            }

            if (xEnd < panelLeft || xStart > panelRight)
                return;

            if (v.HasMaxPos && PositiveDeltaStroke != null && PositiveDeltaStroke.BrushDX != null)
                DrawHLine(chartScale, v.MaxPosIdx * TickSize, xStart, xEnd, PositiveDeltaStroke);
            if (v.HasMaxNeg && NegativeDeltaStroke != null && NegativeDeltaStroke.BrushDX != null)
                DrawHLine(chartScale, v.MaxNegIdx * TickSize, xStart, xEnd, NegativeDeltaStroke);
        }

        private void DrawHLine(ChartScale chartScale, double price, float x1, float x2, Stroke stroke)
        {
            if (price < chartScale.MinValue || price > chartScale.MaxValue)
                return;

            float y = chartScale.GetYByValue(price);
            RenderTarget.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y),
                stroke.BrushDX, stroke.Width, stroke.StrokeStyle);
        }

        private void DrawRowText(SegmentView v, ChartScale chartScale, int g)
        {
            if (RowText == DpRowText.None)
                return;

            float minH  = textFormat.FontSize * 1.05f;
            float cellW = Math.Max(v.HalfLen, 40f);
            float axisX = v.AxisX;

            workBrushDx.Color = textC4;

            double visMin = chartScale.MinValue;
            double visMax = chartScale.MaxValue;

            List<KeyValuePair<long, VolumeRow>> rows = v.Rows;
            for (int ri = 0; ri < rows.Count; ri++)
            {
                KeyValuePair<long, VolumeRow> kvp = rows[ri];

                float yTop, h;
                if (!RowGeometry(kvp.Key, g, chartScale, visMin, visMax, out yTop, out h))
                    continue;
                if (h < minH)
                    return;   // all rows share the same height; none will fit

                VolumeRow row = kvp.Value;
                string text;
                switch (RowText)
                {
                    case DpRowText.Delta:
                        text = FormatCompact(row.Delta, true);
                        break;
                    case DpRowText.DeltaPercent:
                        text = row.TotalVol > 0
                            ? (100.0 * row.Delta / row.TotalVol).ToString("+0;-0;0") + "%"
                            : "0%";
                        break;
                    default:
                        text = FormatCompact(row.SellVol, false) + " x " + FormatCompact(row.BuyVol, false);
                        break;
                }

                // Text sits on the opposite side of the axis from its bar
                SharpDX.RectangleF rect;
                if (row.Delta > 0)
                {
                    textFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;
                    rect = new SharpDX.RectangleF(axisX - cellW - 3, yTop, cellW, h);
                }
                else
                {
                    textFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                    rect = new SharpDX.RectangleF(axisX + 3, yTop, cellW, h);
                }

                RenderTarget.DrawText(text, textFormat, rect, workBrushDx);
            }
        }

        private void DrawSummary(SegmentView v, ChartScale chartScale)
        {
            if (SummaryPosition == DpSummaryPosition.None || v.TotalVolume <= 0)
                return;

            string text = string.Format("\u0394 {0} ({1})\nCD {2} / {3}   Vol {4}",
                FormatCompact(v.Delta, true),
                (100.0 * v.Delta / v.TotalVolume).ToString("+0.0;-0.0;0.0") + "%",
                FormatCompact(v.CumDeltaHigh, true),
                FormatCompact(v.CumDeltaLow, true),
                FormatCompact(v.TotalVolume, false));

            float blockH = textFormat.FontSize * 2.6f;
            float y;
            if (SummaryPosition == DpSummaryPosition.Top)
                y = chartScale.GetYByValue((v.MaxIdx + 0.5) * TickSize) - blockH - 2;
            else
                y = chartScale.GetYByValue((v.MinIdx - 0.5) * TickSize) + 2;

            float w = Math.Max(v.HalfLen * 2f, 180f);

            textFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
            workBrushDx.Color = textC4;

            RenderTarget.DrawText(text, textFormat,
                new SharpDX.RectangleF(v.AxisX - w / 2f, y, w, blockH), workBrushDx);
        }

        /// <summary>Row position for group gi; false if off-screen vertically.</summary>
        private bool RowGeometry(long gi, int g, ChartScale chartScale, double visMin, double visMax,
                                 out float yTop, out float h)
        {
            // Row covers tick indices [gi*g, gi*g + g - 1], centred on those prices.
            long   lowTick   = gi * g;
            long   highTick  = lowTick + g - 1;
            double priceLow  = (lowTick  - 0.5) * TickSize;
            double priceHigh = (highTick + 0.5) * TickSize;

            yTop = 0;
            h    = 0;
            if (priceHigh < visMin || priceLow > visMax)
                return false;

            yTop = chartScale.GetYByValue(priceHigh);
            float yBot = chartScale.GetYByValue(priceLow);
            h = Math.Max(1f, yBot - yTop);
            return true;
        }

        private SharpDX.Color4 DeltaColor(double delta, double total)
        {
            if (delta == 0)
                return neutralC4;

            SharpDX.Color4 target = delta > 0 ? buyC4 : sellC4;
            if (ColorMode == DpColorMode.Solid || total <= 0)
                return target;

            double ratio = Math.Abs(delta) / total;
            float  t     = (float)Math.Min(1.0, ratio / (GradientSaturation / 100.0));
            return Lerp(neutralC4, target, t);
        }

        private static SharpDX.Color4 Lerp(SharpDX.Color4 a, SharpDX.Color4 b, float t)
        {
            return new SharpDX.Color4(
                a.Red   + (b.Red   - a.Red)   * t,
                a.Green + (b.Green - a.Green) * t,
                a.Blue  + (b.Blue  - a.Blue)  * t,
                a.Alpha + (b.Alpha - a.Alpha) * t);
        }

        /// <summary>12345 -> 12.3K, 1234567 -> 1.23M. Values under 10,000 shown in full.</summary>
        private static string FormatCompact(double value, bool signed)
        {
            double a = Math.Abs(value);
            string s;
            if (a >= 1e9)      s = (a / 1e9).ToString("0.##") + "B";
            else if (a >= 1e6) s = (a / 1e6).ToString("0.##") + "M";
            else if (a >= 1e4) s = (a / 1e3).ToString("0.#")  + "K";
            else if (a == Math.Floor(a)) s = a.ToString("0");
            else               s = a.ToString("0.##");

            if (value < 0)              return "-" + s;
            if (signed && value > 0)    return "+" + s;
            return s;
        }

        /// <summary>Picks the tick grouping for this frame.</summary>
        private int ResolveGrouping(ChartScale chartScale)
        {
            int manual = Math.Max(1, TickGrouping);
            if (RowSizing == DpRowSizing.Manual)
                return manual;

            double range = chartScale.MaxValue - chartScale.MinValue;
            if (range <= 0 || chartScale.Height <= 0)
                return manual;

            double pxPerTick = chartScale.Height / (range / TickSize);
            if (pxPerTick <= 0)
                return manual;

            int needed = (int)Math.Ceiling(MinRowHeight / pxPerTick);
            return NiceGrouping(Math.Max(manual, needed));
        }

        /// <summary>
        /// Snaps to 1, 2, 4, 5, 10, 20, 25, 40, 50, 100... so row boundaries
        /// stay stable while zooming (and suit both 4-tick and 5-tick points).
        /// </summary>
        private static int NiceGrouping(int n)
        {
            if (n <= 1) return 1;
            double[] steps = { 1, 2, 2.5, 4, 5 };
            for (long p = 1; p <= 1000000000L; p *= 10)
            {
                foreach (double m in steps)
                {
                    double c = m * p;
                    if (c != Math.Floor(c)) continue;
                    if (c >= n) return (int)c;
                }
            }
            return n;
        }

        /// <summary>
        /// Must be called inside dataLock. Returns rows grouped by g ticks as
        /// new VolumeRow objects owned by the render thread, plus their maxima.
        /// </summary>
        private static List<KeyValuePair<long, VolumeRow>> AggregateRows(Dictionary<long, VolumeRow> rows, int g,
                                                                         out double maxVol, out double maxAbsDelta)
        {
            Dictionary<long, VolumeRow> grouped = new Dictionary<long, VolumeRow>(rows.Count);
            foreach (KeyValuePair<long, VolumeRow> kvp in rows)
            {
                long gi = g == 1 ? kvp.Key : FloorDiv(kvp.Key, g);
                VolumeRow r;
                if (!grouped.TryGetValue(gi, out r))
                {
                    r = new VolumeRow();
                    grouped[gi] = r;
                }
                r.BuyVol  += kvp.Value.BuyVol;
                r.SellVol += kvp.Value.SellVol;
            }

            maxVol      = 0;
            maxAbsDelta = 0;
            List<KeyValuePair<long, VolumeRow>> list = new List<KeyValuePair<long, VolumeRow>>(grouped.Count);
            foreach (KeyValuePair<long, VolumeRow> kvp in grouped)
            {
                list.Add(kvp);
                double tv = kvp.Value.TotalVol;
                double ad = Math.Abs(kvp.Value.Delta);
                if (tv > maxVol)      maxVol      = tv;
                if (ad > maxAbsDelta) maxAbsDelta = ad;
            }
            return list;
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        public override void OnRenderTargetChanged()
        {
            DisposeRenderResources();

            buyC4           = MediaBrushToColor4(BuyColor,     ProfileOpacity);
            sellC4          = MediaBrushToColor4(SellColor,    ProfileOpacity);
            neutralC4       = MediaBrushToColor4(NeutralColor, ProfileOpacity);
            volumeContextC4 = MediaBrushToColor4(VolumeColor,  VolumeContextOpacity);
            textC4          = MediaBrushToColor4(TextColor,    100);

            if (PositiveDeltaStroke != null) PositiveDeltaStroke.RenderTarget = RenderTarget;
            if (NegativeDeltaStroke != null) NegativeDeltaStroke.RenderTarget = RenderTarget;

            if (RenderTarget != null)
            {
                workBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1f, 1f, 1f, 1f));

                if (TextFont != null)
                {
                    textFormat = TextFont.ToDirectWriteTextFormat();
                    textFormat.WordWrapping       = SharpDX.DirectWrite.WordWrapping.NoWrap;
                    textFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
                }
            }
        }

        private void DisposeRenderResources()
        {
            if (workBrushDx != null) { workBrushDx.Dispose(); workBrushDx = null; }
            if (textFormat  != null) { textFormat.Dispose();  textFormat  = null; }
        }

        private static SharpDX.Color4 MediaBrushToColor4(Brush mediaBrush, int opacityPercent)
        {
            float alpha = opacityPercent / 100f;
            SolidColorBrush solid = mediaBrush as SolidColorBrush;
            if (solid != null)
                return new SharpDX.Color4(solid.Color.R / 255f, solid.Color.G / 255f, solid.Color.B / 255f, alpha);
            return new SharpDX.Color4(0.5f, 0.5f, 0.5f, alpha);
        }

        #endregion

        #region Properties

        // ---------------------------------------------
        // 1. General
        // ---------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Classification", Order = 1, GroupName = GroupGeneral,
            Description = "BidAsk: trades at/above the ask are buys, at/below the bid are sells (tick rule inside the spread or when no quote exists). TickRule: up-tick/down-tick only.")]
        public NinjaTrader.NinjaScript.Indicators.icPA.DpClassification Classification { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profile Scope", Order = 2, GroupName = GroupGeneral,
            Description = "Session: one profile per trading session (trading hours template). Anchored: one profile per daily window starting at the anchor time.")]
        public NinjaTrader.NinjaScript.Indicators.icPA.DpProfileScope ProfileScope { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Anchor Start Time", Order = 3, GroupName = GroupGeneral,
            Description = "Anchored mode: time each profile starts, in the Anchor Time Zone.")]
        public DateTime AnchorStartTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Anchor End Time", Order = 4, GroupName = GroupGeneral,
            Description = "Anchored mode: stop the profile at the end time. If off, it runs until the next anchor.")]
        public bool UseAnchorEndTime { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Anchor End Time", Order = 5, GroupName = GroupGeneral,
            Description = "Anchored mode: time each profile ends. An end time earlier than the start crosses midnight.")]
        public DateTime AnchorEndTime { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(DpTimeZoneConverter))]
        [Display(Name = "Anchor Time Zone", Order = 6, GroupName = GroupGeneral,
            Description = "Time zone the anchor times are expressed in. Daylight saving is handled automatically.")]
        public string AnchorTimeZone { get; set; }

        [Range(1, 500)]
        [Display(Name = "Sessions To Keep", Order = 7, GroupName = GroupGeneral,
            Description = "Maximum number of session/anchored profiles kept in memory and drawn.")]
        public int SessionsToKeep { get; set; }

        [Display(Name = "Show Diagnostics", Order = 8, GroupName = GroupGeneral,
            Description = "Prints per-period volume checks, delta levels and bid/ask coverage to the NinjaScript Output window.")]
        public bool ShowDiagnostics { get; set; }

        // ---------------------------------------------
        // 2. Visuals
        // ---------------------------------------------
        [Display(Name = "Delta Measure", Order = 1, GroupName = GroupVisuals,
            Description = "Absolute: bar length is net delta in contracts. Ratio: bar length is delta / volume at that price (100% = completely one-sided).")]
        public DpDeltaMeasure DeltaMeasure { get; set; }

        [Display(Name = "Width Scaling", Order = 2, GroupName = GroupVisuals,
            Description = "Absolute measure only. PerSession: each profile scaled to its own largest delta. SharedVisible: all visible profiles share one scale, so they can be compared.")]
        public DpWidthScaling WidthScaling { get; set; }

        [Display(Name = "Completed Position", Order = 3, GroupName = GroupVisuals,
            Description = "Where completed profiles sit within their session: Start, Center or End. When zoomed in, the profile slides to stay inside the visible part of its session.")]
        public DpCompletedPosition CompletedPosition { get; set; }

        [Display(Name = "Developing Position", Order = 4, GroupName = GroupVisuals,
            Description = "RightOfCurrentBar: fixed-width profile just right of the current bar. WithSession: placed over the session's bars like completed profiles.")]
        public DpDevelopingPosition DevelopingPosition { get; set; }

        [Display(Name = "Auto Right Margin", Order = 5, GroupName = GroupVisuals,
            Description = "RightOfCurrentBar: raise the chart's right margin if it is too small for the developing profile (never reduces it). Restored when the indicator is removed.")]
        public bool AutoRightMargin { get; set; }

        [Display(Name = "Show Axis", Order = 6, GroupName = GroupVisuals,
            Description = "Draw a thin zero-delta axis line through each profile.")]
        public bool ShowAxis { get; set; }

        [Display(Name = "Show Volume Context", Order = 7, GroupName = GroupVisuals,
            Description = "Draw a faint volume silhouette, centred on the axis, behind the delta bars.")]
        public bool ShowVolumeContext { get; set; }

        [Display(Name = "Show Delta Extremes", Order = 8, GroupName = GroupVisuals,
            Description = "Lines at the prices with the largest positive and largest negative delta in each profile.")]
        public bool ShowDeltaExtremes { get; set; }

        [Display(Name = "+Delta Extreme Line", Order = 9, GroupName = GroupVisuals,
            Description = "Line style for the price with the largest positive delta.")]
        public Stroke PositiveDeltaStroke { get; set; }

        [Display(Name = "-Delta Extreme Line", Order = 10, GroupName = GroupVisuals,
            Description = "Line style for the price with the largest negative delta.")]
        public Stroke NegativeDeltaStroke { get; set; }

        [Display(Name = "Extend Levels", Order = 11, GroupName = GroupVisuals,
            Description = "How far completed profiles' level lines extend: None, to the right edge of the chart, or through the next session.")]
        public DpLevelExtension LevelExtension { get; set; }

        [Display(Name = "Row Text", Order = 12, GroupName = GroupVisuals,
            Description = "Text shown on each row, on the opposite side of the axis from its bar. Hidden automatically when rows are too small.")]
        public DpRowText RowText { get; set; }

        [Display(Name = "Summary", Order = 13, GroupName = GroupVisuals,
            Description = "Per-profile summary (net delta, delta %, cumulative delta high/low, volume) above or below the profile.")]
        public DpSummaryPosition SummaryPosition { get; set; }

        [Display(Name = "Font", Order = 14, GroupName = GroupVisuals,
            Description = "Font for row text and the summary label.")]
        public NinjaTrader.Gui.Tools.SimpleFont TextFont { get; set; }

        [XmlIgnore]
        [Display(Name = "Text Color", Order = 15, GroupName = GroupVisuals,
            Description = "Colour of row text and the summary label.")]
        public Brush TextColor { get; set; }

        [Browsable(false)]
        public string TextColorSerializable
        {
            get { return Serialize.BrushToString(TextColor); }
            set { TextColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Color Mode", Order = 16, GroupName = GroupVisuals,
            Description = "Gradient: colour intensity follows the delta ratio (|delta| / volume). Solid: full buy/sell colour.")]
        public DpColorMode ColorMode { get; set; }

        [XmlIgnore]
        [Display(Name = "Buy / Positive Delta", Order = 17, GroupName = GroupVisuals,
            Description = "Colour of positive delta bars.")]
        public Brush BuyColor { get; set; }

        [Browsable(false)]
        public string BuyColorSerializable
        {
            get { return Serialize.BrushToString(BuyColor); }
            set { BuyColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Sell / Negative Delta", Order = 18, GroupName = GroupVisuals,
            Description = "Colour of negative delta bars.")]
        public Brush SellColor { get; set; }

        [Browsable(false)]
        public string SellColorSerializable
        {
            get { return Serialize.BrushToString(SellColor); }
            set { SellColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Neutral / Axis", Order = 19, GroupName = GroupVisuals,
            Description = "Colour for near-zero delta (gradient start) and the axis line.")]
        public Brush NeutralColor { get; set; }

        [Browsable(false)]
        public string NeutralColorSerializable
        {
            get { return Serialize.BrushToString(NeutralColor); }
            set { NeutralColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Volume Context Color", Order = 20, GroupName = GroupVisuals,
            Description = "Colour of the volume silhouette behind the delta bars.")]
        public Brush VolumeColor { get; set; }

        [Browsable(false)]
        public string VolumeColorSerializable
        {
            get { return Serialize.BrushToString(VolumeColor); }
            set { VolumeColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [Display(Name = "Profile Opacity (%)", Order = 21, GroupName = GroupVisuals,
            Description = "Opacity of the delta bars and axis. 0 = invisible, 100 = solid.")]
        public int ProfileOpacity { get; set; }

        [Range(0, 100)]
        [Display(Name = "Volume Context Opacity (%)", Order = 22, GroupName = GroupVisuals,
            Description = "Opacity of the volume silhouette. 0 = invisible, 100 = solid.")]
        public int VolumeContextOpacity { get; set; }

        // ---------------------------------------------
        // 3. Offsets & Thresholds
        // ---------------------------------------------
        [Display(Name = "Row Sizing", Order = 1, GroupName = GroupThresholds,
            Description = "Auto: rows grouped to stay at least Min Row Height pixels tall. Manual: fixed Tick Grouping.")]
        public DpRowSizing RowSizing { get; set; }

        [Range(1, 50)]
        [Display(Name = "Min Row Height (px)", Order = 2, GroupName = GroupThresholds,
            Description = "Auto row sizing: minimum height of each row in pixels.")]
        public int MinRowHeight { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Tick Grouping", Order = 3, GroupName = GroupThresholds,
            Description = "Ticks per row in Manual mode; minimum ticks per row in Auto mode.")]
        public int TickGrouping { get; set; }

        [Range(5, 100)]
        [Display(Name = "Profile Width (%)", Order = 4, GroupName = GroupThresholds,
            Description = "Completed profiles: total width (both sides of the axis) as a percentage of the session's width.")]
        public int ProfileWidthPercent { get; set; }

        [Range(20, 2000)]
        [Display(Name = "Max Profile Width (px)", Order = 5, GroupName = GroupThresholds,
            Description = "Completed profiles: maximum total width in pixels, so profiles stay compact when zoomed in.")]
        public int MaxProfileWidthPx { get; set; }

        [Range(40, 1000)]
        [Display(Name = "Developing Width (px)", Order = 6, GroupName = GroupThresholds,
            Description = "RightOfCurrentBar: total width of the developing profile in pixels.")]
        public int DevelopingWidthPx { get; set; }

        [Range(5, 100)]
        [Display(Name = "Gradient Saturation (%)", Order = 7, GroupName = GroupThresholds,
            Description = "Delta ratio at which a row reaches full colour. Lower = more vivid.")]
        public int GradientSaturation { get; set; }

        // ---------------------------------------------
        // Plot series (for strategies / Market Analyzer)
        // ---------------------------------------------
        [Browsable(false)]
        [XmlIgnore]
        public Series<double> PeriodDelta { get { return Values[PlotDelta]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> PeriodVolume { get { return Values[PlotVolume]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> DevelopingMaxPositiveDelta { get { return Values[PlotDevMaxPos]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> DevelopingMaxNegativeDelta { get { return Values[PlotDevMaxNeg]; } }

        #endregion
    }

    // ---------------------------------------------------------------------
    // Custom types. Declared in the indicator's own namespace, after the
    // indicator, so the file has a single namespace and the NinjaScript
    // Editor resolves the icPA sub-namespace when it generates code.
    // Parameter types are written fully qualified on the properties, so
    // the generated code compiles in every namespace it is copied to.
    // ---------------------------------------------------------------------

    public enum DpProfileScope       { Session, Anchored }
    public enum DpClassification     { BidAsk, TickRule }
    public enum DpDeltaMeasure       { Absolute, Ratio }
    public enum DpRowSizing          { Auto, Manual }
    public enum DpWidthScaling       { PerSession, SharedVisible }
    public enum DpDevelopingPosition { RightOfCurrentBar, WithSession }
    public enum DpCompletedPosition  { Start, Center, End }
    public enum DpColorMode          { Gradient, Solid }
    public enum DpLevelExtension     { None, ToRightEdge, ThroughNextSession }
    public enum DpRowText            { None, Delta, DeltaPercent, SellXBuy }
    public enum DpSummaryPosition    { None, Top, Bottom }

    /// <summary>Drop-down list of Windows time zones for the property grid.</summary>
    public class DpTimeZoneConverter : StringConverter
    {
        public const string PlatformTimeZone = "Platform (NinjaTrader setting)";

        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            List<string> ids = new List<string> { PlatformTimeZone };
            foreach (TimeZoneInfo tz in TimeZoneInfo.GetSystemTimeZones())
                ids.Add(tz.Id);
            return new StandardValuesCollection(ids);
        }
    }
}