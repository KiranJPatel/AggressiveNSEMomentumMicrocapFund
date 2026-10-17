# Dashboard smoke test (jsdom, populated data)

Loads the real dashboard against a real `serve` process and a scratch database, drives every tab and the backtest
detail view, and fails on any JavaScript error. It catches exactly the class of bug that no .NET test can (this suite found
that `window.btEqChart` resolved to a DOM element and threw on first open of a backtest). It does **not** check pixels, fonts or
colours — look at the page in a browser for that.

```bash
export Database__DatabasePath=/tmp/ui-smoke/tp.db ASPNETCORE_URLS=http://127.0.0.1:5097
mkdir -p /tmp/ui-smoke && rm -f /tmp/ui-smoke/tp.db*
dotnet run -- killswitch status                       # creates the schema in the scratch DB
dotnet run -- universe import --file config/samples/universe.csv
python3 scripts/ui-smoke/seed.py /tmp/ui-smoke/tp.db
(dotnet run -- serve &) ; sleep 8
cd scripts/ui-smoke && npm i jsdom && TP_URL=http://127.0.0.1:5097 node smoke.js   # prints JSON; "errors": [] means pass
```
Icon names appear as plain text under jsdom (no icon font) — expected.
