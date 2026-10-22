/* 邮件富文本编辑器：基于 contenteditable 的轻量实现，产出内联样式 HTML，兼容主流邮件客户端。 */
(function () {
  'use strict';

  var editor = document.getElementById('editor');
  var bridge = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;

  var blockFormat = document.getElementById('blockFormat');
  var fontFamily = document.getElementById('fontFamily');
  var fontSize = document.getElementById('fontSize');
  var lineHeight = document.getElementById('lineHeight');
  var paraSpacing = document.getElementById('paraSpacing');

  var linkPop = document.getElementById('linkPop');
  var tablePop = document.getElementById('tablePop');
  var emojiPop = document.getElementById('emojiPop');
  var attachPop = document.getElementById('attachPop');
  var attachList = document.getElementById('attachList');
  var ctxMenu = document.getElementById('ctxMenu');
  var findBar = document.getElementById('findBar');
  var findInput = document.getElementById('findInput');
  var replaceInput = document.getElementById('replaceInput');
  var findCount = document.getElementById('findCount');
  var wordCount = document.getElementById('wordCount');
  var toastBox = document.getElementById('toast');

  var attachments = [];
  var savedRange = null;
  var changeTimer = 0;
  var toastsTimer = 0;

  // ---------------- 宿主通信 ----------------

  function post(message) {
    if (bridge) {
      bridge.postMessage(message);
    }
  }

  function notifyChange(delay) {
    clearTimeout(changeTimer);
    changeTimer = setTimeout(function () {
      post({ type: 'change', html: toEmailHtml() });
    }, delay === undefined ? 260 : delay);
  }

  // ---------------- 选区保存与恢复 ----------------

  function inEditor(node) {
    return node && editor.contains(node);
  }

  document.addEventListener('selectionchange', function () {
    var sel = window.getSelection();
    if (sel && sel.rangeCount > 0 && inEditor(sel.anchorNode)) {
      savedRange = sel.getRangeAt(0).cloneRange();
      updateToolbarState();
    }
  });

  function restoreSelection() {
    editor.focus();
    if (!savedRange) return;
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(savedRange);
  }

  function selectionElement() {
    var sel = window.getSelection();
    if (!sel || !sel.anchorNode || !inEditor(sel.anchorNode)) return null;
    return sel.anchorNode.nodeType === 1 ? sel.anchorNode : sel.anchorNode.parentElement;
  }

  // ---------------- 命令执行 ----------------

  function exec(cmd, value) {
    restoreSelection();
    try {
      document.execCommand(cmd, false, value === undefined ? null : value);
    } catch (e) {
      // 命令不被支持时静默忽略，不打断输入
    }
    editor.focus();
    notifyChange();
  }

  function clearFormat() {
    restoreSelection();
    try {
      document.execCommand('removeFormat', false, null);
    } catch (e) { /* ignore */ }
    notifyChange();
  }

  // 字号：execCommand 只支持 1-7，先落到 size=7 再改写为精确 px，保留撤销栈。
  function applyFontSize(px) {
    restoreSelection();
    try {
      document.execCommand('fontSize', false, '7');
    } catch (e) {
      return;
    }

    editor.querySelectorAll('font[size="7"]').forEach(function (node) {
      wrapWithSpanSize(node, px);
    });

    // styleWithCSS 为 true 时部分版本生成 xxx-large 行内样式
    editor.querySelectorAll('span').forEach(function (span) {
      if (span.style && span.style.fontSize === 'xxx-large') wrapWithSpanSize(span, px);
    });

    editor.focus();
    notifyChange();
  }

  function wrapWithSpanSize(node, px) {
    var span = document.createElement('span');
    span.style.fontSize = px + 'px';
    while (node.firstChild) span.appendChild(node.firstChild);
    node.replaceWith(span);
  }

  // 行距 / 段距：作用于选区覆盖的顶层块级元素
  function applyBlockStyle(property, value) {
    restoreSelection();
    var blocks = selectedBlocks();
    blocks.forEach(function (block) {
      block.style[property] = value;
    });
    editor.focus();
    notifyChange();
  }

  function selectedBlocks() {
    var sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return [editor];

    var range = sel.getRangeAt(0);
    var all = Array.prototype.slice.call(
      editor.querySelectorAll('p,div,li,td,th,h1,h2,h3,h4,h5,h6,blockquote,pre'));

    var picked = all.filter(function (el) {
      try {
        return range.intersectsNode(el);
      } catch (e) {
        return false;
      }
    });

    // 只保留最外层块，避免同时给父子节点写同样的样式
    picked = picked.filter(function (el) {
      var parent = el.parentElement;
      while (parent && parent !== editor) {
        if (picked.indexOf(parent) >= 0) return false;
        parent = parent.parentElement;
      }
      return true;
    });

    return picked.length ? picked : [editor];
  }

  // ---------------- 待办列表 ----------------

  function insertTodo() {
    restoreSelection();
    var html =
      '<ul class="todo-list"><li>' +
      '<input type="checkbox" contenteditable="false">' +
      '<span class="todo-text">待办事项</span></li></ul><p><br></p>';
    document.execCommand('insertHTML', false, html);

    var lists = editor.querySelectorAll('ul.todo-list');
    var list = lists[lists.length - 1];
    if (list) {
      applyTodoStyles(list);
      var text = list.querySelector('.todo-text');
      if (text) placeCaretAtEnd(text);
    }
    notifyChange();
  }

  // execCommand('insertHTML') 会丢弃部分内联样式，插入后统一补回，
  // 保证导出的邮件在 Outlook / Foxmail 中与编辑时视觉一致
  function applyTodoStyles(list) {
    list.style.cssText = 'list-style:none;padding-left:2px;margin:6px 0;';
    Array.prototype.forEach.call(list.querySelectorAll('li'), function (li) {
      li.style.margin = '4px 0';
    });
    Array.prototype.forEach.call(list.querySelectorAll('input[type="checkbox"]'), function (box) {
      box.style.cssText = 'margin-right:6px;vertical-align:-2px;';
    });
  }

  function caretListItem() {
    var sel = window.getSelection();
    if (!sel || !sel.anchorNode) return null;
    var node = sel.anchorNode;
    if (node.nodeType === 3) node = node.parentElement;
    return node ? node.closest('li') : null;
  }

  function isTodoItem(li) {
    return !!(li && li.parentElement && li.parentElement.classList.contains('todo-list'));
  }

  function newTodoItem() {
    var li = document.createElement('li');
    li.style.margin = '4px 0';
    li.innerHTML = '<input type="checkbox" contenteditable="false" style="margin-right:6px;vertical-align:-2px;">' +
      '<span class="todo-text"><br></span>';
    return li;
  }

  // ---------------- 插入能力 ----------------

  function insertTable(rows, cols, withHeader) {
    restoreSelection();
    var html = '<table border="1" cellspacing="0" cellpadding="6" ' +
      'style="border-collapse:collapse;width:100%;margin:8px 0;font-size:13px;">';
    for (var r = 0; r < rows; r++) {
      html += '<tr>';
      for (var c = 0; c < cols; c++) {
        var isHead = withHeader && r === 0;
        var tag = isHead ? 'th' : 'td';
        var extra = isHead ? 'background-color:#F6F8FC;font-weight:600;text-align:left;' : '';
        html += '<' + tag + ' style="border:1px solid #C9D3E0;padding:6px 8px;' + extra + '"><br></' + tag + '>';
      }
      html += '</tr>';
    }
    html += '</table><p><br></p>';

    document.execCommand('insertHTML', false, html);

    var tables = editor.querySelectorAll('table');
    var table = tables[tables.length - 1];
    if (table) {
      // execCommand('insertHTML') 会丢弃 table 上的 border-collapse，这里补回，
      // 保证导出到 Outlook / Foxmail 时边框为单线而不是双线
      table.style.borderCollapse = 'collapse';

      var cell = table.querySelector('th,td');
      if (cell) {
        cell.innerHTML = '';
        placeCaretAtEnd(cell);
      }
    }
    notifyChange();
  }

  function normalizeUrl(raw) {
    var url = (raw || '').trim();
    if (!url) return '';
    if (/^(https?:|mailto:|tel:)/i.test(url)) return url;
    if (/^\/\//.test(url)) return 'https:' + url;
    if (/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(url)) return 'mailto:' + url;
    return 'https://' + url;
  }

  function escapeHtml(text) {
    return String(text)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function insertLink(url, text) {
    restoreSelection();
    var sel = window.getSelection();
    var label = (text || '').trim();
    if (label) {
      document.execCommand('insertHTML', false,
        '<a href="' + escapeHtml(url) + '" style="color:#2563EB;text-decoration:underline;">' +
        escapeHtml(label) + '</a>');
    } else if (sel && !sel.isCollapsed) {
      document.execCommand('createLink', false, url);
      editor.querySelectorAll('a[href="' + url.replace(/"/g, '&quot;') + '"]').forEach(function (a) {
        a.style.color = '#2563EB';
        a.style.textDecoration = 'underline';
      });
    } else {
      document.execCommand('insertHTML', false,
        '<a href="' + escapeHtml(url) + '" style="color:#2563EB;text-decoration:underline;">' +
        escapeHtml(url) + '</a>');
    }
    notifyChange();
  }

  function insertEmoji(char) {
    restoreSelection();
    document.execCommand('insertText', false, char);
    notifyChange();
  }

  // 记录插入前已有的节点，插入后与其做差集即可拿到新插入的内容
  function captureElements(selector) {
    return Array.prototype.slice.call(editor.querySelectorAll(selector));
  }

  function newElements(selector, before) {
    return Array.prototype.filter.call(editor.querySelectorAll(selector), function (el) {
      return before.indexOf(el) < 0;
    });
  }

  function insertImage(dataUrl) {
    restoreSelection();
    var before = captureElements('img');
    var html = '<img src="' + escapeHtml(dataUrl) + '" ' +
      'style="max-width:100%;height:auto;display:block;margin:6px 0;">';

    var inserted = false;
    try {
      // 走 execCommand 才能进入撤销栈
      inserted = document.execCommand('insertHTML', false, html);
    } catch (e) {
      inserted = false;
    }

    var images = newElements('img', before);
    var image = images[images.length - 1];

    if (!inserted && !image) {
      image = document.createElement('img');
      image.src = dataUrl;
      image.style.cssText = 'max-width:100%;height:auto;display:block;margin:6px 0;';
      insertNodeAtCaret(image);
    }

    if (image) fitImage(image);
    notifyChange();
  }

  // Outlook 不支持 max-width，大图会撑破版心；落 width/height 属性才能被邮件客户端正确缩放
  function fitImage(image) {
    function apply() {
      var width = image.naturalWidth;
      var height = image.naturalHeight;
      if (!width || !height) return;

      var limit = Math.min(600, Math.max(320, editor.clientWidth || 0));
      if (width > limit) {
        height = Math.round(height * limit / width);
        width = limit;
      }

      image.setAttribute('width', String(width));
      image.setAttribute('height', String(height));
      notifyChange(0);
    }

    if (image.complete && image.naturalWidth) apply();
    else image.addEventListener('load', apply, { once: true });
  }

  // 直接插入 DOM 节点，避免 execCommand('insertHTML') 丢属性、再事后补样式的做法
  function insertNodeAtCaret(node) {
    try {
      var sel = window.getSelection();
      var range = sel && sel.rangeCount ? sel.getRangeAt(0) : null;
      if (range && inEditor(range.startContainer)) {
        range.deleteContents();
        range.insertNode(node);
        range.setStartAfter(node);
        range.collapse(true);
        sel.removeAllRanges();
        sel.addRange(range);
        savedRange = range.cloneRange();
        return;
      }
    } catch (e) { /* 选区失效时落到末尾插入 */ }

    editor.appendChild(node);
  }

  function insertAttachmentTag(name) {
    restoreSelection();
    var html = '<span class="mail-attach" contenteditable="false">📎 ' + escapeHtml(name) + '</span>&nbsp;';
    document.execCommand('insertHTML', false, html);

    var tags = editor.querySelectorAll('.mail-attach');
    var tag = tags[tags.length - 1];
    if (tag) {
      tag.style.cssText = 'display:inline-block;padding:2px 8px;border:1px solid #D7DEE9;' +
        'border-radius:6px;background-color:#F6F8FC;color:#46526B;font-size:12px;';
    }
    notifyChange();
  }

  function placeCaretAtEnd(node) {
    var range = document.createRange();
    range.selectNodeContents(node);
    range.collapse(false);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    editor.focus();
    savedRange = range.cloneRange();
  }

  // 回复 / 转发的正文以引用块开头时，光标要落在引用**上方**的那一行，
  // 否则一打开窗口就停在几十行原文的末尾，得先滚回去才能写。
  function placeCaretAtStart(node) {
    var target = node.firstChild;
    var range = document.createRange();
    if (target && target.nodeType === 1 && !target.textContent.trim()) {
      range.setStart(target, 0);
    } else {
      range.setStart(node, 0);
    }
    range.collapse(true);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    editor.focus();
    savedRange = range.cloneRange();
  }

  // ---------------- 签名 ----------------

  // 签名块用 data-signature 标记，切换发件账户时整块替换。
  // 上面留一个空段落当书写位，光标放进去，用户接着输入时不会继承签名的加粗/颜色。
  // 回复 / 转发的引用原文是 .mc-quote 容器：签名要插在引用**前面**，
  // 否则长长的引用会把签名顶到最底部，用户以为「没应用签名」。
  var currentSignature = '';

  function applySignature(html) {
    currentSignature = String(html == null ? '' : html).trim();
    ['[data-signature]', '[data-signature-spacer]'].forEach(function (selector) {
      Array.prototype.slice.call(editor.querySelectorAll(selector)).forEach(function (node) {
        node.parentNode.removeChild(node);
      });
    });

    var value = String(html == null ? '' : html).trim();
    if (!value) {
      updateWordCount();
      notifyChange(0);
      return;
    }

    var spacer = document.createElement('p');
    spacer.setAttribute('data-signature-spacer', '1');
    spacer.appendChild(document.createElement('br'));

    var block = document.createElement('div');
    block.setAttribute('data-signature', '1');
    block.innerHTML = value;

    var quote = editor.querySelector('.mc-quote');
    if (quote && quote.parentNode) {
      quote.parentNode.insertBefore(spacer, quote);
      quote.parentNode.insertBefore(block, quote);
    } else {
      editor.appendChild(spacer);
      editor.appendChild(block);
    }

    placeCaretAtEnd(spacer);
    updateWordCount();
    notifyChange(0);
  }

  // ---------------- 表情 ----------------

  var EMOJIS = ('😀 😃 😄 😁 😆 😅 🤣 😂 🙂 🙃 😉 😊 😇 🥰 😍 🤩 😘 😗 😚 😙 ' +
    '😋 😛 😜 🤪 😝 🤗 🤔 🤨 😐 😑 😶 🙄 😏 😣 😥 😮 🤐 😯 😪 😴 😌 😛 ' +
    '😷 🤒 🤕 🤢 🤮 🥵 🥶 😵 🤯 🤠 🥳 😎 🤓 🧐 👍 👎 👏 🙏 💪 🤝 ✌️ ' +
    '❤️ 💔 💯 ✅ ❌ ⭐ 🔥 🎉 📎 📅 📌 💡').split(/\s+/);

  function buildEmojiGrid() {
    var grid = document.createElement('div');
    grid.className = 'emoji-grid';
    EMOJIS.forEach(function (emoji) {
      var button = document.createElement('button');
      button.type = 'button';
      button.textContent = emoji;
      button.addEventListener('mousedown', function (e) { e.preventDefault(); });
      button.addEventListener('click', function () {
        insertEmoji(emoji);
        hidePopovers();
      });
      grid.appendChild(button);
    });
    emojiPop.appendChild(grid);
  }

  // ---------------- 附件标识 ----------------

  function renderAttachments() {
    attachList.innerHTML = '';
    if (!attachments.length) {
      var empty = document.createElement('div');
      empty.className = 'attach-empty';
      empty.textContent = '暂无附件，可先添加再插入标识。';
      attachList.appendChild(empty);
      return;
    }

    attachments.forEach(function (name) {
      var button = document.createElement('button');
      button.type = 'button';
      button.innerHTML = '<span class="clip">📎</span><span class="name"></span>';
      button.querySelector('.name').textContent = name;
      button.addEventListener('mousedown', function (e) { e.preventDefault(); });
      button.addEventListener('click', function () {
        insertAttachmentTag(name);
        hidePopovers();
      });
      attachList.appendChild(button);
    });
  }

  // ---------------- 弹层 ----------------

  function hidePopovers() {
    [linkPop, tablePop, emojiPop, attachPop].forEach(function (pop) {
      pop.hidden = true;
    });
  }

  function showPopover(pop, anchor) {
    hidePopovers();
    pop.hidden = false;

    var rect = anchor.getBoundingClientRect();
    var width = pop.offsetWidth;
    var height = pop.offsetHeight;
    var left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8));
    var top = rect.bottom + 6;
    if (top + height > window.innerHeight - 8) {
      top = Math.max(8, rect.top - height - 6);
    }
    pop.style.left = left + 'px';
    pop.style.top = top + 'px';
  }

  document.addEventListener('mousedown', function (e) {
    if (e.target.closest && e.target.closest('.popover')) return;
    if (e.target.closest && e.target.closest('[data-action],[data-cmd],.select,.color-btn')) return;
    hidePopovers();
    hideContextMenu();
  });

  document.querySelectorAll('[data-pop-close]').forEach(function (button) {
    button.addEventListener('click', hidePopovers);
  });

  // ---------------- 查找替换 ----------------

  var matchRanges = [];
  var matchIndex = -1;

  function clearHighlights() {
    if (window.CSS && CSS.highlights) {
      CSS.highlights.delete('find-all');
      CSS.highlights.delete('find-current');
    }
    matchRanges = [];
    matchIndex = -1;
    findCount.textContent = '0/0';
  }

  function collectRanges(query) {
    var ranges = [];
    if (!query) return ranges;

    var walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        var parent = node.parentElement;
        if (!parent) return NodeFilter.FILTER_REJECT;
        if (parent.closest('.mail-attach')) return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      }
    });

    var needle = query.toLowerCase();
    var node;
    while ((node = walker.nextNode())) {
      var data = node.data.toLowerCase();
      var index = data.indexOf(needle);
      while (index >= 0) {
        var range = document.createRange();
        range.setStart(node, index);
        range.setEnd(node, index + query.length);
        ranges.push(range);
        index = data.indexOf(needle, index + query.length);
      }
    }
    return ranges;
  }

  function runSearch(resetIndex) {
    var query = findInput.value;
    if (!query) {
      clearHighlights();
      return;
    }

    matchRanges = collectRanges(query);
    if (resetIndex || matchIndex >= matchRanges.length) matchIndex = matchRanges.length ? 0 : -1;
    renderHighlights();
  }

  function renderHighlights() {
    if (window.CSS && CSS.highlights) {
      if (matchRanges.length) {
        CSS.highlights.set('find-all', new Highlight(...matchRanges));
      } else {
        CSS.highlights.delete('find-all');
      }
      if (matchIndex >= 0 && matchRanges[matchIndex]) {
        CSS.highlights.set('find-current', new Highlight(matchRanges[matchIndex]));
      } else {
        CSS.highlights.delete('find-current');
      }
    }

    findCount.textContent = matchRanges.length
      ? (matchIndex + 1) + '/' + matchRanges.length
      : '0/0';

    focusCurrentMatch();
  }

  function focusCurrentMatch() {
    var range = matchIndex >= 0 ? matchRanges[matchIndex] : null;
    if (!range) return;

    if (!(window.CSS && CSS.highlights)) {
      var sel = window.getSelection();
      sel.removeAllRanges();
      sel.addRange(range);
    }

    var rect = range.getBoundingClientRect();
    var box = editor.getBoundingClientRect();
    if (rect.top < box.top + 24 || rect.bottom > box.bottom - 24) {
      editor.scrollTop += rect.top - box.top - box.height / 2;
    }
  }

  function stepMatch(delta) {
    if (!matchRanges.length) return;
    matchIndex = (matchIndex + delta + matchRanges.length) % matchRanges.length;
    renderHighlights();
  }

  function replaceCurrent() {
    if (matchIndex < 0 || !matchRanges[matchIndex]) return;
    var replacement = replaceInput.value;

    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(matchRanges[matchIndex]);
    document.execCommand('insertText', false, replacement);
    notifyChange(0);

    var query = findInput.value;
    matchRanges = collectRanges(query);
    if (!matchRanges.length) {
      matchIndex = -1;
    } else if (matchIndex >= matchRanges.length) {
      matchIndex = 0;
    }
    renderHighlights();
  }

  function replaceAll() {
    var query = findInput.value;
    if (!query) return;

    var ranges = collectRanges(query);
    if (!ranges.length) return;

    var replacement = replaceInput.value;
    var sel = window.getSelection();
    for (var i = ranges.length - 1; i >= 0; i--) {
      sel.removeAllRanges();
      sel.addRange(ranges[i]);
      document.execCommand('insertText', false, replacement);
    }

    notifyChange(0);
    runSearch(true);
  }

  function openFind() {
    findBar.hidden = false;
    var sel = window.getSelection();
    if (sel && !sel.isCollapsed && inEditor(sel.anchorNode)) {
      var text = sel.toString();
      if (text && text.length <= 80) findInput.value = text;
    }
    findInput.focus();
    findInput.select();
    runSearch(true);
  }

  function closeFind() {
    findBar.hidden = true;
    clearHighlights();
    editor.focus();
  }

  // ---------------- 右键菜单 ----------------

  var ctxRange = null;

  function showContextMenu(x, y) {
    ctxMenu.hidden = false;

    var sel = window.getSelection();
    var hasSelection = sel && !sel.isCollapsed && inEditor(sel.anchorNode);
    ctxMenu.querySelector('[data-ctx="cut"]').disabled = !hasSelection;
    ctxMenu.querySelector('[data-ctx="copy"]').disabled = !hasSelection;

    // 行列操作只在光标位于表格单元格内时出现
    var cell = caretCell();
    ctxMenu.querySelectorAll('[data-table-op]').forEach(function (item) {
      item.hidden = !cell;
    });

    var width = ctxMenu.offsetWidth;
    var height = ctxMenu.offsetHeight;
    ctxMenu.style.left = Math.max(6, Math.min(x, window.innerWidth - width - 6)) + 'px';
    ctxMenu.style.top = Math.max(6, Math.min(y, window.innerHeight - height - 6)) + 'px';
  }

  function hideContextMenu() {
    ctxMenu.hidden = true;
  }

  editor.addEventListener('contextmenu', function (e) {
    e.preventDefault();
    var sel = window.getSelection();
    ctxRange = sel && sel.rangeCount && inEditor(sel.anchorNode)
      ? sel.getRangeAt(0).cloneRange()
      : null;
    showContextMenu(e.clientX, e.clientY);
  });

  function restoreCtxSelection() {
    if (!ctxRange) return;
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(ctxRange);
    editor.focus();
  }

  ctxMenu.addEventListener('mousedown', function (e) {
    if (e.target.closest('button')) e.preventDefault();
  });

  ctxMenu.addEventListener('click', function (e) {
    var button = e.target.closest('button[data-ctx]');
    if (!button || button.disabled) return;

    var action = button.dataset.ctx;
    hideContextMenu();
    restoreCtxSelection();

    if (TABLE_OPS.indexOf(action) >= 0) {
      applyTableOp(action);
      return;
    }

    if (action === 'paste') {
      pasteFromClipboard();
      return;
    }

    if (action === 'selectAll') {
      document.execCommand('selectAll');
      return;
    }

    if (action === 'clearFormat') {
      clearFormat();
      return;
    }

    try {
      document.execCommand(action);
    } catch (err) { /* ignore */ }
    notifyChange();
  });

  // ---------------- 表格行列操作 ----------------

  var TABLE_OPS = ['rowAbove', 'rowBelow', 'colLeft', 'colRight', 'deleteRow', 'deleteCol'];

  function caretCell() {
    var node = ctxRange ? ctxRange.startContainer : null;
    if (!node) return null;

    var el = node.nodeType === 1 ? node : node.parentElement;
    return el && editor.contains(el) ? el.closest('td,th') : null;
  }

  function rowCells(row) {
    return Array.prototype.filter.call(row.children, function (node) {
      return node.tagName === 'TD' || node.tagName === 'TH';
    });
  }

  function blankCell(source) {
    var cell = source.cloneNode(false);
    cell.innerHTML = '<br>';
    return cell;
  }

  function insertRowAt(cell, below) {
    var row = cell.parentElement;
    var clone = row.cloneNode(false);
    rowCells(row).forEach(function (source) {
      clone.appendChild(blankCell(source));
    });
    row.parentNode.insertBefore(clone, below ? row.nextSibling : row);
    return clone;
  }

  function insertColumnAt(cell, right) {
    var index = cell.cellIndex;
    var table = cell.closest('table');

    Array.prototype.forEach.call(table.rows, function (row) {
      var cells = rowCells(row);
      var source = cells[Math.min(index, cells.length - 1)];
      if (!source) return;
      row.insertBefore(blankCell(source), right ? source.nextSibling : source);
    });
  }

  function applyTableOp(op) {
    var cell = caretCell();
    if (!cell) return;

    var table = cell.closest('table');
    var row = cell.parentElement;
    var focusCell = null;

    if (op === 'rowAbove' || op === 'rowBelow') {
      var newRow = insertRowAt(cell, op === 'rowBelow');
      focusCell = rowCells(newRow)[0] || null;
    } else if (op === 'colLeft' || op === 'colRight') {
      insertColumnAt(cell, op === 'colRight');
      focusCell = op === 'colRight' ? (cell.nextElementSibling || cell) : cell;
    } else if (op === 'deleteRow') {
      var sibling = row.nextElementSibling || row.previousElementSibling;
      row.remove();
      focusCell = sibling ? (rowCells(sibling)[0] || null) : null;
    } else if (op === 'deleteCol') {
      var column = cell.cellIndex;
      Array.prototype.forEach.call(table.rows, function (item) {
        var cells = rowCells(item);
        if (cells[column]) cells[column].remove();
      });
      var remaining = rowCells(row);
      focusCell = remaining[Math.max(0, column - 1)] || remaining[0] || null;
    }

    var hasCells = Array.prototype.some.call(table.rows, function (item) {
      return rowCells(item).length > 0;
    });

    if (!hasCells) {
      table.remove();
      if (editor.childNodes.length === 0) {
        var paragraph = document.createElement('p');
        paragraph.innerHTML = '<br>';
        editor.appendChild(paragraph);
        placeCaretAtEnd(paragraph);
      } else {
        placeCaretAtEnd(editor);
      }
    } else if (focusCell && focusCell.isConnected) {
      placeCaretAtEnd(focusCell);
    }

    notifyChange(0);
  }

  async function pasteFromClipboard() {
    try {
      if (navigator.clipboard && navigator.clipboard.read) {
        var items = await navigator.clipboard.read();

        for (var k = 0; k < items.length; k++) {
          var imageType = null;
          for (var t = 0; t < items[k].types.length; t++) {
            if (/^image\//.test(items[k].types[t])) { imageType = items[k].types[t]; break; }
          }
          if (imageType) {
            insertImage(await readAsDataUrl(await items[k].getType(imageType)));
            return;
          }
        }

        for (var i = 0; i < items.length; i++) {
          if (items[i].types.indexOf('text/html') >= 0) {
            var htmlBlob = await items[i].getType('text/html');
            insertSanitizedHtml(await htmlBlob.text());
            return;
          }
        }
        for (var j = 0; j < items.length; j++) {
          if (items[j].types.indexOf('text/plain') >= 0) {
            var textBlob = await items[j].getType('text/plain');
            insertPlainText(await textBlob.text());
            return;
          }
        }
      }

      var text = await navigator.clipboard.readText();
      if (text) insertPlainText(text);
    } catch (err) {
      toast('无法读取剪贴板，请使用 Ctrl+V 粘贴');
    }
  }

  function insertPlainText(text) {
    restoreCtxSelection();
    document.execCommand('insertText', false, text);
    notifyChange();
  }

  function insertSanitizedHtml(html) {
    restoreCtxSelection();
    insertHtmlAtCaret(sanitizeHtml(html));
    notifyChange();
  }

  // ---------------- 粘贴净化 ----------------

  var BLOCKED_TAGS = 'script,style,link,meta,iframe,object,embed,form,input,button,base';

  // Word / Outlook 把排版写在 <style> 里（如 table.MsoTableGrid、p.MsoNormal），
  // 整块丢弃会带走表格边框。这里先把能安全落地的属性内联到元素上，再移除 <style>，
  // 与 TinyMCE / Jodit 的粘贴策略保持一致。
  var INLINE_PROPS =
    /^(border(-[a-z]+)?|border-collapse|font-size|font-family|text-align|color|background-color|padding(-[a-z]+)?|line-height)$/;

  function inlineStyleSheets(doc) {
    var rules = [];

    doc.querySelectorAll('style').forEach(function (node) {
      var css = (node.textContent || '').replace(/<!--|-->/g, '');
      var pattern = /([^{}]+)\{([^{}]*)\}/g;
      var match;

      while ((match = pattern.exec(css))) {
        var declarations = parseDeclarations(match[2]);
        if (!declarations.length) continue;

        match[1].split(',').forEach(function (selector) {
          selector = selector.trim();
          // 只接受 tag / .class / tag.class / #id 这类单层选择器
          if (/^[.#]?[A-Za-z][\w-]*(\.[\w-]+)?$/.test(selector)) {
            rules.push({ selector: selector, declarations: declarations });
          }
        });
      }
    });

    rules.forEach(function (rule) {
      var nodes;
      try {
        nodes = doc.querySelectorAll(rule.selector);
      } catch (e) {
        return;
      }

      nodes.forEach(function (el) {
        rule.declarations.forEach(function (declaration) {
          if (!el.style.getPropertyValue(declaration.name)) {
            el.style.setProperty(declaration.name, declaration.value);
          }
        });
      });
    });
  }

  function parseDeclarations(text) {
    var list = [];

    text.split(';').forEach(function (part) {
      var index = part.indexOf(':');
      if (index < 0) return;

      var name = part.slice(0, index).trim().toLowerCase();
      var value = part.slice(index + 1).trim();
      if (!name || !value || !INLINE_PROPS.test(name)) return;
      if (/expression|javascript:|url\s*\(/i.test(value)) return;

      list.push({ name: name, value: value });
    });

    return list;
  }

  function sanitizeHtml(html) {
    var doc = new DOMParser().parseFromString(html, 'text/html');
    inlineStyleSheets(doc);
    doc.querySelectorAll(BLOCKED_TAGS).forEach(function (node) { node.remove(); });

    doc.querySelectorAll('*').forEach(function (el) {
      Array.prototype.slice.call(el.attributes).forEach(function (attr) {
        var name = attr.name.toLowerCase();
        if (name.indexOf('on') === 0) {
          el.removeAttribute(attr.name);
        } else if ((name === 'href' || name === 'src') && /^\s*javascript:/i.test(attr.value)) {
          el.removeAttribute(attr.name);
        } else if (name === 'class' && !/^(todo-list|todo-text|mail-attach)$/.test(attr.value)) {
          el.removeAttribute(attr.name);
        }
      });
    });

    return doc.body.innerHTML;
  }

  // execCommand('insertHTML') 会保留撤销栈，但会丢掉表格上的 border-collapse，
  // 导致从 Word 粘贴的表格在 Outlook / Foxmail 里变成双线。插入后按差集补回样式。
  function insertHtmlAtCaret(html) {
    var before = captureElements('table');

    var inserted = false;
    try {
      inserted = document.execCommand('insertHTML', false, html);
    } catch (e) {
      inserted = false;
    }

    var tables = newElements('table', before);
    if (!inserted && !tables.length) {
      insertFragmentAtCaret(html);
      tables = newElements('table', before);
    }

    tables.forEach(function (table) {
      if (!table.style.borderCollapse) table.style.borderCollapse = 'collapse';
    });
  }

  // execCommand 不可用时的兜底：直接插入 DOM 片段
  function insertFragmentAtCaret(html) {
    var template = document.createElement('template');
    template.innerHTML = html;
    var fragment = template.content;

    try {
      var sel = window.getSelection();
      var range = sel && sel.rangeCount ? sel.getRangeAt(0) : null;
      if (range && inEditor(range.startContainer)) {
        range.deleteContents();
        var last = fragment.lastChild;
        range.insertNode(fragment);
        if (last) {
          range.setStartAfter(last);
          range.collapse(true);
          sel.removeAllRanges();
          sel.addRange(range);
          savedRange = range.cloneRange();
        }
        return;
      }
    } catch (e) { /* 选区失效时落到末尾插入 */ }

    while (fragment.firstChild) editor.appendChild(fragment.firstChild);
  }

  // 截图 / 复制图片后直接粘贴
  function imageFileFrom(data) {
    var files = data.files;
    if (files && files.length) {
      for (var i = 0; i < files.length; i++) {
        if (/^image\//.test(files[i].type)) return files[i];
      }
    }

    var items = data.items;
    if (items) {
      for (var j = 0; j < items.length; j++) {
        if (items[j].kind === 'file' && /^image\//.test(items[j].type)) return items[j].getAsFile();
      }
    }

    return null;
  }

  function readAsDataUrl(blob) {
    return new Promise(function (resolve, reject) {
      var reader = new FileReader();
      reader.onload = function () { resolve(String(reader.result || '')); };
      reader.onerror = function () { reject(reader.error); };
      reader.readAsDataURL(blob);
    });
  }

  function insertImageBlob(blob) {
    readAsDataUrl(blob).then(function (url) {
      if (url) insertImage(url);
    }).catch(function () { /* 读取失败时忽略本次粘贴 */ });
  }

  editor.addEventListener('paste', function (e) {
    var data = e.clipboardData;
    if (!data) return;

    var imageFile = imageFileFrom(data);
    if (imageFile) {
      e.preventDefault();
      insertImageBlob(imageFile);
      return;
    }

    var html = data.getData('text/html');
    if (!html) return; // 纯文本交给浏览器原生粘贴

    e.preventDefault();
    insertHtmlAtCaret(sanitizeHtml(html));
    notifyChange();
  });

  // ---------------- 导出规范化 ----------------

  var EXPORT_DROP_ATTRS = /^(contenteditable|spellcheck|draggable|tabindex)$/;

  // 邮件客户端会剥离 class 与 <style>，导出前统一做一次兜底清理，
  // 保证写入正文、发送出去的内容与编辑时一致。
  function toEmailHtml() {
    var doc = new DOMParser().parseFromString(editor.innerHTML, 'text/html');

    doc.querySelectorAll('*').forEach(function (el) {
      // Word 命名空间标签（<o:p>、<v:shape>…）在邮件客户端里无意义，保留文本内容后展开
      if (/^[A-Za-z]+:/.test(el.tagName)) {
        var parent = el.parentNode;
        while (el.firstChild) parent.insertBefore(el.firstChild, el);
        parent.removeChild(el);
        return;
      }

      Array.prototype.slice.call(el.attributes).forEach(function (attr) {
        var name = attr.name.toLowerCase();
        // data-signature 是签名块标记，留着它正文才能被重新灌回编辑器后继续替换
        if (name.indexOf('on') === 0 ||
            (name.indexOf('data-') === 0 && name !== 'data-signature') ||
            EXPORT_DROP_ATTRS.test(name)) {
          el.removeAttribute(attr.name);
        } else if ((name === 'class' || name === 'style') && !attr.value.trim()) {
          el.removeAttribute(attr.name);
        }
      });
    });

    // Outlook 画单线的关键：表格必须带 border-collapse:collapse
    doc.querySelectorAll('table').forEach(function (table) {
      if (!table.style.borderCollapse) table.style.borderCollapse = 'collapse';
    });

    return doc.body.innerHTML;
  }

  // ---------------- 工具栏状态 ----------------

  var STATE_COMMANDS = ['bold', 'italic', 'underline', 'strikeThrough',
    'insertUnorderedList', 'insertOrderedList',
    'justifyLeft', 'justifyCenter', 'justifyRight', 'justifyFull'];

  function updateToolbarState() {
    STATE_COMMANDS.forEach(function (cmd) {
      var button = document.querySelector('[data-cmd="' + cmd + '"]');
      if (!button) return;
      var active = false;
      try {
        active = document.queryCommandState(cmd);
      } catch (e) { /* ignore */ }
      button.classList.toggle('active', active);
    });

    var el = selectionElement();
    if (!el) return;
    var computed = getComputedStyle(el);

    var block = 'p';
    try {
      block = (document.queryCommandValue('formatBlock') || 'p').toLowerCase();
    } catch (e) { /* ignore */ }
    if (block === 'div') block = 'p';
    blockFormat.value = ['p', 'h1', 'h2', 'h3', 'blockquote', 'pre'].indexOf(block) >= 0 ? block : 'p';

    var size = Math.round(parseFloat(computed.fontSize));
    if (size > 0 && Array.prototype.some.call(fontSize.options, function (o) {
      return o.value === String(size);
    })) {
      fontSize.value = String(size);
    }

    var family = computed.fontFamily.toLowerCase();
    for (var i = 0; i < fontFamily.options.length; i++) {
      var first = fontFamily.options[i].value.split(',')[0].replace(/['"]/g, '').trim().toLowerCase();
      if (first && family.indexOf(first) >= 0) {
        fontFamily.value = fontFamily.options[i].value;
        break;
      }
    }
  }

  // ---------------- 事件绑定 ----------------

  function bindToolbar() {
    // 按钮：mousedown 阻止默认，避免点击工具栏时丢失正文选区
    document.querySelectorAll('.toolbar button').forEach(function (button) {
      button.addEventListener('mousedown', function (e) { e.preventDefault(); });
    });

    document.querySelectorAll('[data-cmd]').forEach(function (button) {
      button.addEventListener('click', function () {
        exec(button.dataset.cmd);
      });
    });

    document.querySelectorAll('[data-action]').forEach(function (button) {
      button.addEventListener('click', function () {
        handleAction(button.dataset.action, button);
      });
    });

    document.querySelectorAll('.select[data-keep-selection]').forEach(function (select) {
      select.addEventListener('change', function () {
        handleSelect(select);
        editor.focus();
      });
    });

    var foreColor = document.getElementById('foreColor');
    var foreBar = document.getElementById('foreColorBar');
    foreColor.addEventListener('input', function () {
      foreBar.style.background = foreColor.value;
    });
    foreColor.addEventListener('change', function () {
      exec('foreColor', foreColor.value);
    });

    var backColor = document.getElementById('backColor');
    var backBar = document.getElementById('backColorBar');
    backColor.addEventListener('input', function () {
      backBar.style.background = backColor.value;
    });
    backColor.addEventListener('change', function () {
      restoreSelection();
      try {
        document.execCommand('hiliteColor', false, backColor.value);
      } catch (e) {
        document.execCommand('backColor', false, backColor.value);
      }
      editor.focus();
      notifyChange();
    });
  }

  function handleAction(action, anchor) {
    switch (action) {
      case 'undo':
      case 'redo':
        exec(action);
        break;
      case 'clearFormat':
        clearFormat();
        break;
      case 'todo':
        insertTodo();
        break;
      case 'table':
        showPopover(tablePop, anchor);
        document.getElementById('tableRows').focus();
        break;
      case 'link':
        openLinkPopover(anchor);
        break;
      case 'emoji':
        showPopover(emojiPop, anchor);
        break;
      case 'attachment':
        renderAttachments();
        showPopover(attachPop, anchor);
        break;
      case 'image':
        post({ type: 'pickImage' });
        break;
      case 'find':
        openFind();
        break;
    }
  }

  function handleSelect(select) {
    restoreSelection();
    if (select === blockFormat) {
      document.execCommand('formatBlock', false, '<' + select.value + '>');
    } else if (select === fontFamily) {
      document.execCommand('fontName', false, select.value);
    } else if (select === fontSize) {
      applyFontSize(parseInt(select.value, 10) || 14);
    } else if (select === lineHeight) {
      if (select.value) applyBlockStyle('lineHeight', select.value);
    } else if (select === paraSpacing) {
      if (select.value !== '') applyBlockStyle('marginBottom', select.value + 'px');
    }
    notifyChange();
  }

  function openLinkPopover(anchor) {
    var sel = window.getSelection();
    var text = sel && !sel.isCollapsed ? sel.toString() : '';
    var url = '';

    if (sel && sel.anchorNode) {
      var node = sel.anchorNode.nodeType === 1 ? sel.anchorNode : sel.anchorNode.parentElement;
      var link = node ? node.closest('a') : null;
      if (link) url = link.getAttribute('href') || '';
    }

    document.getElementById('linkUrl').value = url;
    document.getElementById('linkText').value = text.length <= 120 ? text : '';
    showPopover(linkPop, anchor);
    document.getElementById('linkUrl').focus();
  }

  document.getElementById('linkOk').addEventListener('click', function () {
    var url = normalizeUrl(document.getElementById('linkUrl').value);
    if (!url) {
      toast('请输入链接地址');
      return;
    }
    insertLink(url, document.getElementById('linkText').value);
    hidePopovers();
  });

  document.getElementById('tableOk').addEventListener('click', function () {
    var rows = Math.min(20, Math.max(1, parseInt(document.getElementById('tableRows').value, 10) || 3));
    var cols = Math.min(10, Math.max(1, parseInt(document.getElementById('tableCols').value, 10) || 3));
    insertTable(rows, cols, document.getElementById('tableHeader').checked);
    hidePopovers();
  });

  document.getElementById('attachAdd').addEventListener('click', function () {
    hidePopovers();
    post({ type: 'pickAttachment' });
  });

  // ---------------- 编辑区事件 ----------------

  function bindEditor() {
    editor.addEventListener('input', function () {
      notifyChange();
      updateWordCount();
      if (!findBar.hidden && findInput.value) runSearch(false);
    });

    editor.addEventListener('blur', function () {
      notifyChange(0);
    });

    editor.addEventListener('dragover', function (e) { e.preventDefault(); });
    editor.addEventListener('drop', function (e) {
      // 只接受编辑器内拖动；外部文件走工具栏的附件 / 图片入口
      e.preventDefault();
    });

    editor.addEventListener('keydown', function (e) {
      var ctrl = e.ctrlKey || e.metaKey;

      if (ctrl && e.key === 'Enter') {
        e.preventDefault();
        notifyChange(0);
        post({ type: 'send' });
        return;
      }
      if (ctrl && !e.shiftKey && (e.key === 'k' || e.key === 'K')) {
        e.preventDefault();
        openLinkPopover(document.querySelector('[data-action="link"]'));
        return;
      }
      if (ctrl && !e.shiftKey && (e.key === 'f' || e.key === 'F')) {
        e.preventDefault();
        openFind();
        return;
      }
      if (ctrl && e.shiftKey && (e.key === 'x' || e.key === 'X')) {
        e.preventDefault();
        exec('strikeThrough');
        return;
      }
      if (ctrl && (e.key === 'y' || e.key === 'Y')) {
        e.preventDefault();
        exec('redo');
        return;
      }

      var li = caretListItem();

      if (e.key === 'Tab') {
        e.preventDefault();
        exec(e.shiftKey ? 'outdent' : 'indent');
        return;
      }

      if (e.key === 'Enter' && !e.shiftKey && isTodoItem(li)) {
        e.preventDefault();
        var next = newTodoItem();
        li.after(next);
        placeCaretAtEnd(next.querySelector('.todo-text'));
        notifyChange(0);
        return;
      }

      if (e.key === 'Backspace' && isTodoItem(li) && li.textContent.trim() === '') {
        e.preventDefault();
        var paragraph = document.createElement('p');
        paragraph.innerHTML = '<br>';
        var list = li.parentElement;
        list.after(paragraph);
        li.remove();
        if (!list.children.length) list.remove();
        placeCaretAtEnd(paragraph);
        notifyChange(0);
      }
    });

    editor.addEventListener('change', function (e) {
      if (e.target && e.target.matches && e.target.matches('input[type="checkbox"]')) {
        var item = e.target.closest('li');
        if (item) item.classList.toggle('done', e.target.checked);
        notifyChange(0);
      }
    });

    // 复选框点击后保持焦点不跳走
    editor.addEventListener('click', function (e) {
      if (e.target && e.target.matches && e.target.matches('input[type="checkbox"]')) {
        e.preventDefault();
        e.target.checked = !e.target.checked;
        var item = e.target.closest('li');
        if (item) item.classList.toggle('done', e.target.checked);
        notifyChange(0);
      }
    });

    ['mouseup', 'keyup'].forEach(function (name) {
      editor.addEventListener(name, updateToolbarState);
    });
  }

  function updateWordCount() {
    var text = (editor.innerText || '').replace(/\s+/g, '');
    wordCount.textContent = text.length + ' 字';
  }

  // ---------------- 查找栏事件 ----------------

  findInput.addEventListener('input', function () { runSearch(true); });
  findInput.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') {
      e.preventDefault();
      stepMatch(e.shiftKey ? -1 : 1);
    } else if (e.key === 'Escape') {
      closeFind();
    }
  });
  document.getElementById('findNext').addEventListener('click', function () { stepMatch(1); });
  document.getElementById('findPrev').addEventListener('click', function () { stepMatch(-1); });
  document.getElementById('replaceOne').addEventListener('click', replaceCurrent);
  document.getElementById('replaceAll').addEventListener('click', replaceAll);
  document.getElementById('findClose').addEventListener('click', closeFind);

  // ---------------- 内容块拖拽排序 ----------------

  var hoverBlock = null;
  var dragBlock = null;
  var dragGhost = null;
  var dragHandle = null;
  var dragOriginNext = null;
  var dragPointerId = null;
  var dragMoved = false;

  function ensureDragHandle() {
    if (dragHandle) return dragHandle;

    dragHandle = document.createElement('div');
    dragHandle.className = 'block-handle';
    dragHandle.title = '拖拽调整内容块顺序';
    dragHandle.textContent = '⠿';
    dragHandle.hidden = true;
    document.body.appendChild(dragHandle);
    dragHandle.addEventListener('pointerdown', startBlockDrag);
    return dragHandle;
  }

  // 找到光标所在的最外层内容块（段落 / 列表 / 表格 / 标题…）
  function topLevelBlock(node) {
    while (node && node !== editor) {
      if (node.parentNode === editor) return node.nodeType === 1 ? node : null;
      node = node.parentNode;
    }
    return null;
  }

  function showDragHandle(block) {
    var rect = block.getBoundingClientRect();
    var host = editor.getBoundingClientRect();
    if (rect.bottom < host.top + 2 || rect.top > host.bottom - 2) {
      hideDragHandle();
      return;
    }

    var handle = ensureDragHandle();
    handle.hidden = false;
    handle.style.top = Math.max(host.top + 2, Math.min(rect.top + 1, host.bottom - 24)) + 'px';
    handle.style.left = (host.left + 4) + 'px';
  }

  function hideDragHandle() {
    if (dragBlock) return;
    hoverBlock = null;
    if (dragHandle) dragHandle.hidden = true;
  }

  editor.addEventListener('mousemove', function (e) {
    if (dragBlock) return;
    if (e.target === editor) return; // 左侧内边距：保留当前手柄，方便移过去拖拽
    var block = topLevelBlock(e.target);
    if (!block) {
      hideDragHandle();
      return;
    }
    hoverBlock = block;
    showDragHandle(block);
  });

  editor.addEventListener('mouseleave', function (e) {
    // 手柄浮在编辑区左侧内边距上，移向手柄时不要把它隐藏掉
    if (!dragHandle || dragHandle.hidden) return;
    var r = dragHandle.getBoundingClientRect();
    if (e.clientX >= r.left - 10 && e.clientX <= r.right + 10 &&
        e.clientY >= r.top - 10 && e.clientY <= r.bottom + 10) {
      return;
    }
    hideDragHandle();
  });
  editor.addEventListener('scroll', hideDragHandle, { passive: true });

  function startBlockDrag(e) {
    if (!hoverBlock || !editor.contains(hoverBlock)) return;
    e.preventDefault();
    e.stopPropagation();

    dragBlock = hoverBlock;
    dragPointerId = e.pointerId;
    dragMoved = false;
    dragOriginNext = dragBlock.nextSibling;

    var rect = dragBlock.getBoundingClientRect();
    dragGhost = document.createElement('div');
    dragGhost.className = 'block-drop';
    dragGhost.style.height = Math.max(8, Math.min(rect.height, 160)) + 'px';
    editor.insertBefore(dragGhost, dragBlock);
    dragBlock.classList.add('block-dragging');

    if (dragHandle) {
      dragHandle.classList.add('active');
      try { dragHandle.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
    }

    document.addEventListener('pointermove', moveBlockDrag);
    document.addEventListener('pointerup', endBlockDrag);
    document.addEventListener('pointercancel', endBlockDrag);
    document.addEventListener('keydown', onBlockDragKey, true);
  }

  function moveBlockDrag(e) {
    if (!dragBlock || !dragGhost) return;
    e.preventDefault();
    dragMoved = true;

    var children = Array.prototype.slice.call(editor.children).filter(function (el) {
      return el !== dragBlock && el !== dragGhost;
    });

    for (var i = 0; i < children.length; i++) {
      var rect = children[i].getBoundingClientRect();
      if (e.clientY < rect.top + rect.height / 2) {
        if (dragGhost.parentNode !== editor || dragGhost.nextSibling !== children[i]) {
          editor.insertBefore(dragGhost, children[i]);
        }
        return;
      }
    }

    if (editor.lastElementChild !== dragGhost) editor.appendChild(dragGhost);
  }

  function endBlockDrag() {
    if (!dragBlock) return;

    var block = dragBlock;
    var ghost = dragGhost;
    var moved = dragMoved;

    cleanupBlockDrag();
    if (ghost && ghost.parentNode) {
      ghost.parentNode.insertBefore(block, ghost);
      ghost.remove();
    }
    block.classList.remove('block-dragging');

    if (moved) {
      updateWordCount();
      notifyChange(0);
      toast('已调整内容块顺序');
    }
  }

  function cancelBlockDrag() {
    if (!dragBlock) return;

    var block = dragBlock;
    var ghost = dragGhost;
    var origin = dragOriginNext;

    cleanupBlockDrag();
    if (ghost && ghost.parentNode) ghost.remove();
    block.classList.remove('block-dragging');
    if (origin && origin.parentNode === editor) editor.insertBefore(block, origin);
  }

  function onBlockDragKey(e) {
    if (e.key === 'Escape') {
      e.preventDefault();
      cancelBlockDrag();
    }
  }

  function cleanupBlockDrag() {
    document.removeEventListener('pointermove', moveBlockDrag);
    document.removeEventListener('pointerup', endBlockDrag);
    document.removeEventListener('pointercancel', endBlockDrag);
    document.removeEventListener('keydown', onBlockDragKey, true);

    if (dragHandle) {
      dragHandle.classList.remove('active');
      if (dragPointerId !== null) {
        try { dragHandle.releasePointerCapture(dragPointerId); } catch (err) { /* ignore */ }
      }
    }

    dragBlock = null;
    dragGhost = null;
    dragOriginNext = null;
    dragPointerId = null;
    dragMoved = false;
  }

  // ---------------- 宿主 API ----------------

  window.editorApi = {
    setHtml: function (html) {
      var next = html || '';
      if (editor.innerHTML === next) return;

      editor.innerHTML = next;
      var hasContent = !!(next && next.replace(/<br\s*\/?>|&nbsp;|\s/g, ''));
      if (hasContent) {
        // 带引用块（回复 / 转发 / 草稿续写）时把光标放到引用上方，方便直接开始写
        if (editor.querySelector('.mc-quote')) placeCaretAtStart(editor);
        else placeCaretAtEnd(editor);
      }

      // 整篇替换会连同签名块一起冲掉：内存里记着当前签名，替换后立刻贴回去，
      // 避免回复 / 转发 / 恢复草稿时「签名消失」。
      if (currentSignature) applySignature(currentSignature);

      updateWordCount();
      updateToolbarState();
    },

    getHtml: function () { return toEmailHtml(); },

    setTheme: function (dark) {
      document.documentElement.classList.toggle('dark', !!dark);
      var foreColor = document.getElementById('foreColor');
      var backColor = document.getElementById('backColor');
      foreColor.value = dark ? '#E9EEF7' : '#131922';
      document.getElementById('foreColorBar').style.background = foreColor.value;
      backColor.value = '#FFF2A8';
      document.getElementById('backColorBar').style.background = backColor.value;
    },

    setFontSize: function (px) {
      editor.style.fontSize = px + 'px';
    },

    // 空正文时的占位提示（写信是「在此输入邮件正文…」，签名页是「在此输入签名…」）
    setPlaceholder: function (text) {
      editor.setAttribute('data-placeholder', String(text == null ? '' : text));
    },

    // 设置 / 替换正文末尾的签名块（切换发件账户时调用，空串表示移除）
    setSignature: function (html) { applySignature(html); },

    setAttachments: function (names) {
      attachments = Array.isArray(names) ? names.slice() : [];
      renderAttachments();
    },

    // 在光标处插入一段 HTML（模板内容块 / 图形化编辑入口），保留撤销栈
    insertHtml: function (html) {
      if (!html) return;
      restoreSelection();
      insertHtmlAtCaret(html);
      editor.focus();
      updateWordCount();
      notifyChange(0);
    },

    // 在光标处插入纯文本（占位符令牌）
    insertText: function (text) {
      if (!text) return;
      restoreSelection();
      var ok = false;
      try {
        ok = document.execCommand('insertText', false, String(text));
      } catch (e) {
        ok = false;
      }
      if (!ok) insertHtmlAtCaret(String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'));
      editor.focus();
      updateWordCount();
      notifyChange(0);
    },

    // 快速文本：在光标处追加一段话术并留出空行，不打断已写内容
    // 换行转换成 <br>，避免 execCommand('insertText') 在 contenteditable 里把 \n 当空白吞掉
    insertQuickText: function (text) {
      var value = String(text == null ? '' : text).replace(/\s+$/, '');
      if (!value) return;
      restoreSelection();
      var html = value.split(/\r?\n/).map(escapeHtml).join('<br>') + '<br><br>';
      insertHtmlAtCaret(html);
      editor.focus();
      updateWordCount();
      notifyChange(0);
    },

    insertImage: function (dataUrl) { insertImage(dataUrl); },
    insertAttachment: function (name) { insertAttachmentTag(name); },
    focusEditor: function () { editor.focus(); },
    requestHtml: function () { post({ type: 'change', html: toEmailHtml() }); }
  };

  // ---------------- 启动 ----------------

  function toast(message) {
    clearTimeout(toastsTimer);
    toastBox.textContent = message;
    toastBox.hidden = false;
    toastsTimer = setTimeout(function () { toastBox.hidden = true; }, 2400);
  }

  function init() {
    try {
      document.execCommand('styleWithCSS', false, true);
    } catch (e) { /* ignore */ }

    buildEmojiGrid();
    bindToolbar();
    bindEditor();
    updateWordCount();
    editor.focus();
    post({ type: 'ready' });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
