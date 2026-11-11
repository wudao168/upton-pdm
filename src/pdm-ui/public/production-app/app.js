/* Android 9 WebView compatible: no modules, optional chaining or remote code. */
(function () {
  'use strict';
  // Packaging on the PDA is reserved for a future rollout; current operators use the PC packaging workflow.
  var DEVICE_PACKAGING_ENABLED = false;
  var $ = function (id) { return document.getElementById(id); };
  var bridge = window.ProductionNative;
  var callbacks = {}, sequence = 0;
  window.productionNativeResult = function (id, status, body) {
    var cb = callbacks[id]; if (!cb) return; delete callbacks[id];
    cb({ status: status, body: body });
  };
  var state = { page: 'login', session: null, profile: null, record: null, selected: null,
    action: null, quantity: 0, printer: localStorage.getItem('production.printer') || '', printers: [],
    busy: false, attempt: null, drawings: [], chosen: {}, filter: '', completed: false, result: '', panel: null };
  var server = bridge ? bridge.server() : location.origin;
  function pendingKey() { return 'production.pending.' + (state.session ? state.session.username || 'current' : ''); }
  function resetBusinessState() { state.record = state.selected = state.action = state.attempt = null; state.quantity = 0; state.drawings = []; state.chosen = {}; state.completed = false; state.result = ''; close(); }
  var clientId = localStorage.getItem('production.client');
  if (!clientId) { clientId = uuid(); localStorage.setItem('production.client', clientId); }
  function uuid() { var a = new Uint8Array(16); window.crypto.getRandomValues(a); a[6] = a[6] & 15 | 64; a[8] = a[8] & 63 | 128;
    var s = Array.prototype.map.call(a, function (v) { return ('0' + v.toString(16)).slice(-2); }).join('');
    return s.slice(0, 8) + '-' + s.slice(8, 12) + '-' + s.slice(12, 16) + '-' + s.slice(16, 20) + '-' + s.slice(20); }
  function esc(v) { return String(v == null ? '' : v).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function button(text, action, cls, disabled) { return '<button type="button" data-act="' + action + '" class="' + (cls || '') + '"' + (disabled ? ' disabled' : '') + '>' + esc(text) + '</button>'; }
  function notice(text) { $('notice').textContent = text; $('notice').hidden = false; clearTimeout(notice.timer); notice.timer = setTimeout(function () { $('notice').hidden = true; }, 5500); }
  async function api(path, method, body) {
    var raw, status;
    if (bridge) {
      var id = String(++sequence);
      var result = await new Promise(function (resolve) { callbacks[id] = resolve; bridge.request(id, path, method || 'GET', body == null ? '' : JSON.stringify(body)); });
      raw = result.body; status = result.status;
    } else {
      try { var response = await fetch(path, { method: method || 'GET', headers: { 'Content-Type': 'application/json',
        Authorization: state.session ? 'Bearer ' + state.session.accessToken : '' }, body: body == null ? undefined : JSON.stringify(body) });
        raw = await response.text(); status = response.status;
      } catch (_) { throw new Error('无法连接服务器，请检查局域网连接后重试。'); }
    }
    var data = null; try { data = raw ? JSON.parse(raw) : null; } catch (_) { /* Never display server HTML/raw exceptions. */ }
    if (status < 200 || status >= 300) {
      if (status === 401) { state.session = state.profile = null; resetBusinessState(); state.page = 'login'; render(); }
      var message = data && (data.message || data.detail || data.title);
      throw new Error(typeof message === 'string' && /[\u3400-\u9fff]/.test(message) ? message : status === 403 ? '当前账号没有操作权限，请联系管理员。' : '操作未完成，请检查连接并重试；重复提交不会重复计数。');
    }
    return data;
  }
  function fit() { var s = Math.min(innerWidth / 480, innerHeight / 740); $('app').style.transform = 'scale(' + s + ')'; $('app').style.left = Math.max(0, (innerWidth - 480 * s) / 2) + 'px'; $('app').style.top = Math.max(0, (innerHeight - 740 * s) / 2) + 'px'; }
  window.addEventListener('resize', fit); fit();
  function nav() {
    var a = state.action, choices = availableActions(), canChoose = state.record && !a && choices.length > 1;
    var text = state.completed ? '请继续扫码' : reservedPackaging(a) ? '包装在电脑端办理' : canChoose ? '选择本次工序' : isPack(a) ? '确认并打印' : a ? '确认流转' : '等待扫码';
    $('nav').innerHTML = state.session ? button(text, canChoose ? 'choose-action' : 'submit', 'primary',
      state.completed || reservedPackaging(a) || !canChoose && (!a || state.quantity <= 0 || isPack(a) && !state.record.canPrint)) : '';
  }
  function summary() { var r = state.record, l = r.line, lot = r.lots.find(function (x) { return state.action && x.id === state.action.lotId; });
    return '<div class="card summary"><div class="drawing">' + esc(l.drawingNumber) + '</div><div class="name">' + esc(l.name) + '</div><div class="meta"><span>项目 ' + esc(r.projectCode) + '</span><span>版本 ' + esc(l.displayRevision || l.revision) + '</span></div><div class="batch">发布批次 ' + esc(r.packageNumber) + '</div><div class="meta"><span>批次数量 ' + esc(lot ? lot.quantity : l.requiredQuantity) + ' ' + esc(unitName(l.unit)) + '</span><span>可办理 ' + (state.action ? state.action.remainingQuantity : 0) + '</span></div></div>'; }
  function isControl(a) { return ['Pause', 'Resume', 'Cancelled', 'TaskCancelled', 'StageScrap'].indexOf(a.kind) >= 0; }
  function isException(a) { return isControl(a) || a.kind === 'Rework' || a.kind === 'Scrap'; }
  function isPack(a) { return a && a.createsPackageLabel === true; }
  function reservedPackaging(a) { return a && !isException(a) && !DEVICE_PACKAGING_ENABLED && (a.post === 'Packing' || a.kind === 'PackingStarted' || a.kind === 'Packed' || isPack(a)); }
  function availableActions() { return state.record ? state.record.actions.filter(function (a) { return !isException(a) && (!state.selected.lotId || state.selected.lotId === a.lotId); }) : []; }
  function actionText(a) { var lot = state.record.lots.find(function (l) { return l.id === a.lotId; }); return a.label + ' · ' + (a.workstationName || '默认工位') + (state.record.lots.length > 1 ? ' · ' + (lot ? lot.id.slice(0, 6) : '') : ''); }
  function render() {
    nav(); $('operator').hidden = !state.session; $('operator').textContent = '更多';
    var html = '', r = state.record, a = state.action;
    if (!state.session || state.page === 'login') {
      html = '<img class="login-logo" src="logo.png" alt="PDM"><h2 class="center">生产执行</h2><input id="server" aria-label="服务器地址" placeholder="服务器地址，如 http://电脑IP:端口" value="' + esc(server) + '"' + (!bridge ? ' readonly' : '') + '><input id="username" autocomplete="username" aria-label="账号" placeholder="登录账号"><input id="password" type="password" autocomplete="current-password" aria-label="密码" placeholder="密码">' + button('登录', 'login', 'primary') + '<p class="muted center">使用现有 PDM 账号及工位权限</p>';
    } else {
      html = '<div class="scan-row"><textarea id="scan-code" rows="1" inputmode="none" autocomplete="off" aria-label="二维码内容" placeholder="请按 PDA 扫码键或扫码枪"></textarea>' + button('相机', 'camera', '', !bridge) + '</div>';
      if (r) {
        html += summary();
        html += '<div class="card step"><span class="small">本次工序</span><strong>' + esc(a ? a.label : availableActions().length > 1 ? '需核对工序或加工任务' : '暂无可执行工序') + '</strong><span class="small">' + esc(a ? a.workstationName || '默认工位' : '请核对岗位、前序交接及暂停状态') + '</span></div>';
        html += reservedPackaging(a) ? '<div class="card reserved"><h3>包装扫码入口已预留</h3><p>请在电脑端登记实际包装数量并打印标签。</p><p>本次扫码仅核对批次，不提交包装数量。</p></div>' : '<div><div class="caption">' + (isPack(a) ? '本箱／袋数量' : '本次数量') + '<span>需要时可修改</span></div><div class="quantity">' + button('−', 'minus', '', !a || state.completed) + button(state.quantity, 'quantity', 'value', !a || state.completed) + button('+', 'plus', '', !a || state.completed) + '</div></div>';
        var hint = state.result || (reservedPackaging(a) ? '无需在 PDA 配置打印机，可继续扫描其他图纸。' : isPack(a) ? state.printer ? '确认后打印一张标签 · ' + printerName() : '请在“更多 → 工作站设置”选择打印机' : a ? '核对后点确认，完成本次工序交接。' : availableActions().length > 1 ? '存在多个可执行任务，请先核对选择。' : r.hint || '当前工序尚不可执行。');
        html += '<div class="feedback' + (state.completed ? ' success' : '') + '" role="status">' + esc(hint) + '</div>';
      } else {
        html += '<div class="scan-welcome grow"><div class="scan-mark">⌗</div><h2>扫码 → 确认</h2><p>自动识别批次和本次工序</p><p>数量默认带出，需要时修改</p></div><div class="feedback" role="status">' + esc(state.result || '请扫描图纸二维码，操作在本页完成。') + '</div>';
      }
    }
    $('page').innerHTML = html; disableBusy();
  }
  function showPanel(page) {
    var html = '', r = state.record;
    if (page === 'records' && r) {
      var records = r.movements.filter(function (m) { return m.label; }).filter(function (m) { return !state.filter || String(m.label.quantity) === state.filter || m.id.indexOf(state.filter) >= 0; }).slice(0, 2);
      html = '<div class="row"><h2>包装补打</h2>' + button('刷新', 'refresh', 'compact') + '</div><input id="record-filter" class="compact" placeholder="按数量或标签编号筛选" value="' + esc(state.filter) + '"><div class="list">' + records.map(function (m) { var jobs = r.printJobs.filter(function (j) { return j.labelId === m.id; }); var j = jobs[jobs.length - 1]; return '<div class="card record"><div><h3>' + esc(m.label.quantity) + ' ' + esc(unitName(m.label.unit)) + ' / 箱袋</h3><p class="small">' + esc(date(m.occurredAt)) + ' · ' + esc(m.actorName || '姓名待补充') + '</p><p class="status">' + statusName(j && j.status) + '</p><p class="small">编号 ' + esc(m.id.slice(0, 8)) + '</p></div>' + button('补打', 'reprint:' + m.id, '', !r.canReprint || j && ['Pending', 'Sending'].indexOf(j.status) >= 0) + '</div>'; }).join('') + '</div><p class="muted grow">' + (records.length ? '显示最近匹配的 2 条。可输入标签编号或数量筛选；更早记录在电脑端查询。' : '当前没有匹配的包装记录。') + '</p>';
    } else if (page === 'controls' && r) {
      var controls = r.actions.filter(isException);
      html = '<h2>生产异常操作</h2><div class="card"><h3>' + esc(r.line.drawingNumber) + '</h3><p class="muted">' + esc(r.packageNumber) + '</p></div><div class="control-grid">' + ['Pause', 'Resume', 'TaskCancelled', 'Cancelled', 'StageScrap', 'Rework', 'Scrap'].filter(function (kind) { return controls.some(function (a) { return a.kind === kind; }); }).map(function (kind) { var i = ['Pause', 'Resume', 'TaskCancelled', 'Cancelled', 'StageScrap', 'Rework', 'Scrap'].indexOf(kind); return button(['暂停', '恢复', '取消加工任务', '取消业务需求', '环节报废', '检验返工', '检验报废'][i], 'control:' + kind, i > 1 ? 'danger' : '', !controls.some(function (a) { return a.kind === kind; })); }).join('') + '</div><p class="muted grow">按授权范围显示操作。取消加工任务保留需求；取消业务需求减少需求；报废保留追溯记录。</p>';
    } else if (page === 'settings') {
      html = '<h2>我的工作站</h2><div class="card"><h3>' + esc(state.profile.displayName) + '</h3><p class="muted">' + esc(state.session.activeCompanyName || '当前公司') + '</p></div>' + (DEVICE_PACKAGING_ENABLED ? button('选择包装打印机', 'printers') : '') + button('选中图纸作废', 'page:drawings', 'danger', !state.profile.canObsolete) + button('切换操作账号', 'logout') + '<p class="muted">Android 9 及以上 · 竖屏 480×800<br>日常工序扫码确认，包装及标签打印可在电脑端完成。</p>';
    } else if (page === 'drawings') {
      html = '<h2>选择作废图纸</h2><div class="row"><input id="drawing-query" aria-label="图纸搜索" placeholder="图号／名称" value="' + esc(state.drawingQuery || '') + '">' + button('查询', 'drawings-query') + '</div><div class="list">' + state.drawings.slice(0, 3).map(function (d) { return '<label class="choice"><input type="checkbox" data-document="' + d.documentId + '"' + (state.chosen[d.documentId] ? ' checked' : '') + '><div><strong>' + esc(d.drawingNumber) + '</strong><p>' + esc(d.name) + ' · ' + esc(d.revision) + '</p><p>关联发布 ' + d.releases.length + ' 个</p></div></label>'; }).join('') + '</div><p class="muted grow">' + (state.drawings.length > 3 ? '匹配过多，请补充图号缩小范围后选择。' : '作废影响所选图档的关联发布及生产使用，请先确认替代图纸。') + '</p>' + button('作废已选图纸', 'obsolete', 'danger', state.drawings.length > 3 || !Object.keys(state.chosen).some(function (k) { return state.chosen[k]; }));

    } else { return; }
    html = html.replace(/<h2>[^<]*<\/h2>/, '');
    modal({ records: '包装补打', controls: '异常处理', settings: '工作站设置', drawings: '选择作废图纸' }[page], html);
    $('overlay').querySelector('.dialog').classList.add('panel-dialog'); state.panel = page;
  }
  function more() {
    var r = state.record;
    modal('更多操作', '<p class="muted">' + esc(state.profile.displayName) + ' · 日常操作只需扫码、确认</p><div class="control-grid">' +
      button('异常处理', 'page:controls', '', !r || !r.actions.some(isException)) +
      (DEVICE_PACKAGING_ENABLED && r && isPack(state.action) ? button('包装补打', 'page:records', '', !r.movements.some(function (m) { return m.label; })) + button('标签预览', 'preview') : '') +
      button('刷新当前', 'refresh', '', !r) + button('工作站设置', 'page:settings') + '</div>');
  }
  function chooseAction() {
    var choices = availableActions();
    modal('核对本次工序', '<p class="muted">此批次有多个可执行任务，请核对工序与工位。</p><select id="action-choice" aria-label="本次工序"><option value="">请选择</option>' + choices.map(function (a, i) { return '<option value="' + i + '">' + esc(actionText(a)) + '</option>'; }).join('') + '</select>', button('返回', 'close') + button('选定工序', 'action-save', 'primary'));
  }
  function date(value) { var d = new Date(value); return d.getFullYear() + '.' + (d.getMonth() + 1) + '.' + d.getDate() + ' ' + ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2); }
  function statusName(s) { return { Pending: '待打印工作站领取', Sending: '发送中，请勿重复打印', Submitted: '已交给打印机，请核对实物', Failed: '发送失败，可核对后补打', Unknown: '结果不确定，先核对实物', Blocked: '图纸失效，打印已拦截' }[s] || '未生成打印任务'; }
  function printerName() { var p = state.printers.find(function (x) { return x.id === state.printer; }); return p ? p.name : '尚未选择打印机'; }
  function disableBusy() { if (state.busy) Array.prototype.forEach.call(document.querySelectorAll('button'), function (b) { if (!b.disabled) { b.setAttribute('data-busy-disabled', 'true'); b.disabled = true; } }); }
  function restoreBusy() { Array.prototype.forEach.call(document.querySelectorAll('[data-busy-disabled]'), function (b) { b.disabled = false; b.removeAttribute('data-busy-disabled'); }); }
  function modal(title, body, buttons) { $('overlay').hidden = false; $('overlay').innerHTML = '<section class="dialog" role="dialog" aria-modal="true" aria-label="' + esc(title) + '"><h2>' + esc(title) + '</h2>' + body + '<div class="row">' + (buttons || button('返回', 'close')) + '</div></section>'; }
  function close() { state.panel = null; $('overlay').hidden = true; $('overlay').innerHTML = ''; }
  async function refresh() { if (!state.selected) return; var prior = state.action; state.record = await api('/api/production-mobile/releases/' + state.selected.releasePackageId + '?lineId=' + state.selected.lineId); var actions = availableActions(); state.action = actions.find(function (a) { return prior && a.kind === prior.kind && a.lotId === prior.lotId && a.workstationId === prior.workstationId; }) || (actions.length === 1 ? actions[0] : null); state.quantity = state.action ? state.action.remainingQuantity : 0; }
  async function scan(code) { if (!code.trim()) throw new Error('请先扫描二维码。'); state.record = state.selected = state.action = null; state.completed = false; state.quantity = 0; state.result = ''; render(); var matches = await api('/api/production-mobile/scan?code=' + encodeURIComponent(code.trim())); if (!matches.length) throw new Error('未找到可访问的有效批次，请核对图纸和项目权限。');
    if (matches.length > 1) { state.matches = matches; modal('请选择发布批次', '<select id="scan-match" aria-label="发布批次">' + matches.map(function (m, i) { return '<option value="' + i + '">' + esc(m.packageNumber + ' · ' + m.drawingNumber + ' · ' + m.status) + '</option>'; }).join('') + '</select><p class="muted">此二维码对应多条记录，请按纸质图纸的发布批次核对。</p>', button('返回', 'close') + button('选择', 'choose-scan', 'primary')); return; }
    await select(matches[0]); }
  async function select(item) { state.selected = item; state.action = null; state.attempt = null; await refresh(); if (DEVICE_PACKAGING_ENABLED && isPack(state.action)) await loadPrinters(); state.page = 'scan'; close(); }
  function labelPreview() { var r = state.record; return '<div class="label-preview"><div class="company">昆山市阿普顿自动化系统有限公司</div>' + [['项目号', r.projectCode], ['名称', r.line.name], ['图号', r.line.drawingNumber], ['数量', state.quantity + ' ' + unitName(r.line.unit)], ['日期', date(new Date()).split(' ')[0]], ['版本', r.line.displayRevision || r.line.revision], ['批次', r.packageNumber]].map(function (x) { return '<div class="label-row"><span>' + x[0] + '</span><b>' + esc(x[1]) + '</b></div>'; }).join('') + '<p class="codehint">正式二维码按此次箱袋记录生成，关联该发布批次与实际数量。</p></div>'; }
  function quantityDialog(max, initial, callback) { state.number = String(initial); state.numberMax = max; state.numberCallback = callback; state.numberFresh = true;
    modal('本次数量 · 最多 ' + max, '<div class="big-number" id="number-value">' + esc(initial) + '</div><div class="keypad">' + ['1', '2', '3', '4', '5', '6', '7', '8', '9', '.', '0', '退格'].map(function (k) { return button(k, 'key:' + k); }).join('') + '</div>', button('取消', 'close') + button('确定', 'number-ok', 'primary')); }
  function integerUnit() { return /^(001|002|003|007|008|010|011|EA|PCS|PC|个|件|只|台|双|片|支|组|套)$/i.test(state.record.line.unit); }
  function unitName(unit) { return { '001':'个', '002':'台', '003':'只', '004':'盒', '005':'卷', '006':'捆', '007':'双', '008':'片', '009':'桶', '010':'支', '011':'套', '012':'箱', '013':'包' }[unit] || unit; }
  function validateQuantity(q, max) { if (!isFinite(q) || q <= 0 || q > max) throw new Error('请输入大于零且不超过可办理数量的数值。'); if (integerUnit() && q % 1 !== 0) throw new Error('当前单位按件计数，数量必须为整数。'); }
  async function loadPrinters() { var p = await api('/api/production-mobile/printers'); state.printerCatalog = p; state.printers = p.printers.filter(function (x) { return x.isActive; }); if (!state.printers.some(function (x) { return x.id === state.printer; })) state.printer = state.printers.length === 1 ? state.printers[0].id : ''; }
  function printerDialog() { modal('包装打印机', '<select id="printer-choice" aria-label="打印机"><option value="">请选择打印机</option>' + state.printers.map(function (p) { return '<option value="' + p.id + '"' + (p.id === state.printer ? ' selected' : '') + '>' + esc(p.name) + '</option>'; }).join('') + '</select><p class="muted">电脑工作站运行打印助手。PDA 与服务器在局域网内连接，打印机通过电脑驱动打印。</p>' + (state.profile.canManagePrinters ? button('新增打印工作站', 'printer-new') : '') + '<p class="muted">打印尺寸使用制造中心扫码配置。</p>', button('返回', 'close') + button('保存选择', 'printer-save', 'primary')); }
  async function submit(action, qty, note, recipient) {
    if (reservedPackaging(action)) throw new Error('包装扫码在本机为预留入口，请在电脑端完成包装和打印。');
    validateQuantity(qty, action.remainingQuantity);
    var input = { lotId: action.lotId, kind: action.kind, quantity: qty, workstationId: action.workstationId,
      sourceKind: action.sourceKind, note: note || null, recipient: recipient || null,
      labelPrinterId: isPack(action) ? state.printer : null };
    if (isPack(action) && !state.printer) throw new Error('请先在“更多 → 工作站设置”中选择包装打印机。');
    var identity = JSON.stringify(input);
    var previous = localStorage.getItem(pendingKey());
    if (previous) {
      previous = JSON.parse(previous);
      var priorInput = Object.assign({}, previous.input); delete priorInput.requestId;
      if (previous.releaseId !== state.selected.releasePackageId || JSON.stringify(priorInput) !== identity) {
        modal('请先核对上次提交', '<p class="body">上次提交未收到结果，请先按原操作编号核对。确认后才能登记新数量，不会重复计数。</p>', button('返回', 'close') + button('核对并重试', 'retry', 'primary'));
        return;
      }
      state.attempt = { identity: identity, requestId: previous.input.requestId };
    }
    if (!state.attempt || state.attempt.identity !== identity) state.attempt = { identity: identity, requestId: uuid() };
    input.requestId = state.attempt.requestId;
    // Keep the exact request until the server confirms it, including across process restarts.
    var pending = { releaseId: state.selected.releasePackageId, lineId: state.selected.lineId, input: input };
    localStorage.setItem(pendingKey(), JSON.stringify(pending));
    await api('/api/production-mobile/releases/' + pending.releaseId + '/movements', 'POST', input);
    localStorage.removeItem(pendingKey()); state.attempt = null; close(); state.completed = true; state.page = 'scan';
    state.result = '已确认' + action.label + ' ' + qty + ' ' + unitName(state.record.line.unit) + (isPack(action) ? '，标签已进入打印队列。' : '。') + '请继续扫码。';
    try { await refresh(); } catch (_) { /* Confirmed submission remains locked until a fresh scan, even if refresh fails. */ }
  }
  async function act(key) {
    if (key.indexOf('page:') === 0) { showPanel(key.slice(5)); return; }
    if (key === 'more') { more(); return; }
    if (key === 'choose-action') { chooseAction(); return; }
    if (key === 'action-save') { var selectedAction = $('action-choice').value; if (selectedAction === '') throw new Error('请核对并选择本次工序。'); state.action = availableActions()[Number(selectedAction)]; state.quantity = state.action.remainingQuantity; close(); return; }
    if (key === 'close') { close(); return; }
    if (key === 'login') {
      resetBusinessState();
      server = $('server').value.trim().replace(/\/$/, ''); if (bridge) bridge.setServer(server);
      state.session = await api('/api/auth/login', 'POST', { username: $('username').value.trim(), password: $('password').value, clientInstanceId: clientId, clientType: 'web' });
      try {
        state.profile = await api('/api/production-mobile/session');
      } catch (error) {
        state.session = state.profile = null;
        if (bridge) bridge.logout();
        throw error;
      }
      state.page = 'scan';
      var pending = localStorage.getItem(pendingKey()); if (pending) modal('上次提交待核对', '<p class="body">上次提交未收到结果。可使用相同操作编号重试，服务器不会重复计数。</p>', button('稍后处理', 'close') + button('核对并重试', 'retry', 'primary')); return;
    }
    if (key === 'retry') { var pending = JSON.parse(localStorage.getItem(pendingKey()) || 'null'); if (!pending) return; await api('/api/production-mobile/releases/' + pending.releaseId + '/movements', 'POST', pending.input); localStorage.removeItem(pendingKey()); state.attempt = null; close(); state.completed = true; state.result = '已确认上次操作结果，未重复计数。请继续扫码。'; if (state.selected && state.selected.releasePackageId === pending.releaseId) { try { await refresh(); } catch (_) {} } return; }
    if (key === 'logout') { try { await api('/api/auth/logout', 'POST'); } catch (_) {} if (bridge) bridge.logout(); resetBusinessState(); state.session = state.profile = null; state.page = 'login'; return; }
    if (key === 'scan') { await scan($('scan-code').value); return; }
    if (key === 'camera') { if (bridge) bridge.scan(); return; }
    if (key === 'choose-scan') { await select(state.matches[Number($('scan-match').value)]); return; }
    if (key === 'refresh') { await refresh(); if (state.panel === 'records') showPanel('records'); else close(); return; }
    if (key === 'minus' || key === 'plus') { state.quantity = Math.max(0, Math.min(state.action.remainingQuantity, Math.round((state.quantity + (key === 'plus' ? 1 : -1)) * 1000000) / 1000000)); return; }
    if (key === 'quantity') { quantityDialog(state.action.remainingQuantity, state.quantity, function (q) { state.quantity = q; }); return; }
    if (key.indexOf('key:') === 0) { var k = key.slice(4); if (k === '退格') state.number = state.number.slice(0, -1); else if (state.number.length < 12) { if (state.numberFresh) state.number = ''; if (k !== '.' || state.number.indexOf('.') < 0) state.number += k; } state.numberFresh = false; $('number-value').textContent = state.number || '0'; return; }
    if (key === 'number-ok') { var q = Number(state.number); validateQuantity(q, state.numberMax); state.numberCallback(q); close(); return; }
    if (key === 'preview') { modal('本箱／袋标签预览', labelPreview() + '<p class="muted">' + esc(printerName()) + ' · 一张标签</p>'); return; }
    if (key === 'printers') { await loadPrinters(); printerDialog(); return; }
    if (key === 'printer-save') { state.printer = $('printer-choice').value; localStorage.setItem('production.printer', state.printer); close(); return; }
    if (key === 'printer-new') { modal('新增打印工作站', '<input id="printer-name" placeholder="显示名称，如包装一号机"><input id="printer-queue" placeholder="电脑中安装的打印机名称"><input id="printer-agent" placeholder="工作站登录账号（凭据字段）"><p class="muted">工作站账号需授权发送任务及对应项目标签打印权限。</p>', button('取消', 'close') + button('保存', 'printer-create', 'primary')); return; }
    if (key === 'printer-create') { var printers = state.printerCatalog.printers.slice(); printers.push({ id: uuid(), name: $('printer-name').value.trim(), printerName: $('printer-queue').value.trim(), agentUsername: $('printer-agent').value.trim(), isActive: true }); await api('/api/production-mobile/printers', 'PUT', { printers: printers, expectedRowVersion: state.printerCatalog.rowVersion }); await loadPrinters(); printerDialog(); return; }
    if (key === 'submit') { if (state.completed || !state.action) return; var a = state.action; validateQuantity(state.quantity, a.remainingQuantity);
      if (a.kind === 'Issue' || a.kind === 'Rework' || a.kind === 'Scrap') { state.pendingAction = a; modal(a.label, '<p class="body">本次数量 ' + state.quantity + '</p><input id="operation-text" placeholder="' + (a.kind === 'Issue' ? '实际领料人姓名' : '请填写原因') + '">', button('返回', 'close') + button('确认', 'submit-extra', 'primary')); return; }
      await submit(a, state.quantity); return;
    }
    if (key === 'submit-extra') { var text = $('operation-text').value.trim(); if (!text) throw new Error('请填写领料人或原因。'); await submit(state.pendingAction, state.quantity, state.pendingAction.kind === 'Issue' ? null : text, state.pendingAction.kind === 'Issue' ? text : null); return; }
    if (key.indexOf('control:') === 0) { var controls = state.record.actions.filter(function (a) { return a.kind === key.slice(8); }); state.controlChoices = controls;
      modal(controls[0].label, '<select id="control-choice" aria-label="异常环节">' + controls.map(function (a, i) { return '<option value="' + i + '">' + esc((a.sourceName || a.label) + ' · 可办 ' + a.remainingQuantity) + '</option>'; }).join('') + '</select><input id="control-quantity" type="number" step="any" inputmode="decimal" aria-label="异常数量" value="' + controls[0].remainingQuantity + '"><textarea id="control-reason" placeholder="原因及实物处理说明（必填）" maxlength="120"></textarea><p class="muted">仅影响选中环节和数量，保留全部追溯记录。</p>', button('返回', 'close') + button('核对操作', 'control-confirm', 'danger')); return; }
    if (key === 'control-confirm') { var a = state.controlChoices[Number($('control-choice').value)], q = Number($('control-quantity').value), reason = $('control-reason').value.trim(); validateQuantity(q, a.remainingQuantity); if (!reason) throw new Error('请填写原因及实物处理说明。'); state.controlInput = { action: a, quantity: q, note: reason }; modal('确认' + a.label, '<p class="body">' + esc(state.record.line.drawingNumber) + '<br>本次数量：' + q + '<br>' + esc(a.sourceName || '') + '</p><p class="body">' + esc(reason) + '</p>', button('返回', 'close') + button('确认执行', 'control-submit', 'danger')); return; }
    if (key === 'control-submit') { var c = state.controlInput; await submit(c.action, c.quantity, c.note); return; }
    if (key.indexOf('reprint:') === 0) { state.reprintId = key.slice(8); var m = state.record.movements.find(function (x) { return x.id === state.reprintId; }); modal('补打原箱／袋标签', '<p class="body">原包装数量 ' + esc(m.label.quantity) + ' ' + esc(unitName(m.label.unit)) + '<br>补打不增加生产数量。</p><input id="reprint-reason" placeholder="补打原因（必填）" maxlength="120"><p class="muted">' + esc(printerName()) + '，请先核对实物，避免重复贴标。</p>', button('返回', 'close') + button('确认补打', 'reprint-submit', 'primary')); return; }
    if (key === 'reprint-submit') { var reason = $('reprint-reason').value.trim(); if (!reason) throw new Error('请填写补打原因。'); if (!state.printer) throw new Error('请先选择打印机。'); var payload = { printerId: state.printer, requestId: state.reprintRequest || (state.reprintRequest = uuid()), reason: reason }; await api('/api/production-mobile/releases/' + state.selected.releasePackageId + '/labels/' + state.reprintId + '/reprint', 'POST', payload); state.reprintRequest = null; close(); await refresh(); notice('补打任务已保存，生产数量未增加。'); return; }
    if (key === 'drawings-query') { state.drawingQuery = $('drawing-query').value.trim(); state.drawings = await api('/api/production-mobile/drawings?q=' + encodeURIComponent(state.drawingQuery)); state.chosen = {}; showPanel('drawings'); return; }
    if (key === 'obsolete') { var selected = state.drawings.filter(function (d) { return state.chosen[d.documentId]; }); modal('确认作废图档', '<p class="body">已选 ' + selected.length + ' 张图纸。作废将停止关联发布图纸的生产使用，包括后续报工、打印和补打。</p><textarea id="obsolete-reason" placeholder="作废原因及替代图纸说明" maxlength="120"></textarea><p class="muted">' + selected.map(function (d) { return esc(d.drawingNumber); }).join('、') + '</p>', button('返回', 'close') + button('确认作废', 'obsolete-submit', 'danger')); return; }
    if (key === 'obsolete-submit') { var reason = $('obsolete-reason').value.trim(); if (!reason) throw new Error('请填写作废原因。'); var selected = state.drawings.filter(function (d) { return state.chosen[d.documentId]; }); var done = [], failures = [];
      for (var i = 0; i < selected.length; i++) { try { await api('/api/documents/' + selected[i].documentId + '/obsolete', 'POST', { comment: reason }); done.push(selected[i].documentId); } catch (e) { failures.push(selected[i].drawingNumber + '：' + e.message); } }
      state.drawings = state.drawings.filter(function (d) { return done.indexOf(d.documentId) < 0; }); state.chosen = {}; close(); modal('图纸作废结果', '<p class="body">已成功作废 ' + done.length + ' 张。</p><p class="body">' + failures.map(esc).join('<br>') + '</p>'); return;
    }
  }
  async function run(key) { if (state.busy) return; state.busy = true; disableBusy(); try { await act(key); } catch (e) { notice(e.message || '操作未完成，请重试。'); } finally { state.busy = false; render(); restoreBusy(); } }
  document.addEventListener('click', function (e) { var target = e.target.closest('[data-act]'); if (target) run(target.getAttribute('data-act')); });
  document.addEventListener('change', function (e) { if (e.target.id === 'action') { state.action = availableActions()[Number(e.target.value)]; state.quantity = state.action.remainingQuantity; state.attempt = null; render(); } if (e.target.hasAttribute('data-document')) { state.chosen[e.target.getAttribute('data-document')] = e.target.checked; showPanel('drawings'); } if (e.target.id === 'control-choice') $('control-quantity').value = state.controlChoices[Number(e.target.value)].remainingQuantity; if (e.target.id === 'record-filter') { state.filter = e.target.value.trim(); showPanel('records'); } });
  var scanTimer, keyBuffer = '';
  document.addEventListener('keydown', function (e) { if (e.target.id === 'scan-code' && e.key === 'Enter') { clearTimeout(scanTimer); scanTimer = setTimeout(function () { run('scan'); }, 120); return; }
    if (state.session && !/INPUT|TEXTAREA|SELECT/.test(e.target.tagName) && $('overlay').hidden) { if (e.key.length === 1) { keyBuffer += e.key; clearTimeout(scanTimer); scanTimer = setTimeout(function () { keyBuffer = ''; }, 800); } else if (e.key === 'Enter' && keyBuffer) { var code = keyBuffer; keyBuffer = ''; window.productionScan(code); } } });
  window.productionScan = async function (code) { if (state.busy || !$('overlay').hidden) return; state.busy = true; disableBusy(); try { await scan(code); } catch (e) { notice(e.message); } finally { state.busy = false; render(); restoreBusy(); } };
  window.productionBack = function () { if (!$('overlay').hidden) close(); else { state.page = state.session ? 'scan' : 'login'; render(); } };
  setInterval(async function () {
    if (!state.session || state.busy) return;
    try {
      if (state.session.expiresAt && new Date(state.session.expiresAt).getTime() - Date.now() < 300000) {
        state.session = await api('/api/auth/resume', 'POST', { resumeToken: state.session.resumeToken, clientInstanceId: clientId, clientType: 'web' });
      } else await api('/api/auth/heartbeat', 'POST');
    } catch (_) { /* Current form is retained; the next operation still checks server authorization. */ }
  }, 60000);
  render();
})();
