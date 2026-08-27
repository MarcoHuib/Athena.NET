
const SITE = {
  repo: "MarcoHuib/Athena.NET",
  commit: "a7513e2defa2347d5ba20ff501436258d8503d04",
  branch: "feature/poring-live-wire",
  rawBase: "https://raw.githubusercontent.com/MarcoHuib/Athena.NET/a7513e2defa2347d5ba20ff501436258d8503d04/",
  blobBase: "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/"
};
SITE.docs = [{"path": "README.md", "title": "Project README", "group": "Project", "desc": "Project overview, current status, roadmap, architecture direction and public entry points.", "slug": "readme", "page": "docs/readme.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/README.md"}, {"path": "ONBOARDING.md", "title": "Onboarding", "group": "Project", "desc": "Repository onboarding and the fastest route into the Athena.NET codebase.", "slug": "onboarding", "page": "docs/onboarding.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ONBOARDING.md"}, {"path": "AGENTS.md", "title": "Agent instructions", "group": "Maintainer notes", "desc": "Repository-wide implementation guidance for coding agents and contributors.", "slug": "agents", "page": "docs/agents.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/AGENTS.md"}, {"path": "CLAUDE.md", "title": "Claude guidance", "group": "Maintainer notes", "desc": "Repository guidance used by Claude-based development workflows.", "slug": "claude", "page": "docs/claude.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/CLAUDE.md"}, {"path": "docs/installation.md", "title": "Installation", "group": "Getting started", "desc": "Prerequisites and installation steps for Athena.NET.", "slug": "docs-installation", "page": "docs/docs-installation.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/installation.md"}, {"path": "docs/configuration.md", "title": "Configuration", "group": "Getting started", "desc": "Configuration model and local settings.", "slug": "docs-configuration", "page": "docs/docs-configuration.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/configuration.md"}, {"path": "docs/run-locally.md", "title": "Run locally", "group": "Getting started", "desc": "Run the Athena.NET services on a development machine.", "slug": "docs-run-locally", "page": "docs/docs-run-locally.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/run-locally.md"}, {"path": "docs/aspire.md", "title": ".NET Aspire", "group": "Getting started", "desc": "Local orchestration using .NET Aspire.", "slug": "docs-aspire", "page": "docs/docs-aspire.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/aspire.md"}, {"path": "docs/docker-compose.md", "title": "Docker Compose", "group": "Getting started", "desc": "Container-based local startup with Docker Compose.", "slug": "docs-docker-compose", "page": "docs/docs-docker-compose.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/docker-compose.md"}, {"path": "docs/migrations.md", "title": "Database migrations", "group": "Getting started", "desc": "Database migration workflow.", "slug": "docs-migrations", "page": "docs/docs-migrations.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/migrations.md"}, {"path": "docs/sql-edge.md", "title": "SQL Edge / SQL development", "group": "Getting started", "desc": "Database notes for the local development stack.", "slug": "docs-sql-edge", "page": "docs/docs-sql-edge.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/sql-edge.md"}, {"path": "docs/production.md", "title": "Production", "group": "Getting started", "desc": "Production-oriented notes and current limitations.", "slug": "docs-production", "page": "docs/docs-production.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/production.md"}, {"path": "docs/architecture-roadmap.md", "title": "Architecture roadmap", "group": "Architecture", "desc": "The deliberately phased path from stock iRO TCP to proxy, QUIC, Identity and Orleans.", "slug": "docs-architecture-roadmap", "page": "docs/docs-architecture-roadmap.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/architecture-roadmap.md"}, {"path": "docs/client-gateway-architecture.md", "title": "Client & Gateway architecture", "group": "Architecture", "desc": "Athena.Client, Athena.Gateway, transport boundaries and the future QUIC edge.", "slug": "docs-client-gateway-architecture", "page": "docs/docs-client-gateway-architecture.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/client-gateway-architecture.md"}, {"path": "docs/orleans-game-engine-architecture.md", "title": "Orleans game engine", "group": "Architecture", "desc": "The later Microsoft Orleans distributed game-engine direction.", "slug": "docs-orleans-game-engine-architecture", "page": "docs/docs-orleans-game-engine-architecture.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/orleans-game-engine-architecture.md"}, {"path": "docs/game-content-map-lifecycle-architecture.md", "title": "Game content & map lifecycle", "group": "Architecture", "desc": "Content updates, map lifecycle, telemetry and one-map-one-runtime ownership.", "slug": "docs-game-content-map-lifecycle-architecture", "page": "docs/docs-game-content-map-lifecycle-architecture.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/game-content-map-lifecycle-architecture.md"}, {"path": "docs/checklists.md", "title": "Development checklists", "group": "Development", "desc": "Practical development checklists used by the project.", "slug": "docs-checklists", "page": "docs/docs-checklists.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/checklists.md"}, {"path": "docs/scripts.md", "title": "Helper scripts", "group": "Development", "desc": "Repository helper scripts and their intended use.", "slug": "docs-scripts", "page": "docs/docs-scripts.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/docs/scripts.md"}, {"path": "tools/WorldDataImporter/README.md", "title": "World Data Importer", "group": "Development", "desc": "World-data conversion pipeline, tooling and usage.", "slug": "tools-worlddataimporter-readme", "page": "docs/tools-worlddataimporter-readme.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/tools/WorldDataImporter/README.md"}, {"path": "data/world/README.md", "title": "World data", "group": "Development", "desc": "Generated and converted world-data artifacts.", "slug": "data-world-readme", "page": "docs/data-world-readme.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/data/world/README.md"}, {"path": "src/MapServer/Generated/README.md", "title": "Generated MapServer content", "group": "Development", "desc": "Rules for generated MapServer source and world content.", "slug": "src-mapserver-generated-readme", "page": "docs/src-mapserver-generated-readme.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/src/MapServer/Generated/README.md"}, {"path": "src/MapServer/Gameplay/Rules/PreRenewal/README.md", "title": "Pre-Renewal rules", "group": "Development", "desc": "Boundary notes for the Pre-Renewal gameplay-rules area.", "slug": "src-mapserver-gameplay-rules-prerenewal-readme", "page": "docs/src-mapserver-gameplay-rules-prerenewal-readme.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/src/MapServer/Gameplay/Rules/PreRenewal/README.md"}, {"path": "ai/README.md", "title": "Evidence index", "group": "Protocol evidence", "desc": "Index for developer-facing evidence and verified implementation notes.", "slug": "ai-readme", "page": "docs/ai-readme.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/README.md"}, {"path": "ai/iro-2026-wire.md", "title": "iRO 2026 wire reference", "group": "Protocol evidence", "desc": "Verified stock-iRO client-facing wire behavior and packet evidence.", "slug": "ai-iro-2026-wire", "page": "docs/ai-iro-2026-wire.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/iro-2026-wire.md"}, {"path": "ai/login-server.md", "title": "LoginServer notes", "group": "Protocol evidence", "desc": "LoginServer implementation and evidence notes.", "slug": "ai-login-server", "page": "docs/ai-login-server.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/login-server.md"}, {"path": "ai/loginserver-parity.md", "title": "LoginServer parity", "group": "Protocol evidence", "desc": "LoginServer parity tracking against the reference behavior.", "slug": "ai-loginserver-parity", "page": "docs/ai-loginserver-parity.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/loginserver-parity.md"}, {"path": "ai/char-server.md", "title": "CharServer notes", "group": "Protocol evidence", "desc": "CharServer implementation and evidence notes.", "slug": "ai-char-server", "page": "docs/ai-char-server.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/char-server.md"}, {"path": "ai/charserver-parity.md", "title": "CharServer parity", "group": "Protocol evidence", "desc": "CharServer parity tracking against the reference behavior.", "slug": "ai-charserver-parity", "page": "docs/ai-charserver-parity.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/charserver-parity.md"}, {"path": "ai/map-server.md", "title": "MapServer notes", "group": "Protocol evidence", "desc": "Detailed MapServer evidence and implementation notes.", "slug": "ai-map-server", "page": "docs/ai-map-server.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/map-server.md"}, {"path": "ai/mapserver-parity.md", "title": "MapServer parity", "group": "Protocol evidence", "desc": "MapServer parity tracking against the reference behavior.", "slug": "ai-mapserver-parity", "page": "docs/ai-mapserver-parity.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/mapserver-parity.md"}, {"path": "ai/world-data.md", "title": "World-data evidence", "group": "Protocol evidence", "desc": "World-data conversion, mapping and runtime evidence.", "slug": "ai-world-data", "page": "docs/ai-world-data.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/world-data.md"}, {"path": "ai/data-and-tools.md", "title": "Data & tools", "group": "Protocol evidence", "desc": "Data/tooling notes that support the evidence-driven implementation.", "slug": "ai-data-and-tools", "page": "docs/ai-data-and-tools.html", "source": "https://github.com/MarcoHuib/Athena.NET/blob/a7513e2defa2347d5ba20ff501436258d8503d04/ai/data-and-tools.md"}];
SITE.docMap = Object.fromEntries(SITE.docs.map(d => [d.path, d.page]));

function esc(s){return String(s).replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#039;"}[c]));}
function normalizePath(basePath, href){
  if (/^(https?:|mailto:|#)/i.test(href)) return href;
  const base = basePath.split("/"); base.pop();
  for(const part of href.split("/")){
    if(part==="."||part==="") continue;
    if(part==="..") base.pop(); else base.push(part);
  }
  return base.join("/");
}
function localHref(basePath, href){
  if (/^(https?:|mailto:|#)/i.test(href)) return href;
  const hash = href.includes("#") ? "#" + href.split("#").slice(1).join("#") : "";
  const rawPath = href.split("#")[0];
  const resolved = normalizePath(basePath, rawPath);
  if(SITE.docMap[resolved]) return "../" + SITE.docMap[resolved] + hash;
  return SITE.blobBase + resolved + hash;
}
function rawImageHref(basePath, href){
  if (/^https?:/i.test(href)) return href;
  return SITE.rawBase + normalizePath(basePath, href);
}
function inlineMd(text, basePath){
  let out = esc(text);
  out = out.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, (_,alt,url)=>`<img src="${esc(rawImageHref(basePath, url))}" alt="${alt}">`);
  out = out.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_,label,url)=>`<a href="${esc(localHref(basePath,url))}">${label}</a>`);
  out = out.replace(/`([^`]+)`/g,"<code>$1</code>");
  out = out.replace(/\*\*([^*]+)\*\*/g,"<strong>$1</strong>");
  out = out.replace(/__([^_]+)__/g,"<strong>$1</strong>");
  out = out.replace(/\*([^*\n]+)\*/g,"<em>$1</em>");
  return out;
}
function slug(s){return s.toLowerCase().replace(/<[^>]+>/g,"").replace(/[^a-z0-9]+/g,"-").replace(/^-|-$/g,"");}

function renderMarkdown(md, basePath){
  md = md.replace(/\r\n/g,"\n");
  const lines = md.split("\n");
  let html = "", inCode=false, codeLang="", code=[], inUl=false, inOl=false, inQuote=false;
  let tableBuf = [];
  const flushList=()=>{if(inUl){html+="</ul>";inUl=false}if(inOl){html+="</ol>";inOl=false}};
  const flushQuote=()=>{if(inQuote){html+="</blockquote>";inQuote=false}};
  const flushTable=()=>{
    if(tableBuf.length<2){ tableBuf.forEach(x=> html+=`<p>${inlineMd(x,basePath)}</p>`); tableBuf=[]; return; }
    const parseRow=r=>r.trim().replace(/^\||\|$/g,"").split("|").map(c=>c.trim());
    const rows=tableBuf.filter(Boolean); const heads=parseRow(rows[0]);
    html+="<table><thead><tr>"+heads.map(x=>`<th>${inlineMd(x,basePath)}</th>`).join("")+"</tr></thead><tbody>";
    rows.slice(2).forEach(r=>{html+="<tr>"+parseRow(r).map(x=>`<td>${inlineMd(x,basePath)}</td>`).join("")+"</tr>"});
    html+="</tbody></table>"; tableBuf=[];
  };

  for(let i=0;i<lines.length;i++){
    let line=lines[i];

    if(inCode){
      if(/^```/.test(line)){html+=`<pre><code class="language-${esc(codeLang)}">${esc(code.join("\n"))}</code></pre>`;inCode=false;code=[];continue}
      code.push(line);continue;
    }
    let m=line.match(/^```(.*)$/);
    if(m){flushList();flushQuote();flushTable();inCode=true;codeLang=m[1].trim();continue}

    if(line.includes("|") && i+1<lines.length && /^\s*\|?\s*:?-{3,}/.test(lines[i+1])){
      flushList();flushQuote(); tableBuf=[line,lines[++i]];
      while(i+1<lines.length && lines[i+1].includes("|") && lines[i+1].trim()!=="") tableBuf.push(lines[++i]);
      flushTable(); continue;
    }

    if(!line.trim()){flushList();flushQuote();flushTable();continue}
    if(/^---+$/.test(line.trim())){flushList();flushQuote();html+="<hr>";continue}

    m=line.match(/^(#{1,4})\s+(.*)$/);
    if(m){
      flushList();flushQuote();const level=m[1].length;const content=inlineMd(m[2],basePath);const id=slug(m[2]);
      html+=`<h${level} id="${id}">${content}</h${level}>`;continue
    }
    m=line.match(/^>\s?(.*)$/);
    if(m){flushList();if(!inQuote){html+="<blockquote>";inQuote=true}html+=`<p>${inlineMd(m[1],basePath)}</p>`;continue}
    m=line.match(/^\s*[-*+]\s+(.*)$/);
    if(m){flushQuote();if(!inUl){flushList();html+="<ul>";inUl=true}html+=`<li>${inlineMd(m[1],basePath)}</li>`;continue}
    m=line.match(/^\s*\d+\.\s+(.*)$/);
    if(m){flushQuote();if(!inOl){flushList();html+="<ol>";inOl=true}html+=`<li>${inlineMd(m[1],basePath)}</li>`;continue}

    flushList();flushQuote();
    html+=`<p>${inlineMd(line,basePath)}</p>`;
  }
  if(inCode) html+=`<pre><code>${esc(code.join("\n"))}</code></pre>`;
  flushList();flushQuote();flushTable();
  return html;
}

function buildSidebar(activePath){
  const host=document.querySelector("[data-doc-sidebar]"); if(!host)return;
  let html='<input class="side-search" type="search" placeholder="Filter docs…" aria-label="Filter documentation"><div class="side-nav">';
  let current="";
  SITE.docs.forEach(d=>{
    if(d.group!==current){current=d.group;html+=`<div class="side-group">${esc(current)}</div>`}
    html+=`<a class="side-link ${d.path===activePath?"active":""}" data-side-title="${esc((d.title+" "+d.path).toLowerCase())}" href="../${d.page}">${esc(d.title)}</a>`
  });
  html+="</div>";host.innerHTML=html;
  const input=host.querySelector("input");
  input.addEventListener("input",()=>{
    const q=input.value.trim().toLowerCase();
    host.querySelectorAll(".side-link").forEach(a=>a.style.display=!q||a.dataset.sideTitle.includes(q)?"block":"none");
  });
}
function buildToc(){
  const toc=document.querySelector("[data-toc]"); if(!toc)return;
  const heads=[...document.querySelectorAll(".markdown h1,.markdown h2,.markdown h3")].slice(0,18);
  if(!heads.length){toc.innerHTML="";return}
  toc.innerHTML="<h4>On this page</h4>"+heads.map(h=>`<a href="#${h.id}" style="padding-left:${h.tagName==="H3"?18:10}px">${esc(h.textContent)}</a>`).join("");
}
async function loadDocument(){
  const body=document.body;
  const path=body.dataset.docPath;if(!path)return;
  const doc=SITE.docs.find(d=>d.path===path);
  buildSidebar(path);
  const mount=document.querySelector("[data-markdown]");
  try{
    const r=await fetch(SITE.rawBase+path,{cache:"no-cache"});
    if(!r.ok)throw new Error(`HTTP ${r.status}`);
    const text=await r.text();
    mount.innerHTML=renderMarkdown(text,path);
    buildToc();
  }catch(err){
    mount.innerHTML=`<div class="error-box"><strong>Could not load this snapshot.</strong><br>Open the pinned source on GitHub instead.</div>`;
  }
  const src=document.querySelector("[data-source-link]");
  if(src)src.href=SITE.blobBase+path;
}
function initHubSearch(){
  const input=document.querySelector("[data-doc-search]");if(!input)return;
  input.addEventListener("input",()=>{
    const q=input.value.trim().toLowerCase();
    document.querySelectorAll("[data-search-card]").forEach(card=>{
      card.style.display=!q||card.dataset.searchCard.includes(q)?"block":"none";
    });
    document.querySelectorAll("[data-doc-group]").forEach(group=>{
      const visible=[...group.querySelectorAll("[data-search-card]")].some(c=>c.style.display!=="none");
      group.style.display=visible?"block":"none";
    })
  });
}
async function loadRepoStats(){
  const targets=document.querySelectorAll("[data-stars]");
  if(!targets.length)return;
  try{
    const r=await fetch("https://api.github.com/repos/MarcoHuib/Athena.NET");
    if(!r.ok)return; const data=await r.json();
    targets.forEach(el=>el.textContent=(data.stargazers_count??"—").toLocaleString());
    document.querySelectorAll("[data-forks]").forEach(el=>el.textContent=(data.forks_count??"—").toLocaleString());
  }catch{}
}
document.addEventListener("DOMContentLoaded",()=>{loadDocument();initHubSearch();loadRepoStats();});
