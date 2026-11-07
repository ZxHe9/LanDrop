"use strict";

const $ = (id) => document.getElementById(id);
const $$ = (selector, root) => Array.from((root || document).querySelectorAll(selector));

const state = {
  device: null,
  peers: [],
  local: { path: "", data: null, entries: [], selected: new Set(), filter: "", hidden: false, loading: false, error: "", reqId: 0 },
  remote: { peerId: "", path: "", data: null, entries: [], selected: new Set(), filter: "", hidden: false, loading: false, error: "", reqId: 0 },
  transfers: [],
  knownJobs: new Map(),
  dockExpanded: false,
  dockUserCollapsed: false,
  pollingTransfers: false,
  addDevice: null
};

/* ---------------- helpers ---------------- */
function esc(value) {
  return String(value == null ? "" : value).replace(/[&<>"']/g, (ch) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
  }[ch]));
}

function icon(name, extra) {
  return '<svg class="icon ' + (extra || "") + '"><use href="#i-' + name + '"></use></svg>';
}

function formatBytes(bytes) {
  const n = Number(bytes) || 0;
  if (n <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = n;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) { value /= 1024; i++; }
  let text;
  if (i === 0) text = String(Math.round(value));
  else if (value >= 100) text = value.toFixed(0);
  else if (value >= 10) text = value.toFixed(1);
  else text = value.toFixed(2);
  return text + " " + units[i];
}

function formatSpeed(bytes) {
  if (!bytes || bytes <= 0) return "";
  return formatBytes(bytes) + "/s";
}

function formatDate(iso) {
  if (!iso) return "";
  const date = new Date(iso);
  if (isNaN(date.getTime())) return "";
  const now = new Date();
  const pad = (v) => String(v).padStart(2, "0");
  if (date.getFullYear() === now.getFullYear()) {
    return pad(date.getMonth() + 1) + "-" + pad(date.getDate()) + " " + pad(date.getHours()) + ":" + pad(date.getMinutes());
  }
  return date.getFullYear() + "-" + pad(date.getMonth() + 1) + "-" + pad(date.getDate());
}

function formatFullDate(iso) {
  if (!iso) return "";
  const date = new Date(iso);
  if (isNaN(date.getTime())) return "";
  return date.toLocaleString("zh-CN", { hour12: false });
}

function fileKind(entry) {
  if (entry.drive) return "drive";
  if (entry.dir) return "dir";
  const ext = String(entry.ext || "").toLowerCase();
  if (["png", "jpg", "jpeg", "gif", "webp", "bmp", "svg", "ico", "heic"].includes(ext)) return "img";
  if (["mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v"].includes(ext)) return "video";
  if (["mp3", "wav", "flac", "aac", "m4a", "ogg", "wma"].includes(ext)) return "audio";
  if (["zip", "7z", "rar", "tar", "gz", "bz2", "xz", "iso"].includes(ext)) return "zip";
  if (["doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "txt", "md", "rtf", "csv"].includes(ext)) return "doc";
  if (["js", "ts", "json", "html", "css", "py", "java", "cs", "cpp", "c", "h", "xml", "yml", "yaml", "sql", "ps1", "bat"].includes(ext)) return "code";
  return "file";
}

function fileIcon(entry) {
  const kind = fileKind(entry);
  const name = kind === "dir" ? "folder" : kind === "drive" ? "drive" : "file";
  return icon(name);
}

function joinPath(base, name) {
  if (!base) return name;
  return /[\\/]$/.test(base) ? base + name : base + "\\" + name;
}

function rootOf(path) {
  const match = /^([A-Za-z]:\\)/.exec(path || "");
  return match ? match[1] : "";
}

function samePath(a, b) {
  return String(a || "").replace(/[\\/]+$/, "").toLowerCase() === String(b || "").replace(/[\\/]+$/, "").toLowerCase();
}

function debounce(fn, wait) {
  let timer = null;
  return function () {
    const args = arguments;
    clearTimeout(timer);
    timer = setTimeout(() => fn.apply(null, args), wait);
  };
}

async function api(path, options) {
  const opts = Object.assign({ cache: "no-store" }, options || {});
  const response = await fetch(path, opts);
  let data = {};
  const text = await response.text();
  if (text) {
    try { data = JSON.parse(text); } catch (e) { data = {}; }
  }
  if (!response.ok || data.ok === false) {
    const error = new Error(data.error || ("请求失败（HTTP " + response.status + "）"));
    error.status = response.status;
    throw error;
  }
  return data;
}

function getJson(path) { return api(path); }
function postJson(path, body) {
  return api(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body || {})
  });
}

async function copyText(text, message) {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
    } else {
      const area = document.createElement("textarea");
      area.className = "copy-helper";
      area.value = text;
      document.body.appendChild(area);
      area.select();
      document.execCommand("copy");
      area.remove();
    }
    toast(message || "已复制", text, "success");
  } catch (e) {
    toast("复制失败", "请手动选择文本复制", "error");
  }
}

/* ---------------- toast ---------------- */
function toast(title, message, type) {
  const wrap = $("toastWrap");
  if (!wrap) return;
  const el = document.createElement("div");
  el.className = "toast" + (type === "error" ? " error" : "");
  el.innerHTML = icon(type === "error" ? "alert" : "check-circle") +
    '<div class="toast-body"><strong>' + esc(title) + "</strong>" +
    (message ? "<span>" + esc(message) + "</span>" : "") + "</div>";
  wrap.appendChild(el);
  while (wrap.children.length > 4) wrap.removeChild(wrap.firstElementChild);
  const close = () => {
    el.classList.add("out");
    setTimeout(() => { try { el.remove(); } catch (e) { } }, 220);
  };
  el.addEventListener("click", close);
  setTimeout(close, type === "error" ? 6500 : 4200);
}

/* ---------------- boot ---------------- */
async function boot() {
  bindEvents();
  renderDock();
  try {
    const data = await getJson("/api/state");
    state.device = data.device;
    state.peers = data.peers || [];
    renderHeader();
    renderPeerSelect();
    renderNetStatus();

    let path = data.localPath;
    const savedPath = localStorage.getItem("landrop.localPath");
    if (savedPath) path = savedPath;
    await loadLocal(path || "");

    const savedPeer = localStorage.getItem("landrop.peerId");
    let peer = state.peers.find((item) => item.id === savedPeer && item.paired);
    if (!peer) peer = state.peers.find((item) => item.paired && item.online) || state.peers.find((item) => item.paired);
    if (peer) {
      await selectPeer(peer.id);
    } else {
      showRemoteEmpty();
    }

    await pollTransfers();
    setInterval(() => { pollPeers().catch(() => { }); }, 6000);
    setInterval(() => { pollTransfers().catch(() => { }); }, 900);
  } catch (error) {
    toast("无法连接 LanDrop 服务", error.message || "请重新打开程序", "error");
  }
}

function renderHeader() {
  if (!state.device) return;
  $("codeText").textContent = state.device.code || "----";
  $("deviceName").textContent = state.device.name || "本机";
  document.title = (state.device.name || "LanDrop") + " · LanDrop";
  let address = "未检测到局域网地址";
  if (state.device.addresses && state.device.addresses.length > 0) {
    try {
      const parsed = new URL(state.device.addresses[0]);
      address = parsed.host;
    } catch (e) {
      address = state.device.addresses[0];
    }
  }
  $("localAddress").textContent = address;
}

function renderNetStatus() {
  const online = state.peers.filter((peer) => peer.paired && peer.online).length;
  const newDevices = state.peers.filter((peer) => !peer.paired).length;
  const el = $("netStatus");
  if (online > 0) {
    $("netStatusText").textContent = online + " 台设备在线";
    el.classList.remove("offline");
  } else if (newDevices > 0) {
    $("netStatusText").textContent = "发现新设备";
    el.classList.remove("offline");
  } else {
    $("netStatusText").textContent = "等待设备";
    el.classList.add("offline");
  }
}

/* ---------------- peers ---------------- */
function getPeer(id) {
  return state.peers.find((peer) => peer.id === id) || null;
}

function pairedPeers() {
  return state.peers.filter((peer) => peer.paired);
}

function renderPeerSelect() {
  const select = $("peerSelect");
  const current = state.remote.peerId;
  let html = '<option value="">选择设备</option>';
  pairedPeers().forEach((peer) => {
    html += '<option value="' + esc(peer.id) + '">' + esc(peer.name) + (peer.online ? "" : "（离线）") + "</option>";
  });
  select.innerHTML = html;
  if (current && getPeer(current)) {
    select.value = current;
  } else if (current) {
    state.remote.peerId = "";
    select.value = "";
  }
}

function renderRemoteTag() {
  const peer = getPeer(state.remote.peerId);
  if (!peer) return;
  $("remoteTag").textContent = peer.name + " · " + (peer.online ? "已连接" : "当前离线");
  const alert = $("remoteAlert");
  if (!peer.online) {
    alert.textContent = "设备当前离线。请确认对方电脑已打开 LanDrop，并且两台电脑在同一个 Wi-Fi。";
    alert.hidden = false;
  } else if (state.remote.error) {
    alert.textContent = state.remote.error;
    alert.hidden = false;
  } else {
    alert.hidden = true;
  }
}

async function pollPeers() {
  try {
    const data = await getJson("/api/peers");
    state.peers = data.peers || [];
    renderPeerSelect();
    renderNetStatus();
    const peer = getPeer(state.remote.peerId);
    if (peer) {
      renderRemoteTag();
      if (peer.online && state.remote.error) {
        await loadRemote(state.remote.path, { silent: true });
      }
    } else if (state.remote.peerId) {
      showRemoteEmpty();
    }
  } catch (e) { }
}

async function refreshDevices(showToast) {
  const button = $("btnRefreshDevices");
  button.classList.add("spinning");
  try {
    const data = await getJson("/api/discover");
    state.peers = data.peers || [];
    renderPeerSelect();
    renderNetStatus();
    if (showToast) {
      const count = state.peers.filter((peer) => !peer.paired).length;
      if (count > 0) toast("扫描完成", "发现 " + count + " 台新设备", "success");
      else toast("扫描完成", "没有发现新设备，可尝试手动输入 IP", "success");
    }
  } catch (e) {
    if (showToast) toast("扫描失败", e.message, "error");
  } finally {
    button.classList.remove("spinning");
  }
}

async function selectPeer(id, options) {
  const opts = options || {};
  const peer = getPeer(id);
  if (!peer) {
    showRemoteEmpty();
    return;
  }
  const changed = state.remote.peerId !== peer.id;
  state.remote.peerId = peer.id;
  localStorage.setItem("landrop.peerId", peer.id);
  $("peerSelect").value = peer.id;
  $("remoteEmpty").hidden = true;
  $("remoteBody").hidden = false;
  renderRemoteTag();
  if (changed) {
    state.remote.path = "";
    state.remote.data = null;
    state.remote.entries = [];
    state.remote.selected.clear();
    state.remote.filter = "";
    $("remoteSearch").value = "";
  }
  let path = opts.path;
  if (path === undefined) {
    path = localStorage.getItem("landrop.remotePath." + peer.id) || peer.home || "";
  }
  await loadRemote(path || "", { initial: true });
}

function showRemoteEmpty() {
  state.remote.peerId = "";
  state.remote.path = "";
  state.remote.data = null;
  state.remote.entries = [];
  state.remote.selected.clear();
  state.remote.error = "";
  localStorage.removeItem("landrop.peerId");
  $("peerSelect").value = "";
  $("remoteEmpty").hidden = false;
  $("remoteBody").hidden = true;
  $("remoteTag").textContent = "未连接";
  $("remoteAlert").hidden = true;
}

/* ---------------- local ---------------- */
async function loadLocal(path, options) {
  const opts = options || {};
  const requestId = ++state.local.reqId;
  state.local.loading = true;
  state.local.error = "";
  if (!opts.silent) renderEntries("local");
  try {
    const data = await getJson("/api/fs/list?path=" + encodeURIComponent(path || "") + "&hidden=" + (state.local.hidden ? "1" : "0"));
    if (requestId !== state.local.reqId) return;
    state.local.path = data.path || "";
    state.local.data = data;
    state.local.entries = data.entries || [];
    state.local.selected.clear();
    state.local.filter = "";
    state.local.loading = false;
    $("localSearch").value = "";
    localStorage.setItem("landrop.localPath", state.local.path);
    renderLocalPane();
    const list = $("localList");
    list.scrollTop = 0;
  } catch (error) {
    if (requestId !== state.local.reqId) return;
    state.local.loading = false;
    state.local.error = error.message;
    if (path && !opts.noFallback) {
      toast("无法打开此文件夹", error.message, "error");
      await loadLocal((state.device && state.device.home) || "", { noFallback: true });
      return;
    }
    renderEntries("local");
    updateSelectionUI("local");
  }
}

function renderLocalPane() {
  const data = state.local.data;
  $("localTag").textContent = data ? (data.path ? data.name : "此电脑") : "这台电脑";
  renderDriveSelect("local", data ? data.drives : []);
  renderCrumb("local");
  renderEntries("local");
  updateSelectionUI("local");
  $("localHidden").checked = state.local.hidden;
}

/* ---------------- remote ---------------- */
async function loadRemote(path, options) {
  const opts = options || {};
  const peer = getPeer(state.remote.peerId);
  if (!peer) { showRemoteEmpty(); return; }
  const requestId = ++state.remote.reqId;
  state.remote.loading = true;
  state.remote.error = "";
  if (!opts.silent) renderEntries("remote");
  renderRemoteTag();
  try {
    const data = await postJson("/api/remote/list", {
      peerId: peer.id,
      path: path || "",
      hidden: state.remote.hidden
    });
    if (requestId !== state.remote.reqId) return;
    state.remote.path = data.path || "";
    state.remote.data = data;
    state.remote.entries = data.entries || [];
    state.remote.selected.clear();
    state.remote.filter = "";
    state.remote.loading = false;
    $("remoteSearch").value = "";
    localStorage.setItem("landrop.remotePath." + peer.id, state.remote.path);
    renderRemotePane();
    $("remoteList").scrollTop = 0;
  } catch (error) {
    if (requestId !== state.remote.reqId) return;
    state.remote.loading = false;
    state.remote.error = error.message;
    if (path && !opts.noFallback) {
      toast("无法打开对方文件夹", error.message, "error");
      await loadRemote("", { noFallback: true });
      return;
    }
    renderEntries("remote");
    updateSelectionUI("remote");
    renderRemoteTag();
  }
}

function renderRemotePane() {
  const data = state.remote.data;
  $("remoteTag").textContent = data ? ((getPeer(state.remote.peerId) || {}).name || "对方电脑") + " · 已连接" : "已连接";
  renderDriveSelect("remote", data ? data.drives : []);
  renderCrumb("remote");
  renderEntries("remote");
  updateSelectionUI("remote");
  $("remoteHidden").checked = state.remote.hidden;
  renderRemoteTag();
}

/* ---------------- path / breadcrumb ---------------- */
function renderDriveSelect(side, drives) {
  const select = $(side + "Drive");
  const currentRoot = rootOf(state[side].path || "");
  let html = '<option value="">磁盘</option>';
  (drives || []).forEach((drive) => {
    const label = drive.label ? " · " + drive.label : "";
    html += '<option value="' + esc(drive.name || drive.path) + '">' + esc((drive.name || drive.path) + label) + "</option>";
  });
  select.innerHTML = html;
  select.value = currentRoot || "";
}

function renderCrumb(side) {
  const nav = $(side + "Crumb");
  const path = state[side].path || "";
  const parts = [];
  if (!path) {
    parts.push({ label: "此电脑", path: "", current: true });
  } else {
    parts.push({ label: "此电脑", path: "", current: false });
    const isUnc = path.startsWith("\\\\");
    let segments = path.replace(/\\+$/, "").split("\\").filter(Boolean);
    if (isUnc) {
      segments = segments.slice(1);
    }
    let acc = "";
    segments.forEach((segment, index) => {
      if (index === 0 && /^[A-Za-z]:$/.test(segment)) acc = segment + "\\";
      else if (isUnc && index === 0) acc = "\\\\" + segment;
      else acc = acc ? (/[\\/]$/.test(acc) ? acc + segment : acc + "\\" + segment) : segment;
      parts.push({
        label: segment === "Users" ? "用户" : segment,
        path: acc,
        current: index === segments.length - 1
      });
    });
  }
  nav.innerHTML = parts.map((part, index) =>
    (index > 0 ? '<span class="crumb-sep">›</span>' : "") +
    '<button class="crumb' + (part.current ? " current" : "") + '" data-path="' + esc(part.path) + '" title="' + esc(part.path || "此电脑") + '">' + esc(part.label) + "</button>"
  ).join("");
  nav.scrollLeft = nav.scrollWidth;
}

/* ---------------- file lists ---------------- */
function visibleEntries(side) {
  const query = (state[side].filter || "").trim().toLowerCase();
  if (!query) return state[side].entries;
  return state[side].entries.filter((entry) => String(entry.name || "").toLowerCase().includes(query));
}

function renderEntries(side) {
  const list = $(side + "List");
  const data = state[side].data;
  if (state[side].loading) {
    list.innerHTML = '<div class="loading-hint"><span class="spinner"></span> 正在读取文件夹…</div>';
    return;
  }
  if (state[side].error) {
    list.innerHTML = '<div class="empty-row">' + icon("alert") + "<strong>无法读取文件夹</strong><span>" +
      esc(state[side].error) + '</span><button class="link-btn" data-retry="' + side + '">重试</button></div>';
    return;
  }
  if (!data) {
    list.innerHTML = '<div class="empty-row">' + icon("folder-open") + "<strong>还没有打开文件夹</strong><span>从左侧磁盘开始浏览。</span></div>";
    return;
  }

  const entries = visibleEntries(side);
  const selected = state[side].selected;
  let html = "";

  if (data.parent !== null && data.parent !== undefined) {
    html += '<div class="file-row parent-row" data-parent="1" data-parent-path="' + esc(data.parent) + '">' +
      '<span class="file-cell-check"></span>' +
      '<div class="file-name"><span class="file-icon">' + icon("arrow-left") + '</span><span class="name-text">..</span></div>' +
      '<span class="file-size"></span><span class="file-date"></span></div>';
  }

  entries.forEach((entry) => {
    const kind = fileKind(entry);
    const path = entry.path || entry.name;
    const isSelected = selected.has(path);
    const size = entry.drive ? (formatBytes(entry.free) + " 可用") : (entry.dir ? "—" : formatBytes(entry.size));
    const date = entry.drive ? "" : formatDate(entry.modified);
    const nameExtra = entry.drive && entry.label ? '<span class="name-meta">' + esc(entry.label) + "</span>" : "";
    html += '<div class="file-row' + (isSelected ? " selected" : "") + '" data-path="' + esc(path) + '" data-dir="' + (entry.dir ? "1" : "0") + '">' +
      '<span class="file-cell-check"><input type="checkbox" aria-label="选择 ' + esc(entry.name) + '"' + (isSelected ? " checked" : "") + '></span>' +
      '<div class="file-name"><span class="file-icon ' + kind + '">' + fileIcon(entry) + '</span>' +
      '<span class="name-text' + (entry.dir ? " folder-name" : "") + '" title="' + esc(entry.name) + '">' + esc(entry.name) + "</span>" + nameExtra + "</div>" +
      '<span class="file-size">' + esc(size) + '</span>' +
      '<span class="file-date" title="' + esc(formatFullDate(entry.modified)) + '">' + esc(date) + "</span></div>";
  });

  if (entries.length === 0) {
    if ((state[side].filter || "").trim()) {
      html += '<div class="empty-row">' + icon("search") + "<strong>没有匹配的项目</strong><span>换个关键词试试。</span></div>";
    } else if (!data.parent) {
      html += '<div class="empty-row">' + icon("drive") + "<strong>没有可用的磁盘</strong><span>请检查磁盘是否已连接。</span></div>";
    } else {
      html += '<div class="empty-row">' + icon("folder") + "<strong>这个文件夹是空的</strong><span>可以新建文件夹，或从对面拖入文件。</span></div>";
    }
  }

  list.innerHTML = html;
  updateSelectAll(side);
}

function updateSelectAll(side) {
  const entries = visibleEntries(side);
  const selected = state[side].selected;
  const all = $(side + "All");
  if (!all) return;
  const count = entries.filter((entry) => selected.has(entry.path)).length;
  all.checked = entries.length > 0 && count === entries.length;
  all.indeterminate = count > 0 && count < entries.length;
}

function updateSelectionUI(side) {
  const selected = state[side].selected;
  const entries = state[side].entries.filter((entry) => selected.has(entry.path));
  let bytes = 0;
  entries.forEach((entry) => { if (!entry.dir && !entry.drive) bytes += Number(entry.size) || 0; });
  const label = entries.length === 0
    ? "未选择项目"
    : "已选 " + entries.length + " 项" + (bytes > 0 ? " · " + formatBytes(bytes) : "");

  const hasDriveRoot = entries.some((entry) => entry.drive || /^[A-Za-z]:\\$/.test(entry.path));
  if (side === "local") {
    $("localSel").textContent = label;
    $("btnLocalRename").disabled = entries.length !== 1;
    $("btnLocalDelete").disabled = entries.length === 0;
    const canPush = entries.length > 0 && !hasDriveRoot && !!getPeer(state.remote.peerId) && !!state.remote.path && !state.local.loading;
    $("btnPush").disabled = !canPush;
  } else {
    $("remoteSel").textContent = label;
    $("btnRemoteRename").disabled = entries.length !== 1;
    $("btnRemoteDelete").disabled = entries.length === 0;
    const canPull = entries.length > 0 && !hasDriveRoot && !!state.local.path && !state.remote.loading;
    $("btnPull").disabled = !canPull;
  }

  const list = $(side + "List");
  $$(".file-row[data-path]", list).forEach((row) => {
    const on = selected.has(row.dataset.path);
    row.classList.toggle("selected", on);
    const box = row.querySelector('input[type="checkbox"]');
    if (box) box.checked = on;
  });
  updateSelectAll(side);
}

function setSelected(side, path, value) {
  if (value) state[side].selected.add(path);
  else state[side].selected.delete(path);
}

function selectOnly(side, path) {
  state[side].selected.clear();
  state[side].selected.add(path);
}

function toggleSelected(side, path) {
  if (state[side].selected.has(path)) state[side].selected.delete(path);
  else state[side].selected.add(path);
}

function onFileClick(event, side) {
  const retry = event.target.closest("[data-retry]");
  if (retry) {
    if (side === "local") loadLocal(state.local.path);
    else loadRemote(state.remote.path);
    return;
  }
  const row = event.target.closest(".file-row");
  if (!row || row.dataset.parent === "1") return;
  if (event.target.closest('input[type="checkbox"]')) return;
  if (event.ctrlKey || event.metaKey) toggleSelected(side, row.dataset.path);
  else selectOnly(side, row.dataset.path);
  updateSelectionUI(side);
}

function onFileCheck(event, side) {
  const box = event.target.closest('input[type="checkbox"]');
  if (!box) return;
  const row = box.closest(".file-row[data-path]");
  if (!row) return;
  setSelected(side, row.dataset.path, box.checked);
  row.classList.toggle("selected", box.checked);
  updateSelectionUI(side);
}

async function onFileDoubleClick(event, side) {
  const row = event.target.closest(".file-row");
  if (!row) return;
  if (row.dataset.parent === "1") {
    await navigate(side, row.dataset.parentPath || "");
    return;
  }
  const path = row.dataset.path;
  const isDir = row.dataset.dir === "1";
  if (isDir) {
    await navigate(side, path);
    return;
  }
  if (side === "local") {
    try {
      await postJson("/api/open", { path: path });
    } catch (error) {
      toast("无法打开文件", error.message, "error");
    }
  } else {
    await startTransferPaths("pull", [path]);
  }
}

async function navigate(side, path, options) {
  if (side === "local") await loadLocal(path, options);
  else await loadRemote(path, options);
}

/* ---------------- file operations ---------------- */
function openMkdir(side) {
  const base = state[side].path;
  if (!base) { toast("请先进入一个磁盘", "在“此电脑”中双击磁盘后再新建文件夹", "error"); return; }
  const title = side === "local" ? "在本机新建文件夹" : "在对方电脑新建文件夹";
  showModal('<div class="modal">' +
    '<div class="modal-head"><div><h3>' + title + '</h3><p>' + esc(base) + '</p></div>' +
    '<button class="modal-close" data-close aria-label="关闭">' + icon("x") + '</button></div>' +
    '<div class="modal-body"><div class="field"><label>文件夹名称</label>' +
    '<input class="input" id="mkdirName" maxlength="120" placeholder="例如：项目资料" autofocus></div>' +
    '<div class="modal-error" id="modalError" hidden></div></div>' +
    '<div class="modal-foot"><button class="btn ghost" data-close>取消</button>' +
    '<button class="btn primary" id="mkdirSubmit">创建</button></div></div>');

  const submit = async () => {
    const name = $("mkdirName").value.trim();
    if (!name) { $("mkdirName").focus(); return; }
    const button = $("mkdirSubmit");
    button.disabled = true;
    button.textContent = "创建中…";
    try {
      const target = joinPath(base, name);
      if (side === "local") {
        await postJson("/api/fs/mkdir", { path: target });
      } else {
        await postJson("/api/remote/mkdir", { peerId: state.remote.peerId, path: target });
      }
      closeModal();
      toast("文件夹已创建", name, "success");
      await navigate(side, base);
    } catch (error) {
      showModalError(error.message);
      button.disabled = false;
      button.textContent = "创建";
    }
  };
  $("mkdirSubmit").addEventListener("click", submit);
  $("mkdirName").addEventListener("keydown", (event) => { if (event.key === "Enter") submit(); });
  setTimeout(() => { const el = $("mkdirName"); if (el) el.focus(); }, 30);
}

function openRename(side) {
  const entries = state[side].entries.filter((entry) => state[side].selected.has(entry.path));
  if (entries.length !== 1) { toast("请选择一个项目", "重命名一次只能处理一个文件或文件夹", "error"); return; }
  const entry = entries[0];
  const title = side === "local" ? "重命名" : "重命名对方电脑上的项目";
  showModal('<div class="modal">' +
    '<div class="modal-head"><div><h3>' + title + '</h3><p>' + esc(entry.path) + '</p></div>' +
    '<button class="modal-close" data-close aria-label="关闭">' + icon("x") + '</button></div>' +
    '<div class="modal-body"><div class="field"><label>新名称</label>' +
    '<input class="input" id="renameName" maxlength="200" value="' + esc(entry.name) + '" autofocus></div>' +
    '<div class="modal-error" id="modalError" hidden></div></div>' +
    '<div class="modal-foot"><button class="btn ghost" data-close>取消</button>' +
    '<button class="btn primary" id="renameSubmit">保存</button></div></div>');
  const input = $("renameName");
  setTimeout(() => { if (input) { input.focus(); input.select(); } }, 30);
  const submit = async () => {
    const name = input.value.trim();
    if (!name) { input.focus(); return; }
    const button = $("renameSubmit");
    button.disabled = true;
    try {
      if (side === "local") {
        await postJson("/api/fs/rename", { path: entry.path, newName: name });
      } else {
        await postJson("/api/remote/rename", { peerId: state.remote.peerId, path: entry.path, newName: name });
      }
      closeModal();
      toast("已重命名", name, "success");
      await navigate(side, state[side].path);
    } catch (error) {
      showModalError(error.message);
      button.disabled = false;
    }
  };
  $("renameSubmit").addEventListener("click", submit);
  input.addEventListener("keydown", (event) => { if (event.key === "Enter") submit(); });
}

function openDelete(side) {
  const entries = state[side].entries.filter((entry) => state[side].selected.has(entry.path));
  if (entries.length === 0) return;
  const targetName = side === "local" ? "本机" : "对方电脑";
  const listHtml = entries.slice(0, 8).map((entry) => esc(entry.name)).join("<br>") +
    (entries.length > 8 ? "<br>… 还有 " + (entries.length - 8) + " 项" : "");
  showModal('<div class="modal">' +
    '<div class="modal-head"><div><h3>删除 ' + entries.length + ' 个项目？</h3><p>操作将在' + targetName + '上执行</p></div>' +
    '<button class="modal-close" data-close aria-label="关闭">' + icon("x") + '</button></div>' +
    '<div class="modal-body"><div class="confirm-text">以下项目会被<strong>彻底删除，不会进入回收站</strong>：</div>' +
    '<div class="confirm-list">' + listHtml + '</div>' +
    '<div class="modal-error" id="modalError" hidden></div></div>' +
    '<div class="modal-foot"><button class="btn ghost" data-close>取消</button>' +
    '<button class="btn primary danger-btn" id="deleteSubmit">确认删除</button></div></div>');
  const button = $("deleteSubmit");
  button.addEventListener("click", async () => {
    button.disabled = true;
    button.textContent = "删除中…";
    try {
      const paths = entries.map((entry) => entry.path);
      let result;
      if (side === "local") result = await postJson("/api/fs/delete", { paths: paths });
      else result = await postJson("/api/remote/delete", { peerId: state.remote.peerId, paths: paths });
      closeModal();
      const errors = (result && result.errors) || [];
      if (errors.length > 0) toast("部分项目未删除", errors.slice(0, 2).join("；"), "error");
      else toast("已删除", entries.length + " 个项目", "success");
      await navigate(side, state[side].path);
    } catch (error) {
      showModalError(error.message);
      button.disabled = false;
      button.textContent = "确认删除";
    }
  });
}

function openPathDialog(side) {
  const title = side === "local" ? "转到本机文件夹" : "转到对方文件夹";
  showModal('<div class="modal">' +
    '<div class="modal-head"><div><h3>' + title + '</h3><p>输入完整路径，留空表示“此电脑”</p></div>' +
    '<button class="modal-close" data-close aria-label="关闭">' + icon("x") + '</button></div>' +
    '<div class="modal-body"><div class="field"><label>路径</label>' +
    '<input class="input mono input-path" id="gotoPath" value="' + esc(state[side].path || "") + '" placeholder="C:\\Users\\你的名字\\Desktop" autofocus></div>' +
    '<div class="modal-error" id="modalError" hidden></div></div>' +
    '<div class="modal-foot"><button class="btn ghost" data-close>取消</button>' +
    '<button class="btn primary" id="gotoSubmit">前往</button></div></div>');
  const input = $("gotoPath");
  setTimeout(() => { if (input) { input.focus(); input.select(); } }, 30);
  const submit = async () => {
    const value = input.value.trim();
    closeModal();
    await navigate(side, value);
  };
  $("gotoSubmit").addEventListener("click", submit);
  input.addEventListener("keydown", (event) => { if (event.key === "Enter") submit(); });
}

/* ---------------- transfers ---------------- */
async function startTransfer(direction) {
  const side = direction === "push" ? "local" : "remote";
  const selected = state[side].entries.filter((entry) => state[side].selected.has(entry.path)).map((entry) => entry.path);
  return startTransferPaths(direction, selected);
}

async function startTransferPaths(direction, paths) {
  const peer = getPeer(state.remote.peerId);
  if (!peer) { toast("请先添加对方电脑", "点击右侧的“添加设备”", "error"); return; }
  if (!paths || paths.length === 0) { toast("请先选择文件", "勾选或点击文件后再传输", "error"); return; }
  if (paths.some((path) => /^[A-Za-z]:\\$/.test(path))) {
    toast("不能直接传输整个磁盘", "请进入磁盘后选择具体的文件或文件夹", "error");
    return;
  }
  const target = direction === "push" ? state.remote.path : state.local.path;
  if (!target) { toast("目标文件夹未打开", "请先在目标一侧进入一个文件夹", "error"); return; }
  try {
    const result = await postJson("/api/transfer/" + direction, {
      peerId: peer.id,
      paths: paths,
      target: target,
      overwrite: false
    });
    const side = direction === "push" ? "local" : "remote";
    state[side].selected.clear();
    updateSelectionUI(side);
    state.dockUserCollapsed = false;
    state.dockExpanded = true;
    renderDock();
    toast("已加入传输队列", (result.count || paths.length) + " 个项目", "success");
    await pollTransfers();
  } catch (error) {
    toast("无法开始传输", error.message, "error");
  }
}

async function pollTransfers() {
  if (state.pollingTransfers) return;
  state.pollingTransfers = true;
  try {
    const data = await getJson("/api/transfers");
    const jobs = data.jobs || [];
    const nextKnown = new Map();
    let newActive = false;
    jobs.forEach((job) => {
      const previous = state.knownJobs.get(job.id);
      nextKnown.set(job.id, job.status);
      if (!previous && (job.status === "queued" || job.status === "preparing" || job.status === "running")) newActive = true;
      if (previous && previous !== job.status) {
        if (job.status === "done") {
          toast("传输完成", job.title + (job.kind === "push" ? " 已传到对方电脑" : " 已下载到本机"), "success");
          scheduleTransferRefresh(job);
        } else if (job.status === "error") {
          toast("传输失败", job.title + "：" + (job.message || "未知错误"), "error");
        }
      }
    });
    state.knownJobs = nextKnown;
    state.transfers = jobs;
    if (newActive) {
      state.dockUserCollapsed = false;
      state.dockExpanded = true;
    } else if (jobs.some((job) => job.status === "running" || job.status === "preparing" || job.status === "queued") && !state.dockUserCollapsed) {
      state.dockExpanded = true;
    }
    renderDock();
  } catch (error) {
    // 服务暂时不可达时静默，下一轮继续
  } finally {
    state.pollingTransfers = false;
  }
}

let transferRefreshTimer = null;
function scheduleTransferRefresh(job) {
  clearTimeout(transferRefreshTimer);
  transferRefreshTimer = setTimeout(() => {
    if (job.kind === "pull" && samePath(job.target, state.local.path)) {
      loadLocal(state.local.path, { silent: true }).catch(() => { });
    }
    if (job.kind === "push" && samePath(job.target, state.remote.path)) {
      loadRemote(state.remote.path, { silent: true }).catch(() => { });
    }
  }, 400);
}

const STATUS_LABEL = { queued: "等待中", preparing: "准备中", running: "传输中", done: "完成", error: "失败", canceled: "已取消" };

function renderDock() {
  const dock = $("transferDock");
  const jobs = state.transfers.slice().sort((a, b) => {
    const rank = (job) => (job.status === "queued" || job.status === "preparing" || job.status === "running") ? 0 : 1;
    const diff = rank(a) - rank(b);
    if (diff !== 0) return diff;
    return b.id - a.id;
  });
  const active = jobs.filter((job) => job.status === "queued" || job.status === "preparing" || job.status === "running").length;
  const finished = jobs.filter((job) => job.status === "done" || job.status === "error" || job.status === "canceled").length;
  $("dockSummary").textContent = active > 0 ? (active + " 个进行中") : (jobs.length > 0 ? jobs.length + " 个任务" : "暂无任务");
  $("btnClearDone").hidden = finished === 0;
  dock.classList.toggle("expanded", state.dockExpanded);
  $("btnDockToggle").innerHTML = icon(state.dockExpanded ? "chevron-down" : "chevron-up");

  if (jobs.length === 0) {
    $("dockList").innerHTML = '<div class="dock-empty">传到对面或下载到本机时，这里会显示实时进度</div>';
    return;
  }

  $("dockList").innerHTML = jobs.map((job) => {
    const activeJob = job.status === "queued" || job.status === "preparing" || job.status === "running";
    const pct = Math.max(0, Math.min(100, Math.round((Number(job.progress) || 0) * 100)));
    const jobClass = "job " + job.status;
    const stateText = job.status === "running"
      ? '<span class="pct">' + pct + '%</span><span class="speed">' + esc(formatSpeed(job.speed)) + "</span>"
      : job.status === "done" ? '<span class="pct">100%</span><span class="speed">完成</span>'
        : '<span class="pct">' + (pct > 0 ? pct + "%" : "") + '</span><span class="speed">' + esc(STATUS_LABEL[job.status] || job.status) + "</span>";
    let sub = "";
    if (job.status === "running") {
      sub = formatBytes(job.done) + (job.total ? " / " + formatBytes(job.total) : "") + (job.current ? " · " + job.current : "");
    } else if (job.status === "error") {
      sub = job.message || "传输失败";
    } else if (job.status === "done") {
      sub = formatBytes(job.total || job.done) + " · " + (job.kind === "push" ? "已传到对方" : "已下载到本机");
    } else if (job.status === "queued") {
      sub = "等待开始";
    } else if (job.status === "preparing") {
      sub = "正在读取文件列表…";
    } else {
      sub = "已取消";
    }
    return '<div class="' + jobClass + '">' +
      '<span class="job-icon ' + (job.kind === "push" ? "push" : "") + '">' + icon(job.kind === "push" ? "upload" : "download") + "</span>" +
      '<div class="job-main"><div class="job-title-row"><span class="job-name" title="' + esc(job.title) + '">' + esc(job.title) + "</span>" +
      '<span class="job-peer">' + (job.kind === "push" ? "→ " : "← ") + esc(job.peerName || "") + "</span></div>" +
      '<div class="job-meta"><progress class="progress-bar" value="' + pct + '" max="100"></progress>' +
      '<span class="job-state">' + stateText + "</span></div>" +
      '<div class="job-sub" title="' + esc(sub) + '">' + esc(sub) + "</div></div>" +
      (activeJob ? '<button class="job-cancel" data-cancel="' + job.id + '" title="取消">' + icon("x") + "</button>" : "<span></span>") +
      "</div>";
  }).join("");
}

/* ---------------- modal ---------------- */
function showModal(html) {
  const layer = $("modalLayer");
  layer.innerHTML = html;
  layer.hidden = false;
  layer.setAttribute("aria-hidden", "false");
  $$("[data-close]", layer).forEach((button) => button.addEventListener("click", closeModal));
  layer.onclick = (event) => { if (event.target === layer) closeModal(); };
  setTimeout(() => {
    const focus = layer.querySelector("[autofocus]");
    if (focus) focus.focus();
  }, 40);
}

function closeModal() {
  const layer = $("modalLayer");
  layer.hidden = true;
  layer.setAttribute("aria-hidden", "true");
  layer.innerHTML = "";
  layer.onclick = null;
  state.addDevice = null;
}

function showModalError(message) {
  const el = $("modalError");
  if (!el) { toast("操作失败", message, "error"); return; }
  el.textContent = message;
  el.hidden = false;
}

function openAddDevice() {
  state.addDevice = { mode: "nearby", selected: null, code: "" };
  showModal('<div class="modal wide">' +
    '<div class="modal-head"><div><h3>添加设备</h3><p>在同一 Wi-Fi 下发现其他 LanDrop 设备，或手动输入 IP 地址。</p></div>' +
    '<button class="modal-close" data-close aria-label="关闭">' + icon("x") + '</button></div>' +
    '<div class="modal-body">' +
    '<div class="tabs"><button class="tab active" data-tab="nearby">附近设备</button><button class="tab" data-tab="manual">手动输入 IP</button></div>' +
    '<div id="addNearby">' +
    '<div class="scan-row"><span id="scanStatus">正在搜索同一 Wi-Fi 下的设备…</span><button class="link-btn" id="btnRescan">重新扫描</button></div>' +
    '<div class="device-list" id="addDeviceList"></div>' +
    '<div id="pairCodeBox" hidden><div class="field"><label>输入对方连接码</label>' +
    '<input class="input mono" id="pairCode" maxlength="9" placeholder="ABCD-EFGH" autocomplete="off"></div>' +
    '<div class="hint">连接码显示在对方电脑 LanDrop 窗口的右上角。</div></div>' +
    '</div>' +
    '<div id="addManual" hidden>' +
    '<div class="field"><label>对方 IP 地址</label><input class="input" id="manualHost" placeholder="例如 192.168.1.8"></div>' +
    '<div class="field"><label>端口</label><input class="input" id="manualPort" value="52180"></div>' +
    '<div class="field"><label>连接码</label><input class="input mono" id="manualCode" maxlength="9" placeholder="ABCD-EFGH"></div>' +
    '</div>' +
    '<div class="modal-error" id="addError" hidden></div>' +
    '<div class="own-code-row"><span>本机连接码 <code>' + esc(state.device ? state.device.code : "") + '</code></span>' +
    '<button class="link-btn" id="btnCopyOwnCode">复制</button></div>' +
    '</div>' +
    '<div class="modal-foot"><button class="btn ghost" data-close>取消</button>' +
    '<button class="btn primary" id="btnConnect" disabled>连接</button></div></div>');

  $("btnCopyOwnCode").addEventListener("click", () => copyText(state.device.code, "连接码已复制"));
  $("btnRescan").addEventListener("click", scanNearby);
  $("pairCode").addEventListener("input", () => { state.addDevice.code = $("pairCode").value; updateConnectButton(); });
  $("manualHost").addEventListener("input", updateConnectButton);
  $("manualCode").addEventListener("input", updateConnectButton);
  $("manualPort").addEventListener("input", updateConnectButton);
  $("btnConnect").addEventListener("click", connectDevice);
  $$("[data-tab]").forEach((tab) => tab.addEventListener("click", () => switchAddTab(tab.dataset.tab)));
  $("pairCode").addEventListener("keydown", (event) => { if (event.key === "Enter") connectDevice(); });
  ["manualHost", "manualPort", "manualCode"].forEach((id) => {
    $(id).addEventListener("keydown", (event) => { if (event.key === "Enter") connectDevice(); });
  });
  renderAddDeviceList();
  scanNearby();
}

function switchAddTab(mode) {
  if (!state.addDevice) return;
  state.addDevice.mode = mode;
  $$("[data-tab]").forEach((tab) => tab.classList.toggle("active", tab.dataset.tab === mode));
  $("addNearby").hidden = mode !== "nearby";
  $("addManual").hidden = mode !== "manual";
  const error = $("addError");
  if (error) error.hidden = true;
  updateConnectButton();
  if (mode === "manual") setTimeout(() => { const el = $("manualHost"); if (el) el.focus(); }, 30);
}

function renderAddDeviceList() {
  const list = $("addDeviceList");
  if (!list) return;
  const devices = state.peers.filter((peer) => !peer.paired && peer.online);
  if (devices.length === 0) {
    list.innerHTML = '<div class="modal-empty"><strong>没有发现未配对的设备</strong>请确认另一台电脑也打开了 LanDrop，并且在同一 Wi-Fi。<br>如果自动发现失败，可以切换到“手动输入 IP”。</div>';
    return;
  }
  list.innerHTML = devices.map((peer) => {
    const selected = state.addDevice && state.addDevice.selected && state.addDevice.selected.id === peer.id;
    return '<button class="device-row' + (selected ? " active" : "") + '" data-device="' + esc(peer.id) + '">' +
      '<span class="device-avatar">' + icon("monitor") + "</span>" +
      '<span class="device-info"><strong>' + esc(peer.name) + "</strong><span>" + esc(peer.host) + ":" + peer.port + "</span></span>" +
      '<span class="device-badge">可连接</span></button>';
  }).join("");
  $$(".device-row", list).forEach((row) => {
    row.addEventListener("click", () => {
      const peer = devices.find((item) => item.id === row.dataset.device);
      if (!peer) return;
      state.addDevice.selected = { id: peer.id, host: peer.host, port: peer.port, name: peer.name };
      $("pairCodeBox").hidden = false;
      $("pairCode").value = state.addDevice.code || "";
      renderAddDeviceList();
      updateConnectButton();
      setTimeout(() => { const el = $("pairCode"); if (el) el.focus(); }, 30);
    });
  });
}

async function scanNearby() {
  if (!state.addDevice) return;
  const status = $("scanStatus");
  if (status) status.innerHTML = '<span class="spinner"></span> 正在搜索…';
  try {
    const data = await getJson("/api/discover");
    state.peers = data.peers || [];
    renderPeerSelect();
    renderNetStatus();
    const count = state.peers.filter((peer) => !peer.paired).length;
    if (status) status.textContent = count > 0 ? "找到 " + count + " 台可连接的设备" : "没有发现新设备";
    renderAddDeviceList();
  } catch (error) {
    if (status) status.textContent = "自动发现失败，可以使用手动输入";
  }
}

function updateConnectButton() {
  const button = $("btnConnect");
  if (!button || !state.addDevice) return;
  if (state.addDevice.mode === "nearby") {
    const hasDevice = !!state.addDevice.selected;
    const code = ($("pairCode") ? $("pairCode").value.trim() : "").replace(/\s/g, "");
    button.disabled = !hasDevice || code.length < 6;
  } else {
    const host = $("manualHost") ? $("manualHost").value.trim() : "";
    const code = $("manualCode") ? $("manualCode").value.trim() : "";
    button.disabled = !host || code.length < 6;
  }
}

async function connectDevice() {
  if (!state.addDevice) return;
  const button = $("btnConnect");
  if (button.disabled) return;
  let host, port, code;
  if (state.addDevice.mode === "nearby") {
    if (!state.addDevice.selected) return;
    host = state.addDevice.selected.host;
    port = state.addDevice.selected.port;
    code = $("pairCode").value.trim();
  } else {
    host = $("manualHost").value.trim();
    port = parseInt($("manualPort").value, 10) || 52180;
    code = $("manualCode").value.trim();
  }
  $("addError").hidden = true;
  button.disabled = true;
  button.textContent = "连接中…";
  try {
    const data = await postJson("/api/peers/add", { host: host, port: port, code: code });
    closeModal();
    await pollPeers();
    if (data.peer) {
      await selectPeer(data.peer.id);
      toast("已连接", data.peer.name, "success");
    }
  } catch (error) {
    const box = $("addError");
    if (box) { box.textContent = error.message; box.hidden = false; }
    button.disabled = false;
    button.textContent = "连接";
  }
}

function openSettings() {
  if (!state.device) return;
  const addresses = state.device.addresses || [];
  const addressHtml = addresses.length > 0
    ? addresses.map((url) => '<div class="address-box"><span>' + esc(url) + '</span><button class="link-btn" data-copy="' + esc(url) + '">复制</button></div>').join("")
    : '<div class="modal-empty">未检测到局域网地址，请检查网络连接。</div>';
  showModal('<div class="modal wide">' +
    '<div class="modal-head"><div><h3>设置</h3><p>设备信息、连接方式与安全选项。</p></div>' +
    '<button class="modal-close" data-close aria-label="关闭">' + icon("x") + '</button></div>' +
    '<div class="modal-body">' +
    '<div class="setting-row"><div><div class="setting-title">设备名称</div><div class="setting-desc">对方在设备列表中看到的名字</div></div>' +
    '<div class="name-edit"><input class="input" id="settingName" maxlength="32" value="' + esc(state.device.name) + '"><button class="btn small-btn" id="btnSaveName">保存</button></div></div>' +
    '<div class="setting-row"><div><div class="setting-title">本机连接码</div><div class="setting-desc">在另一台电脑添加设备时输入这个码</div></div>' +
    '<div class="name-edit"><code class="setting-value">' + esc(state.device.code) + '</code><button class="btn small-btn" id="btnCopyCode">复制</button></div></div>' +
    '<div class="setting-row"><div class="setting-grow"><div class="setting-title">本机访问地址</div><div class="setting-desc">端口 ' + (state.device.port || "") + '</div><div class="addr-list">' + addressHtml + '</div></div></div>' +
    '<div class="setting-row"><div><div class="setting-title">开机自动启动</div><div class="setting-desc">登录 Windows 后自动在托盘运行</div></div>' +
    '<label class="switch"><input type="checkbox" id="settingAutostart"' + (state.device.autostart ? " checked" : "") + '><span></span></label></div>' +
    '<div class="setting-row"><div><div class="setting-title">数据文件夹</div><div class="setting-desc">配置、日志和接收记录</div></div>' +
    '<button class="btn small-btn" id="btnOpenData">打开</button></div>' +
    '<div class="security-note"><strong>只在可信的家庭 Wi-Fi 中使用。</strong>连接码相当于文件访问钥匙，拿到连接码的人可以浏览和修改本机文件。不要把连接码发给陌生人；在公共 Wi-Fi 下建议关闭 LanDrop。</div>' +
    '<div class="about-line">LanDrop ' + esc(state.device.version || "1.0.0") + " · 文件只在局域网内传输</div>" +
    '</div><div class="modal-foot"><button class="btn ghost" data-close>关闭</button></div></div>');

  $("btnCopyCode").addEventListener("click", () => copyText(state.device.code, "连接码已复制"));
  $$("[data-copy]", $("modalLayer")).forEach((button) => {
    button.addEventListener("click", () => copyText(button.dataset.copy, "地址已复制"));
  });
  $("btnSaveName").addEventListener("click", async () => {
    const name = $("settingName").value.trim();
    if (!name) return;
    try {
      const data = await postJson("/api/config/name", { name: name });
      state.device.name = data.name;
      renderHeader();
      toast("已保存", "设备名称已更新", "success");
    } catch (error) {
      toast("保存失败", error.message, "error");
    }
  });
  $("settingAutostart").addEventListener("change", async (event) => {
    const enabled = event.target.checked;
    try {
      const data = await postJson("/api/config/autostart", { enabled: enabled });
      state.device.autostart = data.autostart;
      event.target.checked = data.autostart;
      toast("已保存", data.autostart ? "已开启开机启动" : "已关闭开机启动", "success");
    } catch (error) {
      event.target.checked = !enabled;
      toast("设置失败", error.message, "error");
    }
  });
  $("btnOpenData").addEventListener("click", async () => {
    try { await postJson("/api/config/open-folder", {}); }
    catch (error) { toast("无法打开", error.message, "error"); }
  });
}

/* ---------------- events ---------------- */
function bindEvents() {
  $("codeChip").addEventListener("click", () => copyText(state.device ? state.device.code : "", "连接码已复制"));
  $("btnSettings").addEventListener("click", openSettings);
  $("btnRefreshDevices").addEventListener("click", () => refreshDevices(true));
  $("btnAddPeer").addEventListener("click", openAddDevice);
  $("btnAddPeer2").addEventListener("click", openAddDevice);

  $("btnLocalRefresh").addEventListener("click", () => loadLocal(state.local.path));
  $("btnLocalGo").addEventListener("click", () => openPathDialog("local"));
  $("localDrive").addEventListener("change", (event) => { if (event.target.value !== undefined) loadLocal(event.target.value); });
  $("localHidden").addEventListener("change", (event) => {
    state.local.hidden = event.target.checked;
    loadLocal(state.local.path);
  });
  $("localSearch").addEventListener("input", debounce((event) => {
    state.local.filter = event.target.value;
    renderEntries("local");
  }, 100));
  $("localAll").addEventListener("change", (event) => {
    const entries = visibleEntries("local");
    if (event.target.checked) entries.forEach((entry) => state.local.selected.add(entry.path));
    else entries.forEach((entry) => state.local.selected.delete(entry.path));
    updateSelectionUI("local");
  });
  $("localList").addEventListener("click", (event) => onFileClick(event, "local"));
  $("localList").addEventListener("change", (event) => onFileCheck(event, "local"));
  $("localList").addEventListener("dblclick", (event) => onFileDoubleClick(event, "local"));
  $("localCrumb").addEventListener("click", (event) => {
    const crumb = event.target.closest("[data-path]");
    if (crumb) navigate("local", crumb.dataset.path);
  });
  $("btnLocalMkdir").addEventListener("click", () => openMkdir("local"));
  $("btnLocalRename").addEventListener("click", () => openRename("local"));
  $("btnLocalDelete").addEventListener("click", () => openDelete("local"));
  $("btnPush").addEventListener("click", () => startTransfer("push"));

  $("peerSelect").addEventListener("change", (event) => {
    if (event.target.value) selectPeer(event.target.value);
    else showRemoteEmpty();
  });
  $("btnRemoteGo").addEventListener("click", () => openPathDialog("remote"));
  $("remoteDrive").addEventListener("change", (event) => { if (event.target.value !== undefined) loadRemote(event.target.value); });
  $("remoteHidden").addEventListener("change", (event) => {
    state.remote.hidden = event.target.checked;
    loadRemote(state.remote.path);
  });
  $("remoteSearch").addEventListener("input", debounce((event) => {
    state.remote.filter = event.target.value;
    renderEntries("remote");
  }, 100));
  $("remoteAll").addEventListener("change", (event) => {
    const entries = visibleEntries("remote");
    if (event.target.checked) entries.forEach((entry) => state.remote.selected.add(entry.path));
    else entries.forEach((entry) => state.remote.selected.delete(entry.path));
    updateSelectionUI("remote");
  });
  $("remoteList").addEventListener("click", (event) => onFileClick(event, "remote"));
  $("remoteList").addEventListener("change", (event) => onFileCheck(event, "remote"));
  $("remoteList").addEventListener("dblclick", (event) => onFileDoubleClick(event, "remote"));
  $("remoteCrumb").addEventListener("click", (event) => {
    const crumb = event.target.closest("[data-path]");
    if (crumb) navigate("remote", crumb.dataset.path);
  });
  $("btnRemoteMkdir").addEventListener("click", () => openMkdir("remote"));
  $("btnRemoteRename").addEventListener("click", () => openRename("remote"));
  $("btnRemoteDelete").addEventListener("click", () => openDelete("remote"));
  $("btnPull").addEventListener("click", () => startTransfer("pull"));

  $("dockHead").addEventListener("click", (event) => {
    if (event.target.closest("button")) return;
    toggleDock();
  });
  $("btnDockToggle").addEventListener("click", (event) => {
    event.stopPropagation();
    toggleDock();
  });
  $("btnClearDone").addEventListener("click", async (event) => {
    event.stopPropagation();
    try {
      await postJson("/api/transfer/clear", {});
      await pollTransfers();
    } catch (error) { toast("操作失败", error.message, "error"); }
  });
  $("dockList").addEventListener("click", async (event) => {
    const button = event.target.closest("[data-cancel]");
    if (!button) return;
    try {
      await postJson("/api/transfer/cancel", { id: Number(button.dataset.cancel) });
      await pollTransfers();
    } catch (error) { toast("取消失败", error.message, "error"); }
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !$("modalLayer").hidden) closeModal();
    if (event.key === "Backspace" && $("modalLayer").hidden) {
      const tag = (document.activeElement && document.activeElement.tagName) || "";
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;
      if (document.activeElement === $("localList") && state.local.data && state.local.data.parent !== null) navigate("local", state.local.data.parent || "");
      else if (document.activeElement === $("remoteList") && state.remote.data && state.remote.data.parent !== null) navigate("remote", state.remote.data.parent || "");
    }
  });
}

function toggleDock() {
  state.dockExpanded = !state.dockExpanded;
  state.dockUserCollapsed = !state.dockExpanded;
  renderDock();
}

/* start */
boot().then(function () {
  if (location.hash === "#add") setTimeout(openAddDevice, 350);
  if (location.hash === "#settings") setTimeout(openSettings, 350);
  if (location.hash === "#dock") { state.dockExpanded = true; renderDock(); }
}).catch(function () { });
