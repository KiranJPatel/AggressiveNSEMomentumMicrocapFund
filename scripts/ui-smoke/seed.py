#!/usr/bin/env python3
"""Seed a SCRATCH database (never your real one) with synthetic bars + dashboard rows so the UI can be exercised with data.
Usage: python3 seed.py /tmp/ui-smoke/tp.db   (the schema must already exist: run any CLI command against that DB first)"""
import sqlite3, sys, random, datetime as dt
random.seed(7)
db = sqlite3.connect(sys.argv[1]); now = dt.datetime.now(dt.timezone.utc)
iso = lambda t: t.isoformat()
iso7 = lambda t: t.strftime('%Y-%m-%dT%H:%M:%S.0000000+00:00')   # the repository compares timestamps as exact strings
days = []; d = now.date() - dt.timedelta(days=480)
while d <= now.date():
    if d.weekday() < 5: days.append(d)
    d += dt.timedelta(days=1)
def series(start, drift, vol, base_vol):
    p = start; out = []
    for day in days:
        r = drift + random.gauss(0, vol); o = p; c = max(5, p*(1+r))
        out.append((day, o, max(o, c)*(1+abs(random.gauss(0, .004))), min(o, c)*(1-abs(random.gauss(0, .004))), c, int(base_vol*(1+random.gauss(0, .15))))); p = c
    return out
for sym, s in {'^CRSLDX': series(15000, .0007, .007, 1), 'NMIDCAP150.NS': series(18000, .0009, .009, 1), 'EXAMPLEA': series(100, .0012, .012, 600000), 'EXAMPLEB': series(80, .0006, .014, 700000)}.items():
    for day, o, h, l, c, v in s:
        db.execute("INSERT OR REPLACE INTO HistoricalBars (Symbol,Timestamp,[Open],High,Low,[Close],AdjustedClose,Volume,Source,Timeframe,CreatedAt) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                   (sym, dt.datetime(day.year, day.month, day.day).isoformat()+'+00:00', f'{o:.4f}', f'{h:.4f}', f'{l:.4f}', f'{c:.4f}', f'{c:.4f}', v, 'synthetic', 1, iso(now)))
for i, (sym, px) in enumerate([('EXAMPLEA', 142.5), ('EXAMPLEB', 88.2)], 1):
    db.execute("INSERT INTO ScannerResults (ScanTimestamp,Rank,Symbol,Price,MarketCap,Sector,Score,RelativeStrengthScore,MomentumScore,VolumeScore,VcpScore,Adx,Rsi,Atr,PivotPrice,BreakoutStatus,DistanceFromPivotPercent,SuggestedEntry,SuggestedStop,Risk,SuggestedPositionSize) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
               (iso7(now), i, sym, str(px), '15000', 'Capital Goods', str(90-i*7), '85', '78', '70', '82', '31', '64', '3.1', str(px*1.01), 'NearPivot', '1.2', str(px*1.012), str(px*.93), '20000', '120'))
db.execute("INSERT INTO Signals (Symbol,GeneratedAt,Stage,PivotPrice,SuggestedEntry,SuggestedStop,RiskPerShare,SuggestedPositionQty,SignalToOrderLag,StrategyVersion,ConfigurationVersion,CreatedAt) VALUES ('EXAMPLEA',?,1,'140','141','131','10','120',0,'AggressiveMomentumV1','live',?)", (iso(now), iso(now)))
for sym, side, st, q, br in [('EXAMPLEA', 0, 6, '48', 'K100'), ('EXAMPLEA', 0, 2, '36', 'K101'), ('EXAMPLEB', 0, 8, '50', None)]:
    db.execute("INSERT INTO Orders (SignalId,Symbol,Side,Type,State,Quantity,BrokerOrderId,RejectionReason,CorrelationId,CreatedAt,UpdatedAt) VALUES (NULL,?,?,0,?,?,?,?,?,?,?)", (sym, side, st, q, br, 'Insufficient funds' if st == 8 else None, f'c-{sym}-{q}', iso(now), iso(now)))
db.execute("INSERT INTO Positions (Symbol,Quantity,AverageEntryPrice,InitialStop,CurrentStop,InitialRiskAmount,RiskPerShare,HighestStageFilled,IsOpen,OpenedAt) VALUES ('EXAMPLEA','48','141.2','131.2','138.4','480','10',1,1,?)", (iso(now-dt.timedelta(days=6)),))
nav = peak = 1000000.0
for k in range(60, 0, -1):
    nav *= 1+random.gauss(.002, .01); peak = max(peak, nav)
    db.execute("INSERT INTO PortfolioSnapshots (AsOfDate,Nav,CashBalance,PeakNav,DrawdownPercent,PortfolioHeatPercent,OpenPositionCount,Regime,CreatedAt) VALUES (?,?,?,?,?,?,?,?,?)", ((now-dt.timedelta(days=k)).date().isoformat(), f'{nav:.2f}', f'{nav*.4:.2f}', f'{peak:.2f}', f'{(peak-nav)/peak*100:.2f}', '3.2', 1, 3, iso(now)))
for sym, e, x, pnl, r, reason in [('OLDCO', 100, 112, 1200, 1.5, 4), ('ZED', 50, 46, -400, -1.0, 0)]:
    cur = db.execute("INSERT INTO Trades (Symbol,SignalId,EntryPrice,InitialStop,InitialRiskAmount,ExitPrice,ExitReason,RealizedPnl,RMultiple,MaximumFavorableExcursion,MaximumAdverseExcursion,RegimeScoreAtEntry,StockScoreAtEntry,RiskAtEntryPercent,StrategyVersion,ConfigurationVersion,EntryAt,ExitAt) VALUES (?,NULL,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                     (sym, str(e), str(e*.93), '800', str(x), reason, str(pnl), str(r), str(abs(r)+.6), '-0.4', 72, '81.5', '1', 'AggressiveMomentumV1', 'live', iso(now-dt.timedelta(days=20)), iso(now-dt.timedelta(days=3))))
    db.execute("INSERT INTO TradeLegs (TradeId,Stage,Quantity,Price,FilledAt) VALUES (?,1,'100',?,?)", (cur.lastrowid, str(e), iso(now-dt.timedelta(days=20))))
rid = db.execute("INSERT INTO BacktestRuns (StrategyVersion,ConfigurationHash,DataVersion,StartDate,EndDate,Status,SurvivorshipBiased,FinalCapital,Cagr,MaxDrawdownPercent,Sharpe,Sortino,WinRatePercent,ProfitFactor,ExpectancyR,TradeCount,StartedAt,CompletedAt) VALUES ('AggressiveMomentumV1','abc123','synthetic','2025-01-01','2026-01-01',1,1,'1320000','32.0','14.5','1.4','2.0','46','1.9','0.42',2,?,?)", (iso(now), iso(now))).lastrowid
for k in range(200, 0, -5):
    db.execute("INSERT INTO BacktestSnapshots (BacktestRunId,AsOfDate,Nav,CashBalance,PeakNav,DrawdownPercent,PortfolioHeatPercent,OpenPositionCount,Regime,CreatedAt) VALUES (?,?,?,?,?,?,?,?,?,?)", (rid, (now-dt.timedelta(days=k+200)).date().isoformat(), str(1e6+(200-k)*1500), '400000', str(1e6+(200-k)*1500), '2.0', '3', 2, 3, iso(now)))
db.commit(); print('seeded', sys.argv[1])
