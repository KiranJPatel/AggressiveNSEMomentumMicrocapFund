const { JSDOM } = require('jsdom');
const BASE=process.env.TP_URL || 'http://127.0.0.1:5097';
(async () => {
  const html = await (await fetch(BASE+'/')).text();
  const errors = [];
  const dom = new JSDOM(html, { url: BASE+'/', runScripts: 'outside-only', pretendToBeVisual: true });
  const w = dom.window;
  const optionCalls = [];
  w.fetch = (u, o) => fetch(new URL(u, BASE), o);
  w.echarts = { init: () => ({ setOption(o){ optionCalls.push(JSON.stringify(o).length); }, resize(){}, dispose(){}, on(){}, off(){}, getDom: () => w.document.body, getWidth: () => 800, getHeight: () => 400, dispatchAction(){}, group:'', clear(){}, convertToPixel(){return [0,0]} }), connect(){} , getInstanceByDom(){return null}};
  w.ResizeObserver = class { observe(){} disconnect(){} };
  w.scrollTo = () => {};
  w.addEventListener('error', e => errors.push('window.error: '+e.message));
  w.console.error = (...a) => errors.push('console.error: '+a.join(' '));
  for (const s of [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => m[1])) { try { w.eval(s); } catch (e) { errors.push('eval: ' + e.message); } }
  await new Promise(r => setTimeout(r, 3000));
  const d = w.document, out = {};
  const rows = id => d.querySelectorAll('#panel-'+id+' tbody tr').length;
  out.cards = [...d.querySelectorAll('#summaryCards .card')].map(c=>c.textContent.trim().replace(/\s+/g,' ').slice(0,40));
  out.hero = d.getElementById('heroBanner').textContent.trim().replace(/\s+/g,' ').slice(0,100);
  for (const id of ['positions','orders','signals','scanner']) { w.switchTab(id); out[id+'Rows']=rows(id); }
  out.positionsSample = d.querySelector('#panel-positions tbody tr')?.textContent.trim().replace(/\s+/g,' ').slice(0,110);
  w.switchTab('chart'); await new Promise(r=>setTimeout(r,1500));
  out.chartSymbols = d.querySelectorAll('#panel-chart .symbol-item').length;
  out.chartLegend = d.querySelector('#panel-chart .chart-legend')?.textContent.trim().replace(/\s+/g,' ').slice(0,90);
  w.switchTab('backtests'); await new Promise(r=>setTimeout(r,500)); out.backtestRows = rows('backtests');
  await w.showBacktestDetail(1); await new Promise(r=>setTimeout(r,1500)); out.backtestDetail = d.getElementById('panel-backtests').textContent.replace(/\s+/g,' ').slice(0,200);
  w.switchTab('risk'); await new Promise(r=>setTimeout(r,500)); out.riskLen = d.getElementById('panel-risk').innerHTML.length;
  w.switchTab('settings'); out.settingsCards = d.querySelectorAll('#panel-settings .set-card').length;
  out.echartsOptionsBuilt = optionCalls.length;
  const tpl = html.match(/class="[^"]*"/g)?.length;
  out.errors = errors;
  console.log(JSON.stringify(out, null, 1)); process.exit(0);
})();
