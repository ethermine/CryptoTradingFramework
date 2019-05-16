﻿using Crypto.Core.Exchanges.Base;
using Crypto.Core.Helpers;
using CryptoMarketClient;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Crypto.Core.Strategies {
    public class SimulationStrategyDataProvider : IStrategyDataProvider {
        public SimulationStrategyDataProvider() {
            StartTime = DateTime.MaxValue;
            EndTime = DateTime.MinValue;
        }

        bool IStrategyDataProvider.IsFinished { get { return FinishedCore; } }
        protected bool FinishedCore { get; set; }
        bool IStrategyDataProvider.Connect(StrategyInputInfo info) {
            for(int i = 0; i < info.Tickers.Count; i++) {
                TickerInputInfo ti = info.Tickers[i];

                ti.Ticker.CandleStickData.Clear();
                ti.Ticker.TradeHistory.Clear();

                ti.Ticker.Exchange.EnterSimulationMode();
                ti.Ticker.Exchange.StartListenTickerStream(ti.Ticker);
                //ti.Ticker.Exchange.StartListenOrderBook(ti.Ticker);
                //ti.Ticker.Exchange.StartListenTradeHistory(ti.Ticker);
                //ti.Ticker.Exchange.StartListenTickerStream(ti.Ticker);

                StrategySimulationData data = GetSimulationData(ti);
                if(data != null) data.Connected = true;
                StartTime = MinTime(StartTime, data.GetStartTime());
                EndTime = MaxTime(EndTime, data.GetEndTime());
            }
            LastTime = StartTime;
            return true;
        }

        public DateTime MaxTime(DateTime tm1, DateTime tm2) {
            return tm1 > tm2 ? tm1 : tm2;
        }

        public DateTime MinTime(DateTime tm1, DateTime tm2) {
            return tm1 < tm2 ? tm1 : tm2;
        }

        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }
        public double SimulationProgress { get; set; } = 0.0;
        public DateTime LastTime { get; private set; } = DateTime.Now;
        DateTime IStrategyDataProvider.CurrentTime { get { return LastTime; } }
        void IStrategyDataProvider.OnTick() {
            DateTime nextTime = GetNextTime();
            if(nextTime == DateTime.MaxValue)
                FinishedCore = true;
            UpdateTickersOrderBook();
            SendEventsWithTime(nextTime);
            LastTime = nextTime;
            SimulationProgress = (LastTime - StartTime).TotalMilliseconds / (EndTime - StartTime).TotalMilliseconds;
        }

        protected virtual void UpdateTickersOrderBook() {
            Random r = null;
            foreach(StrategySimulationData data in SimulationData.Values) {
                if(data.UseSimulationFile)
                    continue;
                data.Ticker.OrderBook.Assign(data.OrderBook);
                if(data.CandleStickData.Count == 0)
                    continue;
                CandleStickData cd = data.CandleStickData.First();
                if(r == null)
                    r = new Random(DateTime.Now.Millisecond);
                double newBid = cd.Low + (cd.High - cd.Low) * r.NextDouble();
                data.Ticker.OrderBook.Offset(newBid);
            }
        }

        protected void SendCandleStickDataEvent(Ticker ticker, List<CandleStickData> candleStickData) {
            CandleStickData data = candleStickData.First();
            candleStickData.RemoveAt(0);
            ticker.UpdateCandleStickData(data);
        }

        protected virtual void SendEventsWithTime(DateTime time) {
            foreach(StrategySimulationData s in SimulationData.Values) {
                if(!s.Connected)
                    continue;
                if(s.UseSimulationFile) {
                    s.Ticker.ApplyCapturedEvent(time);
                }
                else {
                    if(s.TickerInfo.UseKline) {
                        if(s.CandleStickData.Count > 0 && s.CandleStickData.First().Time == time) {
                            SendCandleStickDataEvent(s.Ticker, s.CandleStickData);
                        }
                    }
                }
            }
        }

        protected virtual DateTime GetNextTime() {
            DateTime minTime = DateTime.MaxValue;
            foreach(StrategySimulationData s in SimulationData.Values) {
                if(!s.Connected)
                    continue;
                if(s.UseSimulationFile) {
                    if(s.Ticker.CaptureDataHistory.CurrentItem == null)
                        continue;
                    DateTime time = s.Ticker.CaptureDataHistory.CurrentItem.Time;
                    if(minTime > time)
                        minTime = time;
                }
                else {
                    if(s.TickerInfo.UseKline) {
                        DateTime time = s.CandleStickData.Count == 0 ? DateTime.MaxValue : s.CandleStickData.First().Time;
                        if(minTime > time)
                            minTime = time;
                    }
                }
            }
            return minTime;
        }

        private StrategySimulationData GetSimulationData(TickerInputInfo ti) {
            StrategySimulationData data;
            if(SimulationData.TryGetValue(ti.Ticker, out data)) return data;
            return null;
        }

        bool IStrategyDataProvider.Disconnect(StrategyInputInfo info) {
            for(int i = 0; i < info.Tickers.Count; i++) {
                TickerInputInfo ti = info.Tickers[i];
                ti.Ticker.Exchange.ExitSimulationMode();
                StrategySimulationData data = GetSimulationData(ti);
                if(data != null) data.Connected = false;
            }
            return true;
        }

        protected Dictionary<ExchangeType, Exchange> Exchanges { get; } = new Dictionary<ExchangeType, Exchange>();

        Exchange IStrategyDataProvider.GetExchange(ExchangeType exchange) {
            if(Exchanges.ContainsKey(exchange))
                return Exchanges[exchange];

            Exchange e = Exchange.CreateExchange(exchange);
            if(!e.GetTickersInfo())
                return null;

            Exchanges.Add(exchange, e);
            return e;
        }

        void IStrategyDataProvider.Reset() {
            StartTime = DateTime.MaxValue;
            EndTime = DateTime.MinValue;
            FinishedCore = false;
            SimulationProgress = 0.0;
        }

        bool IStrategyDataProvider.InitializeDataFor(StrategyBase s) {
            StrategyInputInfo info = s.CreateInputInfo();
            for(int i = 0; i < info.Tickers.Count; i++) {
                TickerInputInfo ti = info.Tickers[i];
                Exchange e = ((IStrategyDataProvider) this).GetExchange(ti.Exchange);
                if(e == null)
                    return false;
                ti.Ticker = e.GetTicker(ti.TickerName);
                if(ti.Ticker == null)
                    return false;
                StrategySimulationData data = null;
                SimulationData.TryGetValue(ti.Ticker, out data);
                if(data == null)
                    data = new StrategySimulationData() { Ticker = ti.Ticker, Exchange = e, Strategy = s, TickerInfo = ti };
                if(!string.IsNullOrEmpty(ti.SimulationDataFile)) {
                    data.UseSimulationFile = true;
                    if(!ti.Ticker.CaptureDataHistory.Load(ti.SimulationDataFile))
                        return false;
                    data.CaptureDataHistory = ti.Ticker.CaptureDataHistory;
                }
                else {
                    if(ti.UseKline) {
                        if(data.LoadedCandleStickData == null)
                            data.LoadedCandleStickData = DownloadCandleStickData(ti);
                        data.CopyCandleSticksFromLoaded();
                        if(data.CandleStickData.Count == 0)
                            return false;
                    }
                    data.OrderBook = DownloadOrderBook(ti);
                    if(data.OrderBook == null)
                        return false;
                }
                if(!SimulationData.ContainsKey(ti.Ticker))
                    SimulationData.Add(ti.Ticker, data);
            }
            return true;
        }

        protected OrderBook DownloadOrderBook(TickerInputInfo ti) {
            if(!ti.Ticker.UpdateOrderBook())
                return null;
            OrderBook ob = new OrderBook(null);
            ob.Assign(ti.Ticker.OrderBook);
            return ob;
        }

        protected virtual List<CandleStickData> DownloadCandleStickData(TickerInputInfo info) {
            CachedCandleStickData savedData = new CachedCandleStickData() { Exchange = info.Exchange, TickerName = info.TickerName, IntervalMin = info.KlineIntervalMin };
            CachedCandleStickData cachedData = CachedCandleStickData.FromFile(CachedCandleStickData.Directory + "\\" + ((ISupportSerialization)savedData).FileName);
            if(cachedData != null)
                return cachedData.Items;

            DateTime start = DateTime.UtcNow;
            int intervalInSeconds = info.KlineIntervalMin * 60;
            int candleStickCount = 10000;
            start = start.AddSeconds(-intervalInSeconds * candleStickCount);
            List<CandleStickData> res = new List<CandleStickData>();
            List<BindingList<CandleStickData>> splitData = new List<BindingList<CandleStickData>>();
            int deltaCount = 500;

            for(int i = 0; i < candleStickCount / deltaCount; i++) {
                BindingList<CandleStickData> data = info.Ticker.GetCandleStickData(info.KlineIntervalMin, start, intervalInSeconds * deltaCount);
                if(data == null || data.Count == 0)
                    continue;
                res.AddRange(data);
                Thread.Sleep(300);
                start = start.AddSeconds(intervalInSeconds * deltaCount);
            }
            cachedData = savedData;
            cachedData.Items = res;
            cachedData.Save();
            return res;   
        }

        protected Dictionary<Ticker, StrategySimulationData> SimulationData { get; } = new Dictionary<Ticker, StrategySimulationData>();

        AccountInfo IStrategyDataProvider.GetAccount(Guid accountId) {
            return null;
        }
    }

    public class StrategySimulationData {
        public bool Connected { get; set; }
        public Exchange Exchange { get; set; }
        public Ticker Ticker { get; set; }
        public StrategyBase Strategy { get; set; }
        public List<CandleStickData> LoadedCandleStickData { get; set; }
        public List<CandleStickData> CandleStickData { get; set; }
        public OrderBook OrderBook { get; set; }
        public ExchangeInputInfo ExchangeInfo { get; set; }
        public TickerInputInfo TickerInfo { get; set; }
        public bool UseSimulationFile { get; set; }
        public TickerCaptureData CaptureDataHistory { get; internal set; }

        public bool CopyCandleSticksFromLoaded() {
            if(LoadedCandleStickData == null)
                return false;
            CandleStickData = new List<CryptoMarketClient.CandleStickData>();
            CandleStickData.AddRange(LoadedCandleStickData);
            return true;
        }

        public DateTime MaxTime(DateTime tm1, DateTime tm2) {
            return tm1 > tm2 ? tm1 : tm2;
        }

        public DateTime MinTime(DateTime tm1, DateTime tm2) {
            return tm1 < tm2 ? tm1 : tm2;
        }

        public DateTime GetStartTime() {
            DateTime start = DateTime.MaxValue;
            if(CaptureDataHistory != null)
                start = MinTime(start, CaptureDataHistory.Items.First().Time);
            if(CandleStickData != null)
                start = MinTime(start, CandleStickData.First().Time);
            return start;
        }

        public DateTime GetEndTime() {
            DateTime end = DateTime.MinValue;
            if(CaptureDataHistory != null)
                end = MaxTime(end, CaptureDataHistory.Items.Last().Time);
            if(CandleStickData != null)
                end = MaxTime(end, CandleStickData.Last().Time);
            return end;
        }
    }

    [Serializable]
    public class CachedCandleStickData : ISupportSerialization {
        public static string Directory { get { return "SimulationData\\CandleStickData"; } }
        public static CachedCandleStickData FromFile(string fileName) {
            return (CachedCandleStickData)SerializationHelper.FromFile(fileName, typeof(CachedCandleStickData));
        }

        void ISupportSerialization.OnEndDeserialize() {
            
        }

        public bool Save() {
            return SerializationHelper.Save(this, typeof(CachedCandleStickData), Directory);
        }

        string ISupportSerialization.FileName { get { return Exchange.ToString() + "_" + TickerName.ToString() + "_" + IntervalMin.ToString() + "_CandleStickData.xml"; } set { } }

        public List<CandleStickData> Items { get; set; } = new List<CandleStickData>();
        public ExchangeType Exchange { get; set; }
        public string TickerName { get; set; }
        public int IntervalMin { get; set; }
        
    }
}
