/* ZungMail 升级服务管理页：无框架、无外部依赖，所有请求都走同源相对路径，支持反向代理子路径。 */
(() => {
  'use strict';

  const state = {
    releases: [],
    feedback: [],
    feedbackDetails: {},
    settings: null,
    session: null,
    uploadFile: null,
    busy: false
  };

  const $ = (id) => document.getElementById(id);

  const loginView = $('login-view');
  const mainView = $('main-view');
  const toastEl = $('toast');

  // ---------------- 基础工具 ----------------

  function escapeHtml(value) {
    return String(value ?? '')
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;');
  }

  function formatBytes(bytes) {
    const value = Number(bytes) || 0;
    if (value < 1024) return `${value} B`;
    const units = ['KB', 'MB', 'GB'];
    let size = value / 1024;
    let index = 0;
    while (size >= 1024 && index < units.length - 1) {
      size /= 1024;
      index++;
    }
    return `${size.toFixed(size >= 10 ? 0 : 1)} ${units[index]}`;
  }

  function formatTime(value) {
    if (!value) return '—';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '—';
    const pad = (n) => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  let toastTimer = 0;
  function toast(message, isError = false) {
    toastEl.textContent = message;
    toastEl.classList.toggle('error', isError);
    toastEl.classList.remove('hidden');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastEl.classList.add('hidden'), 3600);
  }

  function setHidden(el, hidden) {
    el.classList.toggle('hidden', hidden);
  }

  function showLogin() {
    setHidden(loginView, false);
    setHidden(mainView, true);
    $('login-password').value = '';
    $('login-password').focus();
  }

  function showMain() {
    setHidden(loginView, true);
    setHidden(mainView, false);
  }

  class UnauthorizedError extends Error {}

  async function api(path, options = {}) {
    const response = await fetch(path, {
      credentials: 'same-origin',
      ...options,
      headers: {
        ...(options.body instanceof FormData ? {} : { 'Content-Type': 'application/json' }),
        ...(options.headers || {})
      }
    });

    if (response.status === 401) {
      throw new UnauthorizedError('unauthorized');
    }

    const contentType = response.headers.get('content-type') || '';
    const payload = contentType.includes('application/json')
      ? await response.json().catch(() => null)
      : null;

    if (!response.ok) {
      const message = payload && (payload.error || payload.message)
        ? payload.error || payload.message
        : `请求失败（HTTP ${response.status}）`;
      const error = new Error(message);
      error.status = response.status;
      throw error;
    }

    return payload;
  }

  // ---------------- 登录 ----------------

  $('login-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const button = $('login-submit');
    const errorEl = $('login-error');
    setHidden(errorEl, true);
    button.disabled = true;
    button.textContent = '登录中…';
    try {
      state.session = await api('api/admin/session', {
        method: 'POST',
        body: JSON.stringify({ password: $('login-password').value })
      });
      showMain();
      showView('releases');
      await Promise.all([loadReleases(), loadSettings(), loadFeedback()]);
    } catch (error) {
      if (error instanceof UnauthorizedError) {
        errorEl.textContent = '口令不正确，或错误次数过多被暂时锁定。';
      } else {
        errorEl.textContent = error.message;
      }
      setHidden(errorEl, false);
    } finally {
      button.disabled = false;
      button.textContent = '登录';
    }
  });

  $('logout-button').addEventListener('click', async () => {
    try {
      await fetch('api/admin/session', { method: 'DELETE', credentials: 'same-origin' });
    } catch {
      /* 网络失败也要回到登录页 */
    }
    state.session = null;
    showLogin();
  });

  // ---------------- 数据加载 ----------------

  async function loadReleases() {
    try {
      const data = await api('api/admin/releases');
      state.releases = data?.releases || [];
      renderReleases();
    } catch (error) {
      if (error instanceof UnauthorizedError) return showLogin();
      toast(error.message, true);
    }
  }

  async function loadSettings() {
    try {
      state.settings = await api('api/admin/settings');
      renderSettings();
    } catch (error) {
      if (error instanceof UnauthorizedError) return showLogin();
      toast(error.message, true);
    }
  }

  function renderSettings() {
    const settings = state.settings;
    if (!settings) return;

    $('data-directory').textContent = settings.dataDirectory || '—';
    const sourceText = {
      environment: '环境变量 UPDATESERVER_ADMIN_PASSWORD',
      stored: '数据目录 admin.json（可在下方修改）',
      generated: '首次启动自动生成'
    }[settings.passwordSource] || settings.passwordSource || '—';
    $('password-source').textContent = sourceText;
    $('max-upload').textContent = formatBytes(settings.maxUploadBytes);

    const clientUrl = location.origin + location.pathname.replace(/[^/]*$/, '').replace(/\/$/, '');
    $('client-url').value = clientUrl;
    $('check-url').textContent = settings.checkUrlHint || '';

    const hint = $('initial-password-hint');
    if (settings.initialPasswordFile) {
      hint.textContent = `初始口令文件：${settings.initialPasswordFile}（登录后请立即修改口令，文件会自动删除）`;
      setHidden(hint, false);
    } else {
      setHidden(hint, true);
    }

    const canChange = settings.canChangePassword !== false;
    $('password-current').disabled = !canChange;
    $('password-new').disabled = !canChange;
    $('password-submit').disabled = !canChange;
    $('password-note').textContent = canChange
      ? '口令使用 PBKDF2-SHA256 哈希保存在数据目录，修改后所有会话立即失效。'
      : '当前口令来自环境变量，请在部署配置里修改后重启服务。';
  }

  function statusBadge(release) {
    if (!release.published) return '<span class="badge draft">草稿</span>';
    if (!release.fileExists) return '<span class="badge warn">文件缺失</span>';
    return '<span class="badge ok">已发布</span>';
  }

  function renderReleases() {
    const container = $('releases');
    const empty = $('releases-empty');
    $('release-count').textContent = state.releases.length ? `${state.releases.length} 个版本` : '';

    if (!state.releases.length) {
      container.innerHTML = '';
      setHidden(empty, false);
      return;
    }

    setHidden(empty, true);
    container.innerHTML = state.releases.map((release) => {
      const mandatory = release.mandatory
        ? '<span class="badge warn">强制更新</span>'
        : '';
      const minVersion = release.minVersion
        ? `<span>最低版本 ${escapeHtml(release.minVersion)}</span>`
        : '';
      const notes = release.notes
        ? `<div class="release-notes">${escapeHtml(release.notes)}</div>`
        : '';
      const publishAction = release.published
        ? `<button class="button ghost" data-action="unpublish" data-id="${escapeHtml(release.id)}">下线</button>`
        : `<button class="button primary" data-action="publish" data-id="${escapeHtml(release.id)}" ${release.fileExists ? '' : 'disabled'}>发布</button>`;

      return `
        <article class="release-item">
          <div class="release-top">
            <div>
              <div class="release-title">
                <strong>v${escapeHtml(release.version)}</strong>
                <span class="badge neutral">${escapeHtml(release.channel)}</span>
                ${statusBadge(release)}
                ${mandatory}
              </div>
              <div class="release-meta">
                <span>${escapeHtml(release.fileName)}</span>
                <span>${formatBytes(release.size)}</span>
                <span>上传 ${formatTime(release.createdAt)}</span>
                ${release.publishedAt ? `<span>发布 ${formatTime(release.publishedAt)}</span>` : ''}
                <span>下载 ${release.downloadCount || 0} 次</span>
                ${minVersion}
              </div>
            </div>
          </div>
          ${notes}
          <div class="release-actions">
            <button class="button ghost" data-action="edit" data-id="${escapeHtml(release.id)}">编辑</button>
            ${release.published ? `<a class="button ghost" href="${escapeHtml(release.downloadUrl)}" target="_blank" rel="noreferrer">下载</a>` : ''}
            ${publishAction}
            <button class="button danger" data-action="delete" data-id="${escapeHtml(release.id)}">删除</button>
          </div>
        </article>`;
    }).join('');
  }

  $('releases').addEventListener('click', async (event) => {
    const button = event.target.closest('button[data-action]');
    if (!button) return;

    const id = button.dataset.id;
    const action = button.dataset.action;
    const release = state.releases.find((item) => item.id === id);
    if (!release) return;

    if (action === 'edit') {
      openEdit(release);
      return;
    }

    if (action === 'delete') {
      if (!confirm(`删除 v${release.version}（${release.channel}）？安装包文件也会一并删除，且不可恢复。`)) return;
      button.disabled = true;
      try {
        await api(`api/admin/releases/${encodeURIComponent(id)}`, { method: 'DELETE' });
        toast(`已删除 v${release.version}`);
        await loadReleases();
      } catch (error) {
        if (error instanceof UnauthorizedError) return showLogin();
        toast(error.message, true);
        button.disabled = false;
      }
      return;
    }

    if (action === 'publish' || action === 'unpublish') {
      button.disabled = true;
      try {
        await api(`api/admin/releases/${encodeURIComponent(id)}/${action}`, { method: 'POST' });
        toast(action === 'publish' ? `v${release.version} 已发布` : `v${release.version} 已下线`);
        await loadReleases();
      } catch (error) {
        if (error instanceof UnauthorizedError) return showLogin();
        toast(error.message, true);
        button.disabled = false;
      }
    }
  });

  // ---------------- 页签 ----------------

  const views = {
    releases: $('view-releases'),
    feedback: $('view-feedback')
  };

  function showView(name) {
    Object.entries(views).forEach(([key, el]) => setHidden(el, key !== name));
    document.querySelectorAll('#main-tabs .tab').forEach((tab) => {
      tab.classList.toggle('active', tab.dataset.view === name);
    });
    if (name === 'feedback') loadFeedback();
  }

  $('main-tabs').addEventListener('click', (event) => {
    const tab = event.target.closest('.tab');
    if (tab) showView(tab.dataset.view);
  });

  // ---------------- 用户反馈 ----------------

  async function loadFeedback() {
    try {
      const data = await api('api/admin/feedback');
      state.feedback = data?.items || [];
      renderFeedback();
    } catch (error) {
      if (error instanceof UnauthorizedError) return showLogin();
      toast(error.message, true);
    }
  }

  function renderFeedback() {
    const container = $('feedback-list');
    const empty = $('feedback-empty');
    const countBadge = $('feedback-count');

    if (state.feedback.length) {
      countBadge.textContent = String(state.feedback.length);
      setHidden(countBadge, false);
    } else {
      setHidden(countBadge, true);
    }

    if (!state.feedback.length) {
      container.innerHTML = '';
      setHidden(empty, false);
      return;
    }

    setHidden(empty, true);
    container.innerHTML = state.feedback.map((item) => {
      const contact = item.contact
        ? `<span>联系 ${escapeHtml(item.contact)}</span>`
        : '<span>未留联系方式</span>';
      const log = item.hasLog
        ? `<span>日志 ${formatBytes(item.logBytes)}${item.logTruncated ? '（已截断）' : ''}</span>`
        : '<span>无日志</span>';

      return `
        <article class="feedback-item" data-id="${escapeHtml(item.id)}">
          <div class="feedback-title">
            <span class="badge neutral">${escapeHtml(item.categoryLabel || item.category)}</span>
            <strong>${escapeHtml(item.id)}</strong>
          </div>
          <div class="feedback-meta">
            <span>${formatTime(item.createdAt)}</span>
            <span>客户端 ${escapeHtml(item.clientVersion || '未知')}${item.channel ? `（${escapeHtml(item.channel)}）` : ''}</span>
            ${contact}
            ${log}
          </div>
          <div class="feedback-preview">${escapeHtml(item.descriptionPreview)}</div>
          <div class="feedback-actions">
            <button class="button ghost" data-action="toggle" data-id="${escapeHtml(item.id)}">查看详情</button>
            <button class="button danger" data-action="delete" data-id="${escapeHtml(item.id)}">删除</button>
          </div>
          <div class="feedback-detail hidden" data-detail="${escapeHtml(item.id)}"></div>
        </article>`;
    }).join('');
  }

  function renderFeedbackDetail(detail) {
    const environment = detail.environment
      ? `<div class="text-block">${escapeHtml(detail.environment)}</div>`
      : '<p class="muted small">未附加系统环境。</p>';
    // 日志用同源相对地址下载：浏览器会带上管理会话 Cookie，文件名就是反馈编号
    const logFile = `api/admin/feedback/${encodeURIComponent(detail.id)}/log`;
    const log = detail.log
      ? `<div class="detail-head">
           <h3>运行日志${detail.logTruncated ? '（已截断）' : ''}</h3>
           <div class="detail-actions">
             <a class="button ghost" href="${logFile}" download="${escapeHtml(detail.id)}.log">下载日志</a>
             <button class="button ghost" data-action="copy-log" data-id="${escapeHtml(detail.id)}">复制日志</button>
           </div>
         </div>
         <pre class="log-block">${escapeHtml(detail.log)}</pre>`
      : '<p class="muted small">未附加日志。</p>';

    return `
      <div class="detail-head">
        <h3>问题描述</h3>
        <span class="muted small">${formatTime(detail.createdAt)}</span>
      </div>
      <div class="text-block">${escapeHtml(detail.description)}</div>
      <h3>系统环境</h3>
      ${environment}
      ${log}`;
  }

  $('feedback-list').addEventListener('click', async (event) => {
    const button = event.target.closest('button[data-action]');
    if (!button) return;

    const id = button.dataset.id;
    const action = button.dataset.action;

    if (action === 'copy-log') {
      const detail = state.feedbackDetails[id];
      if (!detail?.log) return;
      await copyText(detail.log);
      toast('日志已复制');
      return;
    }

    if (action === 'delete') {
      if (!confirm(`删除反馈 ${id}？描述与日志文件都会一并删除，且不可恢复。`)) return;
      button.disabled = true;
      try {
        await api(`api/admin/feedback/${encodeURIComponent(id)}`, { method: 'DELETE' });
        delete state.feedbackDetails[id];
        toast(`已删除 ${id}`);
        await loadFeedback();
      } catch (error) {
        if (error instanceof UnauthorizedError) return showLogin();
        toast(error.message, true);
        button.disabled = false;
      }
      return;
    }

    if (action === 'toggle') {
      const box = document.querySelector(`.feedback-detail[data-detail="${CSS.escape(id)}"]`);
      if (!box) return;

      if (!box.classList.contains('hidden')) {
        box.classList.add('hidden');
        button.textContent = '查看详情';
        return;
      }

      box.classList.remove('hidden');
      button.textContent = '收起详情';

      if (state.feedbackDetails[id]) {
        box.innerHTML = renderFeedbackDetail(state.feedbackDetails[id]);
        return;
      }

      box.innerHTML = '<p class="muted small">正在读取…</p>';
      try {
        const detail = await api(`api/admin/feedback/${encodeURIComponent(id)}`);
        state.feedbackDetails[id] = detail;
        box.innerHTML = renderFeedbackDetail(detail);
      } catch (error) {
        if (error instanceof UnauthorizedError) return showLogin();
        box.innerHTML = `<p class="error-text">${escapeHtml(error.message)}</p>`;
      }
    }
  });

  async function copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      const area = document.createElement('textarea');
      area.value = text;
      document.body.appendChild(area);
      area.select();
      document.execCommand('copy');
      area.remove();
    }
  }

  // ---------------- 上传 ----------------

  const dropZone = $('drop-zone');
  const fileInput = $('upload-file');

  function setUploadFile(file) {
    state.uploadFile = file || null;
    const title = $('drop-title');
    const detail = $('drop-detail');
    if (!file) {
      dropZone.classList.remove('has-file');
      title.textContent = '把安装包拖到这里，或点击选择文件';
      detail.textContent = '';
      return;
    }

    dropZone.classList.add('has-file');
    title.textContent = file.name;
    detail.textContent = formatBytes(file.size);

    // 从 ZungMail-1.1.0-win-x64-setup.exe 里带出版本号，减少手填错误
    const match = file.name.match(/(\d+\.\d+\.\d+(?:\.\d+)?)/);
    if (match && !$('upload-version').value.trim()) {
      $('upload-version').value = match[1];
    }
  }

  dropZone.addEventListener('click', () => fileInput.click());
  fileInput.addEventListener('change', () => setUploadFile(fileInput.files?.[0] || null));

  ['dragenter', 'dragover'].forEach((name) => {
    dropZone.addEventListener(name, (event) => {
      event.preventDefault();
      dropZone.classList.add('dragging');
    });
  });
  ['dragleave', 'drop'].forEach((name) => {
    dropZone.addEventListener(name, (event) => {
      event.preventDefault();
      dropZone.classList.remove('dragging');
    });
  });
  dropZone.addEventListener('drop', (event) => {
    const file = event.dataTransfer?.files?.[0];
    if (file) setUploadFile(file);
  });

  $('upload-form').addEventListener('submit', (event) => {
    event.preventDefault();
    if (state.busy) return;

    const file = state.uploadFile;
    const version = $('upload-version').value.trim();
    const errorEl = $('upload-error');
    setHidden(errorEl, true);

    if (!file) {
      errorEl.textContent = '请先选择安装包文件。';
      setHidden(errorEl, false);
      return;
    }
    if (!/^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z.-]+)?$/.test(version)) {
      errorEl.textContent = '版本号格式不正确，示例：1.1.0 或 1.1.0-beta.1。';
      setHidden(errorEl, false);
      return;
    }

    const form = new FormData();
    form.append('file', file);
    form.append('version', version);
    form.append('channel', $('upload-channel').value);
    form.append('notes', $('upload-notes').value);
    form.append('mandatory', $('upload-mandatory').checked ? 'true' : 'false');
    form.append('minVersion', $('upload-min-version').value.trim());

    state.busy = true;
    const button = $('upload-submit');
    button.disabled = true;
    button.textContent = '上传中…';
    const progressWrap = $('upload-progress-wrap');
    const progressBar = $('upload-progress-bar');
    const progressText = $('upload-progress-text');
    progressBar.style.width = '0%';
    progressText.textContent = '准备上传…';
    setHidden(progressWrap, false);

    const xhr = new XMLHttpRequest();
    xhr.open('POST', 'api/admin/releases');
    xhr.withCredentials = true;

    xhr.upload.addEventListener('progress', (progressEvent) => {
      if (!progressEvent.lengthComputable) return;
      const percent = Math.round((progressEvent.loaded / progressEvent.total) * 100);
      progressBar.style.width = `${percent}%`;
      progressText.textContent = `上传中 ${percent}%（${formatBytes(progressEvent.loaded)} / ${formatBytes(progressEvent.total)}）`;
    });

    xhr.addEventListener('load', async () => {
      finish();
      if (xhr.status === 401) {
        showLogin();
        return;
      }

      let payload = null;
      try {
        payload = JSON.parse(xhr.responseText);
      } catch {
        payload = null;
      }

      if (xhr.status >= 200 && xhr.status < 300) {
        toast(`v${payload?.version || version} 已保存为草稿，确认后点「发布」`);
        $('upload-form').reset();
        $('upload-version').value = '';
        setUploadFile(null);
        await loadReleases();
      } else {
        errorEl.textContent = payload?.error || `上传失败（HTTP ${xhr.status}）`;
        setHidden(errorEl, false);
      }
    });

    xhr.addEventListener('error', () => {
      finish();
      errorEl.textContent = '网络中断，上传失败。请检查服务是否仍在运行。';
      setHidden(errorEl, false);
    });

    xhr.addEventListener('abort', () => {
      finish();
      errorEl.textContent = '上传已取消。';
      setHidden(errorEl, false);
    });

    xhr.send(form);

    function finish() {
      state.busy = false;
      button.disabled = false;
      button.textContent = '上传为草稿';
      setHidden(progressWrap, true);
      progressBar.style.width = '0%';
    }
  });

  // ---------------- 编辑弹窗 ----------------

  const editOverlay = $('edit-overlay');

  function openEdit(release) {
    $('edit-id').value = release.id;
    $('edit-version').value = release.version;
    $('edit-channel').value = release.channel === 'beta' ? 'beta' : 'stable';
    $('edit-notes').value = release.notes || '';
    $('edit-min-version').value = release.minVersion || '';
    $('edit-mandatory').checked = !!release.mandatory;
    setHidden($('edit-error'), true);
    setHidden(editOverlay, false);
    $('edit-version').focus();
  }

  function closeEdit() {
    setHidden(editOverlay, true);
  }

  $('edit-cancel').addEventListener('click', closeEdit);
  $('edit-cancel-x').addEventListener('click', closeEdit);
  editOverlay.addEventListener('click', (event) => {
    if (event.target === editOverlay) closeEdit();
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !editOverlay.classList.contains('hidden')) closeEdit();
  });

  $('edit-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const id = $('edit-id').value;
    const errorEl = $('edit-error');
    setHidden(errorEl, true);
    try {
      await api(`api/admin/releases/${encodeURIComponent(id)}`, {
        method: 'PUT',
        body: JSON.stringify({
          version: $('edit-version').value.trim(),
          channel: $('edit-channel').value,
          notes: $('edit-notes').value,
          mandatory: $('edit-mandatory').checked,
          minVersion: $('edit-min-version').value.trim()
        })
      });
      closeEdit();
      toast('版本信息已更新');
      await loadReleases();
    } catch (error) {
      if (error instanceof UnauthorizedError) return showLogin();
      errorEl.textContent = error.message;
      setHidden(errorEl, false);
    }
  });

  // ---------------- 侧栏 ----------------

  $('copy-url').addEventListener('click', async () => {
    const value = $('client-url').value;
    try {
      await navigator.clipboard.writeText(value);
      toast('客户端地址已复制');
    } catch {
      $('client-url').select();
      document.execCommand('copy');
      toast('客户端地址已复制');
    }
  });

  $('refresh-button').addEventListener('click', () => {
    loadReleases();
    loadSettings();
    loadFeedback();
  });

  $('password-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const errorEl = $('password-error');
    const okEl = $('password-ok');
    setHidden(errorEl, true);
    setHidden(okEl, true);
    try {
      const result = await api('api/admin/password', {
        method: 'POST',
        body: JSON.stringify({
          currentPassword: $('password-current').value,
          newPassword: $('password-new').value
        })
      });
      okEl.textContent = result?.message || '口令已更新，请重新登录。';
      setHidden(okEl, false);
      $('password-form').reset();
      setTimeout(showLogin, 1200);
    } catch (error) {
      if (error instanceof UnauthorizedError) return showLogin();
      errorEl.textContent = error.message;
      setHidden(errorEl, false);
    }
  });

  // ---------------- 启动 ----------------

  (async () => {
    try {
      state.session = await api('api/admin/session');
      if (state.session?.authenticated) {
        showMain();
        showView('releases');
        await Promise.all([loadReleases(), loadSettings(), loadFeedback()]);
        return;
      }
    } catch {
      /* 未登录或服务刚启动，走登录页 */
    }
    showLogin();
  })();
})();
