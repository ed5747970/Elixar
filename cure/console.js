// Cure Console. Everything a PC sent is shown with textContent (never as HTML), so a report can't run code here.
(function () {
  "use strict";
  var all = [], filter = "all", seenUser = "";
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined && text !== null) e.textContent = String(text); return e; }
  function clear(e) { while (e.firstChild) e.removeChild(e.firstChild); }
  function gb(b) { if (b === null || b === undefined || b < 0) return "?"; var g = b / 1073741824; return g >= 1024 ? (g / 1024).toFixed(1) + " TB" : g >= 10 ? Math.round(g) + " GB" : g.toFixed(1) + " GB"; }
  function ago(s) {
    if (!s) return "never"; var t = Date.parse(s); if (isNaN(t)) return "?";
    var m = Math.round((Date.now() - t) / 60000);
    if (m < 1) return "now"; if (m < 60) return m + " min"; if (m < 1440) return Math.round(m / 60) + " h"; var dd = Math.round(m / 1440); return dd === 1 ? "1 day" : dd + " days";
  }
  function daysAgo(s) {
    if (!s) return "never"; var t = Date.parse(s); if (isNaN(t)) return "?";
    var d = Math.floor((Date.now() - t) / 86400000);
    return d <= 0 ? "today" : d === 1 ? "yesterday" : d + " days ago";
  }
  var STATES = { disk: ["Disk failing", "var(--critical)", 0], full: ["Almost full", "var(--critical)", 1], worn: ["Disk worn", "var(--caution)", 2],
                 old: ["Old Elixar", "var(--caution)", 3], off: ["Not seen 7+ days", "var(--quiet)", 4], ok: ["OK", "var(--safe)", 9] };
  function st(s) { return STATES[s] || STATES.ok; }
  function pct(p) { return p.total > 0 && p.free >= 0 ? Math.round(p.free * 100 / p.total) : -1; }
  function get(url) { return fetch(url, { credentials: "same-origin", headers: { "Accept": "application/json" } }).then(function (r) { if (!r.ok) throw new Error(r.status + " " + r.statusText); return r.json(); }); }

  function kpi(box, cap, val, note, cls) { var k = el("div", "kpi" + (cls ? " " + cls : "")); k.appendChild(el("div", "cap", cap)); k.appendChild(el("div", "val", val)); k.appendChild(el("div", "note", note)); box.appendChild(k); }

  function render() {
    var k = $("kpis"); clear(k);
    var seen = all.filter(function (p) { return p.state !== "off"; }).length;
    var full = all.filter(function (p) { return p.state === "full"; }).length, disk = all.filter(function (p) { return p.state === "disk" || p.state === "worn"; }).length;
    var old = all.filter(function (p) { return p.state === "old"; }).length;
    var clean = all.reduce(function (a, p) { return a + Math.max(0, p.clean); }, 0), saved = all.reduce(function (a, p) { return a + Math.max(0, p.saved); }, 0), month = all.reduce(function (a, p) { return a + Math.max(0, p.month); }, 0);
    kpi(k, "PCs reporting", seen + " / " + all.length, (all.length - seen) + " not seen for 7+ days");
    kpi(k, "C: almost full", full, "under 10% free", full ? "bad" : "good");
    kpi(k, "Disk warnings", disk, "failing or worn", disk ? "bad" : "good");
    kpi(k, "Can be cleaned", gb(clean), "Safe items, all PCs");
    kpi(k, "Freed so far", gb(saved), gb(month) + " this month", "good");
    kpi(k, "Old Elixar", old, "older than the newest PC", old ? "warn" : "");

    var f = $("filters"); clear(f);
    [["all", "All", all.length], ["need", "Needs attention", all.filter(function (p) { return p.state !== "ok"; }).length], ["full", "Almost full", full], ["disk", "Disk", disk], ["off", "Not seen", all.length - seen]].forEach(function (x) {
      var c = el("span", "f" + (filter === x[0] ? " on" : ""), x[1] + " " + x[2]); c.addEventListener("click", function () { filter = x[0]; render(); }); f.appendChild(c);
    });

    var q = $("find").value.trim().toUpperCase();
    var rows = all.filter(function (p) {
      if (q && p.pc.indexOf(q) < 0) return false;
      if (filter === "need") return p.state !== "ok"; if (filter === "full") return p.state === "full";
      if (filter === "disk") return p.state === "disk" || p.state === "worn"; if (filter === "off") return p.state === "off"; return true;
    }).sort(function (a, b) { return st(a.state)[2] - st(b.state)[2] || pct(a) - pct(b); });
    var tb = $("rows"); clear(tb);
    if (!rows.length) { var tr0 = el("tr"), td0 = el("td", "empty", all.length ? "No PC matches." : "No PC has reported yet. Install Cure Agent on the PCs (see the guide)."); td0.colSpan = 9; tr0.appendChild(td0); tb.appendChild(tr0); }
    rows.forEach(function (p) {
      var tr = el("tr"), s = st(p.state), fp = pct(p);
      var cell = tr.appendChild(el("td")); cell.appendChild(el("b", null, p.pc));
      // two PCs enrolled with this name: one may be a copy made with the enrollment token (remove it with CureServer.exe --remove-pc <id>)
      if (p.twice) { var w = el("span", "twice", " enrolled twice"); w.title = "Enrolled " + String(p.enrolled || "") + " from " + String(p.ip || "") + " · id " + String(p.id || ""); cell.appendChild(w); }
      var c2 = el("td"), sp = el("span", "st"), dot = el("span", "dot"); dot.style.background = s[1]; sp.appendChild(dot); sp.appendChild(document.createTextNode(s[0])); c2.appendChild(sp); tr.appendChild(c2);
      var c3 = el("td"), bar = el("span", "bar"), fill = el("i"); fill.style.width = Math.max(0, fp) + "%"; fill.style.background = fp < 10 ? "var(--critical)" : fp < 20 ? "var(--caution)" : "var(--safe)";
      bar.appendChild(fill); c3.appendChild(bar); c3.appendChild(document.createTextNode((fp < 0 ? "?" : fp + "%") + " · " + gb(p.free) + " / " + gb(p.total))); tr.appendChild(c3);
      tr.appendChild(el("td", "n", gb(p.clean)));
      var dk = el("td", null, p.disk === "Healthy" ? "Good" : p.disk || "?"); if (p.disk === "Failing" || p.disk === "Warning") dk.style.color = "var(--critical)"; tr.appendChild(dk);
      tr.appendChild(el("td", null, daysAgo(p.lastClean)));
      tr.appendChild(el("td", "n", p.updates < 0 ? "?" : p.updates));
      tr.appendChild(el("td", null, p.elixar || "?"));
      tr.appendChild(el("td", null, ago(p.seen)));
      tr.addEventListener("click", function () { location.hash = "pc=" + encodeURIComponent(p.id); });
      tb.appendChild(tr);
    });

    var al = $("alerts"); clear(al);
    var bad = all.filter(function (p) { return p.state !== "ok"; }).sort(function (a, b) { return st(a.state)[2] - st(b.state)[2]; }).slice(0, 12);
    if (!bad.length) al.appendChild(el("li", null, "Nothing needs attention."));
    bad.forEach(function (p) {
      var li = el("li"), d = el("span", "dot"); d.style.background = st(p.state)[1]; d.style.marginTop = "6px"; li.appendChild(d);
      var t = el("div"); t.appendChild(el("b", null, p.pc)); t.appendChild(document.createTextNode(" " + st(p.state)[0] + (p.state === "full" ? " (" + gb(p.free) + " free)" : ""))); li.appendChild(t);
      li.appendChild(el("span", "t", ago(p.seen))); al.appendChild(li);
    });
  }

  function showPc(id) {
    $("overview").className = "view"; $("pc").className = "view on";
    get("api/v1/pc?id=" + encodeURIComponent(id)).then(function (d) {
      var r = d.report || {};
      $("pcName").textContent = d.pc + (d.twice ? " (enrolled twice: this one " + String(d.enrolled || "") + " from " + String(d.ip || "") + ", id " + String(d.id || "") + ")" : "");
      var k = $("pcKpis"); clear(k);
      var fp = r.Total > 0 ? Math.round(r.Free * 100 / r.Total) : -1;
      kpi(k, "C: free", gb(r.Free), (fp < 0 ? "?" : fp + "%") + " of " + gb(r.Total), fp >= 0 && fp < 10 ? "bad" : "");
      kpi(k, "Can be cleaned", gb(r.CleanSafe), "Check first " + gb(r.CleanReview));
      kpi(k, "Disk health", r.DiskLevel === "Healthy" ? "Good" : r.DiskLevel || "?", "", r.DiskLevel === "Failing" ? "bad" : r.DiskLevel === "Warning" ? "warn" : "good");
      kpi(k, "Last clean", daysAgo(r.LastClean), r.LastCleanBytes > 0 ? "freed " + gb(r.LastCleanBytes) : "");
      kpi(k, "App updates", r.AppUpdates < 0 ? "?" : r.AppUpdates, "from winget / Store", r.AppUpdates > 5 ? "warn" : "");
      kpi(k, "Startup programs", r.StartupCount < 0 ? "?" : r.StartupCount, "start with Windows");
      var dt = $("drives"); clear(dt);
      (r.Drives || []).forEach(function (x) { var tr = el("tr"); tr.appendChild(el("td", null, x.Letter + ":")); tr.appendChild(el("td", "n", gb(x.Free) + " free of " + gb(x.Total))); dt.appendChild(tr); });
      var ds = $("disks"); clear(ds);
      (r.Disks || []).forEach(function (x) {
        var tr = el("tr"); tr.appendChild(el("td", null, x.Name)); tr.appendChild(el("td", null, x.Kind));
        tr.appendChild(el("td", null, (x.Level === "Healthy" ? "Good" : x.Level) + (x.Wear >= 0 ? " · wear " + x.Wear + "%" : "") + (x.Temp > 0 ? " · " + x.Temp + " °C" : ""))); ds.appendChild(tr);
      });
      var fa = $("facts"); clear(fa);
      [["Windows", r.Windows], ["Model", r.Model], ["Company", r.Company], ["OU", r.Ou], ["Up since", r.BootTime ? r.BootTime.replace("T", " ") + (r.PendingReboot ? " · restart pending" : "") : ""],
       ["Elixar", (r.Elixar || "?") + " · Cure Agent " + (r.Agent || "?")], ["Policy", (r.PolicyMode || "?") + (r.PolicyFingerprint ? " · " + r.PolicyFingerprint.substr(0, 8) : "")],
       ["Company locks", (r.Locks || []).length ? r.Locks.join(", ") + " off" : "none"], ["Event Log", r.EventLog ? "On" : "Off"], ["Enrolled", (d.enrolled || "").replace("T", " ")],
       ["Problems", (r.Errors || []).join(" · ") || "none"]].forEach(function (x) { fa.appendChild(el("dt", null, x[0])); fa.appendChild(el("dd", null, x[1] || "?")); });
      var h = (d.history || []).filter(function (x) { return x[2] > 0; }).slice(-30), svg = $("spark"); clear(svg);
      if (h.length > 1) {
        var vals = h.map(function (x) { return x[1] / 1073741824; }), mx = Math.max.apply(null, vals), mn = Math.min.apply(null, vals);
        var pts = vals.map(function (v, i) { return (i * 300 / (vals.length - 1)).toFixed(1) + "," + (110 - (v - mn) / ((mx - mn) || 1) * 95).toFixed(1); }).join(" ");
        var pl = document.createElementNS("http://www.w3.org/2000/svg", "polyline"); pl.setAttribute("points", pts); pl.setAttribute("fill", "none"); pl.style.stroke = "var(--accent)"; pl.style.strokeWidth = "2"; svg.appendChild(pl);
        var per = (vals[vals.length - 1] - vals[0]) / (h.length - 1);
        $("trend").textContent = per < -0.05 ? "Dropping about " + (-per).toFixed(1) + " GB a day" + (per < 0 ? ": full in about " + Math.max(1, Math.round(vals[vals.length - 1] / -per)) + " days" : "") : "Steady";
      } else $("trend").textContent = "A line shows here after a few days of reports.";
    }).catch(function (e) { $("pcName").textContent = "Couldn't load this PC: " + e.message; });
  }

  function route() {
    var m = /^#pc=([0-9a-f]{1,64})$/.exec(location.hash);
    if (m) showPc(m[1]); else { $("pc").className = "view"; $("overview").className = "view on"; }
  }

  function load() {
    get("api/v1/pcs").then(function (d) {
      all = (d.pcs || []).map(function (p) { p.pc = String(p.pc || ""); return p; });
      if (d.user !== seenUser) { seenUser = d.user; $("user").textContent = d.user; }
      $("status").textContent = all.length + " PCs · updated " + new Date().toLocaleTimeString() + " · refreshes every minute";
      render();
    }).catch(function (e) { $("status").textContent = "Couldn't load: " + e.message; $("status").className = "ro err"; });
  }
  $("find").addEventListener("input", render);
  $("back").addEventListener("click", function (e) { e.preventDefault(); location.hash = ""; });
  $("navPcs").addEventListener("click", function (e) { e.preventDefault(); location.hash = ""; });
  window.addEventListener("hashchange", route);
  load(); route(); setInterval(load, 60000);
})();
